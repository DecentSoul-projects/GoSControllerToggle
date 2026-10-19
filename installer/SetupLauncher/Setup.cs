using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;

[assembly: AssemblyTitle("Controller Toggle Setup")]
[assembly: AssemblyDescription("Installs the Controller Toggle Xbox Game Bar widget and its helper service.")]
[assembly: AssemblyProduct("Controller Toggle")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ControllerToggle.Setup
{
    /// <summary>
    /// Single-file installer: the build embeds ControllerToggle-Setup.zip as a resource.
    /// The exe's manifest asks for administrator rights up front (one UAC prompt), then it
    /// unpacks the zip to a temp folder, runs Install.ps1 in this console, and cleans up.
    ///
    ///   ControllerToggle-Setup.exe              install (interactive)
    ///   ControllerToggle-Setup.exe -Quiet       install without the final key press
    ///   ControllerToggle-Setup.exe --extract D  only unpack the installer files into folder D
    /// </summary>
    internal static class Program
    {
        private const string PayloadResource = "payload.zip";

        private static int Main(string[] args)
        {
            try
            {
                Console.Title = "Controller Toggle Setup";
            }
            catch
            {
            }

            bool quiet = false;
            string extractTo = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].Trim().ToLowerInvariant();
                if (a == "-quiet" || a == "/quiet" || a == "--quiet")
                {
                    quiet = true;
                }
                else if ((a == "--extract" || a == "/extract") && i + 1 < args.Length)
                {
                    extractTo = args[++i];
                }
            }

            if (extractTo != null)
            {
                try
                {
                    Extract(Path.GetFullPath(extractTo));
                    Console.WriteLine("Extracted the installer files to " + Path.GetFullPath(extractTo));
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Extract failed: " + ex.Message);
                    return 1;
                }
            }

            string temp = Path.Combine(Path.GetTempPath(), "ControllerToggle-Setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Console.WriteLine("Unpacking Controller Toggle...");
                Extract(temp);

                string script = Path.Combine(temp, "Install.ps1");
                if (!File.Exists(script))
                {
                    throw new FileNotFoundException("Install.ps1 is missing from the setup package.");
                }

                var start = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"" + (quiet ? " -Quiet" : ""),
                    WorkingDirectory = temp,
                    UseShellExecute = false
                };

                using (Process process = Process.Start(start))
                {
                    process.WaitForExit();
                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("Setup failed: " + ex.Message);
                Console.ResetColor();
                if (!quiet)
                {
                    Console.WriteLine();
                    Console.WriteLine("Press Enter to close");
                    Console.ReadLine();
                }
                return 1;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(temp))
                    {
                        Directory.Delete(temp, true);
                    }
                }
                catch
                {
                    // Leftovers in %TEMP% are harmless.
                }
            }
        }

        private static void Extract(string destination)
        {
            Directory.CreateDirectory(destination);
            string root = destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource))
            {
                if (payload == null)
                {
                    throw new InvalidOperationException("This setup file is damaged (no installer payload inside).");
                }

                using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            continue; // folder entry
                        }

                        string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // never write outside the destination folder
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (Stream source = entry.Open())
                        using (FileStream output = File.Create(target))
                        {
                            source.CopyTo(output);
                        }
                    }
                }
            }
        }
    }
}
