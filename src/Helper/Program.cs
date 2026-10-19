using System;
using System.ServiceProcess;

namespace ControllerToggle.Helper
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // Started by the Service Control Manager: no console, no arguments.
            if (args.Length == 0 && !Environment.UserInteractive)
            {
                ServiceBase.Run(new ToggleService());
                return 0;
            }

            return Cli.Run(args);
        }
    }
}
