using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace ControllerToggle.Helper
{
    /// <summary>
    /// Persistent settings in %ProgramData%\ControllerToggle\settings.ini.
    /// The folder is locked down (SYSTEM/Administrators write, Users read) so a standard
    /// user can't point the service at some other device by editing TargetInstanceId.
    /// </summary>
    internal sealed class Settings
    {
        /// <summary>Turn the built-in controller off while an external controller is connected.</summary>
        public bool AutoMode;

        /// <summary>The user's manual choice (used when AutoMode is off). Persists across reboots.</summary>
        public bool ManualDisabled;

        /// <summary>Instance ID of the built-in controller node we manage (lets us find it again while it's disabled).</summary>
        public string TargetInstanceId = "";

        public static string DataDirectory
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ControllerToggle");
            }
        }

        private static string FilePath
        {
            get { return Path.Combine(DataDirectory, "settings.ini"); }
        }

        public static Settings Load()
        {
            var settings = new Settings();
            try
            {
                if (!File.Exists(FilePath))
                {
                    return settings;
                }

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                string v;
                if (values.TryGetValue("AutoMode", out v))
                {
                    settings.AutoMode = ParseBool(v);
                }
                if (values.TryGetValue("ManualDisabled", out v))
                {
                    settings.ManualDisabled = ParseBool(v);
                }
                if (values.TryGetValue("TargetInstanceId", out v))
                {
                    settings.TargetInstanceId = v;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Couldn't read settings, using defaults: " + ex.Message);
            }
            return settings;
        }

        public void Save()
        {
            try
            {
                EnsureDataDirectory();
                var text = new StringBuilder();
                text.AppendLine("# Controller Toggle helper settings (written by the service)");
                text.AppendLine("AutoMode=" + (AutoMode ? "1" : "0"));
                text.AppendLine("ManualDisabled=" + (ManualDisabled ? "1" : "0"));
                text.AppendLine("TargetInstanceId=" + (TargetInstanceId ?? ""));

                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
                if (File.Exists(FilePath))
                {
                    File.Replace(temp, FilePath, null);
                }
                else
                {
                    File.Move(temp, FilePath);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't save settings: " + ex.Message);
            }
        }

        private static bool ParseBool(string value)
        {
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Creates the data folder with a restrictive ACL (call only when running as SYSTEM/admin).</summary>
        public static void EnsureDataDirectory()
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

            if (!Directory.Exists(DataDirectory))
            {
                Directory.CreateDirectory(DataDirectory, security);
            }
            else
            {
                Directory.SetAccessControl(DataDirectory, security);
            }
        }
    }
}
