using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Threading;
using ControllerToggle.Helper.Ipc;
using ControllerToggle.Helper.Native;

namespace ControllerToggle.Helper
{
    /// <summary>
    /// The helper's brain: decides whether the built-in controller should be on or off,
    /// makes it so, and answers the widget over the named pipe.
    ///
    /// Rules
    /// - Auto mode off: the user's manual choice applies and persists (like Device Manager / HHT).
    /// - Auto mode on: the controller turns off when an external controller connects and back on
    ///   when the last one disconnects. A manual toggle overrides until the next connect/disconnect.
    /// - Stopping the service (uninstall, update, "sc stop") turns the controller back on.
    /// </summary>
    internal sealed class ToggleCore : IDisposable
    {
        private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MessageLifetime = TimeSpan.FromSeconds(20);

        private readonly object _sync = new object();
        private readonly object _broadcastGate = new object();
        private readonly Settings _settings;
        private readonly DeviceScanner _scanner = new DeviceScanner();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly ManualResetEvent _stopping = new ManualResetEvent(false);

        private PipeServer _pipe;
        private DeviceChangeWatcher _watcher;
        private Thread _worker;
        private bool _watcherOk;

        private readonly AutoPolicy _auto = new AutoPolicy();

        // Feedback for the widget.
        private string _message = "";
        private DateTime _messageUtc = DateTime.MinValue;
        private string _error = "";

        private DateTime _lastFailureUtc = DateTime.MinValue;
        private string _lastFailureTarget = "";
        private DateTime _lastCycleUtc = DateTime.MinValue;
        private string _lastBroadcast = "";

        public ToggleCore()
        {
            _settings = Settings.Load();
        }

        public void Start()
        {
            Log.Info("Settings: AutoMode=" + _settings.AutoMode + ", ManualDisabled=" + _settings.ManualDisabled + ", Target=" + _settings.TargetInstanceId);

            _pipe = new PipeServer(HandleCommand);
            _pipe.Start();

            _watcher = new DeviceChangeWatcher(RequestEvaluate);
            _watcherOk = _watcher.Start(InterfaceGuids.XUsb, InterfaceGuids.Hid);
            if (!_watcherOk)
            {
                Log.Warn("Device notifications unavailable; falling back to checking every 3 seconds.");
            }

            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "ControllerToggle.Worker" };
            _worker.Start();
        }

        public void RequestEvaluate()
        {
            _wake.Set();
        }

        private void WorkerLoop()
        {
            SafeEvaluate("service start");

            int periodicMs = _watcherOk ? 30000 : 3000;
            var handles = new WaitHandle[] { _stopping, _wake };
            while (true)
            {
                int which = WaitHandle.WaitAny(handles, periodicMs);
                if (which == 0)
                {
                    return;
                }

                string reason = "periodic check";
                if (which == 1)
                {
                    // Plugging a controller in fires a burst of notifications; let it settle.
                    do
                    {
                        if (_stopping.WaitOne(700))
                        {
                            return;
                        }
                    }
                    while (_wake.WaitOne(0));
                    reason = "device change";
                }
                SafeEvaluate(reason);
            }
        }

        private void SafeEvaluate(string reason)
        {
            string json = null;
            try
            {
                lock (_sync)
                {
                    json = EvaluateLocked(reason, false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Evaluation failed: " + ex);
            }
            if (json != null)
            {
                BroadcastIfChanged(json);
            }
        }

        /// <summary>Handles one line from a pipe client and returns the JSON state to send back.</summary>
        private string HandleCommand(string line)
        {
            string command = (line ?? "").Trim().ToLowerInvariant();
            string json;
            try
            {
                lock (_sync)
                {
                    switch (command)
                    {
                        case "get":
                        case "status":
                        case "ping":
                            json = BuildStateJson(_scanner.Scan(_settings.TargetInstanceId, true));
                            break;

                        case "enable":
                        case "on":
                            Log.Info("Request: turn the built-in controller on");
                            _settings.ManualDisabled = false;
                            _settings.Save();
                            _auto.Override(false);
                            SetMessage("Built-in controller turned on.");
                            json = EvaluateLocked("turned on from the widget", true);
                            break;

                        case "disable":
                        case "off":
                            Log.Info("Request: turn the built-in controller off");
                            _settings.ManualDisabled = true;
                            _settings.Save();
                            _auto.Override(true);
                            SetMessage("Built-in controller turned off.");
                            json = EvaluateLocked("turned off from the widget", true);
                            break;

                        case "auto on":
                            Log.Info("Request: auto mode on");
                            _settings.AutoMode = true;
                            _settings.Save();
                            _auto.Reset();
                            SetMessage("Auto mode is on.");
                            json = EvaluateLocked("auto mode turned on", true);
                            break;

                        case "auto off":
                            Log.Info("Request: auto mode off");
                            _settings.AutoMode = false;
                            _settings.Save();
                            SetMessage("Auto mode is off.");
                            json = EvaluateLocked("auto mode turned off", true);
                            break;

                        default:
                            SetMessage("Unknown command: " + (command.Length > 40 ? command.Substring(0, 40) : command));
                            json = BuildStateJson(_scanner.Scan(_settings.TargetInstanceId, true));
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Command '" + command + "' failed: " + ex);
                return new JsonWriter().Add("type", "error").Add("error", ex.Message).ToString();
            }

            BroadcastIfChanged(json);
            return json;
        }

        private string EvaluateLocked(string reason, bool userInitiated)
        {
            ScanResult scan = _scanner.Scan(_settings.TargetInstanceId, true);
            RememberTarget(scan);

            bool desiredDisabled = ComputeDesiredDisabled(scan);
            bool acted = Enforce(scan, desiredDisabled, userInitiated, reason);
            if (acted)
            {
                // Give PnP a moment to settle, then report what actually happened.
                Thread.Sleep(400);
                scan = _scanner.Scan(_settings.TargetInstanceId, true);
                RememberTarget(scan);
            }
            return BuildStateJson(scan);
        }

        private bool ComputeDesiredDisabled(ScanResult scan)
        {
            string transition;
            bool disabled = _auto.ShouldBeDisabled(_settings.AutoMode, _settings.ManualDisabled, scan.Externals.Count > 0, out transition);
            if (transition == "connected")
            {
                Log.Info("Auto mode: external controller connected");
                SetMessage("External controller connected, so the built-in controller was turned off.");
            }
            else if (transition == "disconnected")
            {
                Log.Info("Auto mode: last external controller disconnected");
                SetMessage("External controller disconnected, so the built-in controller was turned back on.");
            }
            return disabled;
        }

        /// <summary>Brings the built-in controller to the desired state. Returns true if it changed anything.</summary>
        private bool Enforce(ScanResult scan, bool desiredDisabled, bool userInitiated, string reason)
        {
            BuiltInController b = scan.BuiltIn;
            if (b == null)
            {
                return false;
            }

            bool needsChange = desiredDisabled ? !b.DisabledFlag : b.DisabledFlag;
            if (!needsChange)
            {
                // Windows accepted the disable but the device is still running (an app had it open
                // and vetoed the stop). Cycle the USB port, at most once a minute.
                if (desiredDisabled && b.State == BuiltInState.PendingRestart && DateTime.UtcNow - _lastCycleUtc > CycleInterval)
                {
                    _lastCycleUtc = DateTime.UtcNow;
                    CycleOrReport(b, true);
                    return true;
                }
                if (b.State != BuiltInState.PendingRestart && _error.StartsWith("Windows needs a restart", StringComparison.Ordinal))
                {
                    _error = "";
                }
                return false;
            }

            if (!userInitiated && _lastFailureTarget == b.Node.InstanceId && DateTime.UtcNow - _lastFailureUtc < FailureBackoff)
            {
                return false;
            }

            if (!IsAdministrator())
            {
                _error = "The helper isn't running with administrator rights, so it can't change the controller.";
                return false;
            }

            Log.Info((desiredDisabled ? "Disabling " : "Enabling ") + b.Node.InstanceId + " (" + reason + ")");
            ChangeResult result = SetupApi.SetEnabled(b.Node.InstanceId, !desiredDisabled);
            if (!result.Success && DisabledFlagIs(b, desiredDisabled))
            {
                // Seen on the Legion Go S: disabling returns ERROR_INVALID_DATA after ~5 s, yet Windows
                // has already recorded the new state; the device just couldn't be stopped live (it's
                // in use). Treat it like "needs restart" and finish the job with a port cycle.
                Log.Warn("Windows reported \"" + result.Message + "\", but the change was recorded; finishing it with a USB port cycle.");
                result = new ChangeResult { Success = true, NeedsRestart = true, Message = "" };
            }
            if (!result.Success)
            {
                _lastFailureUtc = DateTime.UtcNow;
                _lastFailureTarget = b.Node.InstanceId;
                _error = "Couldn't turn the controller " + (desiredDisabled ? "off" : "on") + ": " + result.Message;
                Log.Error(_error);
                return true;
            }

            _error = "";
            _lastFailureTarget = "";
            if (result.NeedsRestart)
            {
                Log.Info("Windows says the change needs a restart (the controller is in use); cycling its USB port.");
                _lastCycleUtc = DateTime.UtcNow;
                CycleOrReport(b, desiredDisabled);
            }
            return true;
        }

        /// <summary>Whether Windows' stored "disabled" flag for the controller now equals <paramref name="disabled"/>.</summary>
        private static bool DisabledFlagIs(BuiltInController b, bool disabled)
        {
            uint? flags = CfgMgr.GetUInt32Property(b.Node.DevInst, DevPropKeys.ConfigFlags);
            return flags.HasValue && ((flags.Value & CfgMgr.CONFIGFLAG_DISABLED) != 0) == disabled;
        }

        private void CycleOrReport(BuiltInController b, bool turningOff)
        {
            string detail;
            if (UsbPort.TryCycle(b.Node.InstanceId, out detail))
            {
                Log.Info(detail);
            }
            else
            {
                Log.Warn("USB port cycle failed: " + detail);
                _error = "Windows needs a restart to finish turning the controller " + (turningOff ? "off" : "on") + " (an app is holding it open).";
            }
        }

        private void RememberTarget(ScanResult scan)
        {
            if (scan.BuiltIn == null)
            {
                return;
            }
            string id = scan.BuiltIn.Node.InstanceId;
            if (!string.Equals(id, _settings.TargetInstanceId, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("Built-in controller: " + scan.BuiltIn.DisplayName + " = " + id + " (" + scan.BuiltIn.Reason + ")");
                _settings.TargetInstanceId = id;
                _settings.Save();
            }
        }

        private void SetMessage(string message)
        {
            _message = message ?? "";
            _messageUtc = DateTime.UtcNow;
        }

        private string BuildStateJson(ScanResult scan)
        {
            string message = DateTime.UtcNow - _messageUtc < MessageLifetime ? _message : "";
            return StateJson.Build(scan, _settings, message, _error);
        }

        private void BroadcastIfChanged(string json)
        {
            PipeServer pipe = _pipe;
            if (pipe == null)
            {
                return;
            }
            lock (_broadcastGate)
            {
                if (json == _lastBroadcast)
                {
                    return;
                }
                _lastBroadcast = json;
                pipe.Broadcast(json);
            }
        }

        public void Stop(bool restoreController)
        {
            _stopping.Set();
            try
            {
                if (_watcher != null)
                {
                    _watcher.Dispose();
                }
            }
            catch
            {
            }
            try
            {
                if (_pipe != null)
                {
                    _pipe.Dispose();
                }
            }
            catch
            {
            }
            try
            {
                if (_worker != null)
                {
                    _worker.Join(3000);
                }
            }
            catch
            {
            }

            if (restoreController)
            {
                lock (_sync)
                {
                    try
                    {
                        ScanResult scan = _scanner.Scan(_settings.TargetInstanceId, false);
                        if (scan.BuiltIn != null && scan.BuiltIn.DisabledFlag)
                        {
                            Log.Info("Service stopping: turning the built-in controller back on.");
                            ChangeResult result = SetupApi.SetEnabled(scan.BuiltIn.Node.InstanceId, true);
                            if (!result.Success)
                            {
                                Log.Error(result.Message);
                            }
                            else if (result.NeedsRestart)
                            {
                                string detail;
                                UsbPort.TryCycle(scan.BuiltIn.Node.InstanceId, out detail);
                                Log.Info(detail);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Restoring the controller on stop failed: " + ex.Message);
                    }
                }
            }
        }

        public void Dispose()
        {
            Stop(false);
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                        || identity.User != null && identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Emergency restore: turns every built-in controller node we know about back on and
        /// switches the helper to "manual, on". Used by --restore, Restore-Controller.cmd and
        /// the uninstaller. Needs administrator rights.
        /// </summary>
        public static int RestoreAll(TextWriter output)
        {
            Settings settings = Settings.Load();
            var ids = new List<string>();
            ScanResult scan = new DeviceScanner().Scan(settings.TargetInstanceId, false);
            if (scan.BuiltIn != null)
            {
                ids.Add(scan.BuiltIn.Node.InstanceId);
            }
            foreach (string id in DeviceScanner.KnownGamepadNodes())
            {
                AddUnique(ids, id);
            }
            if (!string.IsNullOrEmpty(settings.TargetInstanceId))
            {
                AddUnique(ids, settings.TargetInstanceId);
            }

            int failures = 0;
            int changed = 0;
            foreach (string id in ids)
            {
                DeviceNode node = DeviceScanner.Describe(id);
                if (node == null)
                {
                    continue;
                }
                BuiltInController b = DeviceScanner.DescribeBuiltIn(node, node.Known != null ? node.Known.Name : "controller", "restore");
                if (!b.DisabledFlag)
                {
                    output.WriteLine("Already on:  " + id);
                    continue;
                }
                ChangeResult result = SetupApi.SetEnabled(id, true);
                if (!result.Success)
                {
                    failures++;
                    output.WriteLine("FAILED:      " + id + " - " + result.Message);
                    continue;
                }
                changed++;
                output.WriteLine("Turned on:   " + id);
                if (result.NeedsRestart)
                {
                    string detail;
                    UsbPort.TryCycle(id, out detail);
                    output.WriteLine("             " + detail);
                }
            }

            if (ids.Count == 0)
            {
                output.WriteLine("No built-in controller found.");
            }

            settings.ManualDisabled = false;
            settings.AutoMode = false;
            try
            {
                Settings.EnsureDataDirectory();
                settings.Save();
            }
            catch
            {
            }

            Log.Info("Restore: " + changed + " turned on, " + failures + " failed.");
            return failures == 0 ? 0 : 1;
        }

        private static void AddUnique(List<string> ids, string id)
        {
            foreach (string existing in ids)
            {
                if (string.Equals(existing, id, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            ids.Add(id);
        }
    }

    internal static class StateJson
    {
        public static string Build(ScanResult scan, Settings settings, string message, string error)
        {
            BuiltInController b = scan.BuiltIn;
            var names = new List<string>();
            foreach (ExternalController e in scan.Externals)
            {
                names.Add(e.Name);
            }

            return new JsonWriter()
                .Add("type", "state")
                .Add("helperVersion", BuildInfo.Version)
                .Add("builtInFound", b != null)
                .Add("builtInName", b != null ? b.DisplayName : "")
                .Add("builtInId", b != null ? b.Node.InstanceId : "")
                .Add("builtInState", DeviceScanner.StateName(b))
                .Add("builtInEnabled", b == null || !b.DisabledFlag)
                .Add("builtInReason", b != null ? b.Reason : "")
                .Add("autoMode", settings.AutoMode)
                .Add("manualDisabled", settings.ManualDisabled)
                .Add("externalCount", scan.Externals.Count)
                .Add("externalNames", names)
                .Add("message", message ?? "")
                .Add("error", error ?? "")
                .ToString();
        }
    }
}
