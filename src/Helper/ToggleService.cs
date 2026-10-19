using System;
using System.ServiceProcess;

namespace ControllerToggle.Helper
{
    /// <summary>Windows service host (runs as LocalSystem, starts automatically).</summary>
    internal sealed class ToggleService : ServiceBase
    {
        public const string Name = "ControllerToggle";

        private ToggleCore _core;

        public ToggleService()
        {
            ServiceName = Name;
            CanStop = true;
            CanHandlePowerEvent = true;
            CanPauseAndContinue = false;
            // No shutdown handling on purpose: the disabled state is stored by Windows, and the
            // service re-applies its policy at the next start anyway.
            CanShutdown = false;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                Settings.EnsureDataDirectory();
            }
            catch (Exception ex)
            {
                Log.Warn("Couldn't prepare the data folder: " + ex.Message);
            }

            Log.Info("Service starting (helper " + BuildInfo.Version + ")");
            _core = new ToggleCore();
            _core.Start();
        }

        protected override void OnStop()
        {
            Log.Info("Service stopping");
            ToggleCore core = _core;
            _core = null;
            if (core != null)
            {
                core.Stop(true);
            }
            Log.Info("Service stopped");
        }

        protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
        {
            if (powerStatus == PowerBroadcastStatus.ResumeAutomatic || powerStatus == PowerBroadcastStatus.ResumeSuspend)
            {
                ToggleCore core = _core;
                if (core != null)
                {
                    core.RequestEvaluate();
                }
            }
            return true;
        }
    }
}
