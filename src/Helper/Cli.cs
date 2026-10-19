using System;
using System.Collections.Generic;
using ControllerToggle.Helper.Ipc;
using ControllerToggle.Helper.Native;

namespace ControllerToggle.Helper
{
    /// <summary>Command line for diagnostics and recovery. Runs the same code as the service.</summary>
    internal static class Cli
    {
        public static int Run(string[] args)
        {
            string command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "--help";
            Log.FileEnabled = ToggleCore.IsAdministrator();

            try
            {
                switch (command)
                {
                    case "--status":
                        return Status();
                    case "--list":
                        return List();
                    case "--ping":
                        return Ping();
                    case "--enable":
                        return SetState(true);
                    case "--disable":
                        return SetState(false);
                    case "--restore":
                        return Restore();
                    case "--selftest":
                        return SelfTest();
                    case "--help":
                    case "-h":
                    case "/?":
                        PrintHelp();
                        return 0;
                    default:
                        Console.Error.WriteLine("Unknown option: " + args[0]);
                        PrintHelp();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Controller Toggle helper " + BuildInfo.Version);
            Console.WriteLine();
            Console.WriteLine("Normally runs as the 'ControllerToggle' Windows service. Options for troubleshooting:");
            Console.WriteLine("  --status    Show the current state as JSON (no changes).");
            Console.WriteLine("  --list      Show every controller the helper can see and how it was classified.");
            Console.WriteLine("  --ping      Ask the running service for its state (tests the widget's connection path).");
            Console.WriteLine("  --enable    Turn the built-in controller on   (via the service, or directly as admin).");
            Console.WriteLine("  --disable   Turn the built-in controller off  (via the service, or directly as admin).");
            Console.WriteLine("  --restore   Emergency: turn the built-in controller back on and switch auto mode off (admin).");
            Console.WriteLine("  --selftest  Check the native structure layouts and run a scan.");
        }

        private static int Status()
        {
            ScanResult scan = new DeviceScanner().Scan(Settings.Load().TargetInstanceId, true);
            Console.WriteLine(StateJson.Build(scan, Settings.Load(), "", ""));
            return 0;
        }

        private static int Ping()
        {
            string reply = PipeClient.Request("get", 5000);
            if (reply == null)
            {
                Console.Error.WriteLine("The ControllerToggle service didn't answer on \\\\.\\pipe\\" + PipeServer.PipeName + ".");
                Console.Error.WriteLine("Check that it's running:  sc query ControllerToggle");
                return 1;
            }
            Console.WriteLine(reply);
            return 0;
        }

        private static int SetState(bool enable)
        {
            string reply = PipeClient.Request(enable ? "enable" : "disable", 10000);
            if (reply != null)
            {
                Console.WriteLine(reply);
                return 0;
            }

            if (!ToggleCore.IsAdministrator())
            {
                Console.Error.WriteLine("The service isn't running, and changing the controller directly needs an administrator prompt.");
                return 1;
            }

            ScanResult scan = new DeviceScanner().Scan(Settings.Load().TargetInstanceId, false);
            if (scan.BuiltIn == null)
            {
                Console.Error.WriteLine("No built-in controller found. Run --list to see what the helper detects.");
                return 1;
            }

            ChangeResult result = SetupApi.SetEnabled(scan.BuiltIn.Node.InstanceId, enable);
            if (!result.Success)
            {
                Console.Error.WriteLine(result.Message);
                return 1;
            }
            if (result.NeedsRestart)
            {
                string detail;
                bool cycled = UsbPort.TryCycle(scan.BuiltIn.Node.InstanceId, out detail);
                Console.WriteLine((cycled ? "" : "Port cycle failed: ") + detail);
            }
            Console.WriteLine((enable ? "Turned on: " : "Turned off: ") + scan.BuiltIn.DisplayName + " (" + scan.BuiltIn.Node.InstanceId + ")");
            return 0;
        }

        private static int Restore()
        {
            // If the service is running, tell it first so it doesn't immediately undo the restore.
            if (PipeClient.Request("auto off", 5000) != null)
            {
                PipeClient.Request("enable", 10000);
                Console.WriteLine("Asked the running service to turn auto mode off and the controller on.");
            }

            if (!ToggleCore.IsAdministrator())
            {
                Console.Error.WriteLine("Run this from an administrator prompt (or use Restore-Controller.cmd) to restore directly.");
                return 1;
            }
            return ToggleCore.RestoreAll(Console.Out);
        }

        private static int SelfTest()
        {
            Console.WriteLine("Controller Toggle helper " + BuildInfo.Version);
            Console.WriteLine("64-bit process:        " + Environment.Is64BitProcess);
            Console.WriteLine("Administrator:         " + ToggleCore.IsAdministrator());
            Console.WriteLine("SetupAPI layouts:      " + SetupApi.CheckLayouts());
            int filter = DeviceChangeWatcher.FilterSize();
            Console.WriteLine("CM_NOTIFY_FILTER size: " + filter + (filter == 416 ? " (ok)" : " (UNEXPECTED, expected 416)"));
            Console.WriteLine();
            return List();
        }

        private static int List()
        {
            Settings settings = Settings.Load();
            ScanResult scan = new DeviceScanner().Scan(settings.TargetInstanceId, true);

            Console.WriteLine("Built-in controller");
            if (scan.BuiltIn == null)
            {
                Console.WriteLine("  (none found)");
            }
            else
            {
                BuiltInController b = scan.BuiltIn;
                Console.WriteLine("  " + b.DisplayName);
                Console.WriteLine("  Instance:  " + b.Node.InstanceId);
                Console.WriteLine("  State:     " + DeviceScanner.StateName(b) + "  (status 0x" + b.Status.ToString("X8") + ", problem " + b.Problem + ", config flags 0x" + b.ConfigFlags.ToString("X") + ")");
                Console.WriteLine("  Why:       " + b.Reason);
                Console.WriteLine("  Name:      " + (b.Node.Name ?? "-"));
            }
            foreach (string note in scan.Notes)
            {
                Console.WriteLine("  Note: " + note);
            }

            Console.WriteLine();
            Console.WriteLine("XInput devices (" + scan.XInputDevices.Count + ")");
            PrintNodes(scan.XInputDevices, scan);

            Console.WriteLine();
            Console.WriteLine("HID game controllers (" + scan.HidGameDevices.Count + ")");
            PrintNodes(scan.HidGameDevices, scan);

            Console.WriteLine();
            Console.WriteLine("External controllers counted for auto mode: " + scan.Externals.Count);
            foreach (ExternalController e in scan.Externals)
            {
                Console.WriteLine("  - " + e.Name + "  [" + e.Kind + "]  " + e.InstanceId);
            }

            Console.WriteLine();
            Console.WriteLine("Settings: AutoMode=" + settings.AutoMode + ", ManualDisabled=" + settings.ManualDisabled);
            return 0;
        }

        private static void PrintNodes(List<DeviceNode> nodes, ScanResult scan)
        {
            if (nodes.Count == 0)
            {
                Console.WriteLine("  (none)");
                return;
            }
            foreach (DeviceNode node in nodes)
            {
                string role;
                if (scan.BuiltIn != null && string.Equals(scan.BuiltIn.Node.InstanceId, node.InstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    role = "BUILT-IN (managed)";
                }
                else if (node.IsVirtual)
                {
                    role = "virtual (ignored)";
                }
                else if (node.Known != null)
                {
                    role = "part of " + node.Known.Name;
                }
                else if (node.InLocalMachineContainer)
                {
                    role = "part of this PC";
                }
                else if (!node.IsStarted)
                {
                    role = "not running (ignored)";
                }
                else
                {
                    role = "external";
                }

                Console.WriteLine("  - " + (node.Name ?? "(no name)") + "  [" + role + "]");
                Console.WriteLine("      " + node.InstanceId);
                Console.WriteLine("      container " + (node.ContainerId.HasValue ? node.ContainerId.Value.ToString("B") : "-"));
                if (node.Lineage.Count > 1)
                {
                    Console.WriteLine("      parent    " + node.Lineage[1]);
                }
            }
        }
    }
}
