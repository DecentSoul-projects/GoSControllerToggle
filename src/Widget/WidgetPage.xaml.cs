using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace ControllerToggleWidget
{
    /// <summary>
    /// The widget UI. It never touches devices itself: it sends one-word commands to the
    /// ControllerToggle service and renders the JSON state the service sends back.
    /// </summary>
    public sealed partial class WidgetPage : Page
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(10);

        private readonly HelperClient _client = new HelperClient();
        private readonly DispatcherTimer _timer = new DispatcherTimer();

        // Brushes are per page (XAML objects belong to the thread/window that created them).
        private readonly SolidColorBrush _darkBackground = new SolidColorBrush(Color.FromArgb(255, 38, 38, 38));
        private readonly SolidColorBrush _lightBackground = new SolidColorBrush(Color.FromArgb(255, 219, 219, 219));
        private readonly SolidColorBrush _green = new SolidColorBrush(Color.FromArgb(255, 63, 185, 80));
        private readonly SolidColorBrush _amber = new SolidColorBrush(Color.FromArgb(255, 227, 163, 59));
        private readonly SolidColorBrush _red = new SolidColorBrush(Color.FromArgb(255, 229, 83, 75));
        private readonly SolidColorBrush _gray = new SolidColorBrush(Color.FromArgb(255, 138, 138, 138));

        private XboxGameBarWidget _widget;
        private bool _builtInFound;
        private bool _builtInEnabled = true;
        private bool _connecting;
        private bool _shutDown;
        private DateTime? _busySinceUtc;

        public WidgetPage()
        {
            this.InitializeComponent();
            _timer.Interval = PollInterval;
            _timer.Tick += Timer_Tick;
            _client.LineReceived += Client_LineReceived;
            _client.Disconnected += Client_Disconnected;
            this.Loaded += WidgetPage_Loaded;
            this.KeyDown += WidgetPage_KeyDown;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _widget = e.Parameter as XboxGameBarWidget;
            if (_widget != null)
            {
                _widget.RequestedThemeChanged += Widget_RequestedThemeChanged;
                _widget.RequestedOpacityChanged += Widget_RequestedOpacityChanged;
                _widget.VisibleChanged += Widget_VisibleChanged;
            }
            ApplyTheme();
        }

        private void WidgetPage_Loaded(object sender, RoutedEventArgs e)
        {
            FooterText.Text = "Widget " + GetPackageVersionText();
            if (_widget == null || _widget.Visible)
            {
                _timer.Start();
            }
            Refresh();
        }

        /// <summary>Called when the widget window closes.</summary>
        public void Shutdown()
        {
            if (_shutDown)
            {
                return;
            }
            _shutDown = true;
            _timer.Stop();
            if (_widget != null)
            {
                _widget.RequestedThemeChanged -= Widget_RequestedThemeChanged;
                _widget.RequestedOpacityChanged -= Widget_RequestedOpacityChanged;
                _widget.VisibleChanged -= Widget_VisibleChanged;
                _widget = null;
            }
            _client.Release();
        }

        // ---- Game Bar events (raised off the UI thread) ----

        private async void Widget_RequestedThemeChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ApplyTheme);
        }

        private async void Widget_RequestedOpacityChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ApplyTheme);
        }

        private async void Widget_VisibleChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_shutDown)
                {
                    return;
                }
                if (_widget != null && _widget.Visible)
                {
                    _timer.Start();
                    Refresh();
                }
                else
                {
                    _timer.Stop();
                }
            });
        }

        private void ApplyTheme()
        {
            if (_widget == null)
            {
                return;
            }
            this.RequestedTheme = _widget.RequestedTheme;
            BackgroundGrid.Background = _widget.RequestedTheme == ElementTheme.Dark ? _darkBackground : _lightBackground;
            BackgroundGrid.Opacity = _widget.RequestedOpacity;
        }

        // ---- Talking to the helper ----

        private void Timer_Tick(object sender, object e)
        {
            Refresh();
        }

        private async void Refresh()
        {
            if (_shutDown)
            {
                return;
            }
            CheckBusyTimeout();
            if (_client.IsConnected)
            {
                _client.Send("get");
                return;
            }
            await ConnectAsync();
        }

        private async Task ConnectAsync()
        {
            if (_connecting)
            {
                return;
            }
            _connecting = true;
            try
            {
                ConnectResult result = await _client.ConnectAsync();
                if (result == ConnectResult.Connected)
                {
                    _client.Send("get");
                }
                else
                {
                    ShowHelperProblem(result);
                }
            }
            finally
            {
                _connecting = false;
            }
        }

        private async void Client_LineReceived(string line)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ApplyState(line));
        }

        private async void Client_Disconnected()
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (!_shutDown)
                {
                    ShowHelperProblem(ConnectResult.Failed);
                    FooterText.Text = "Lost the connection to the helper. Reconnecting…";
                }
            });
        }

        private void ShowHelperProblem(ConnectResult result)
        {
            _builtInFound = false;
            _busySinceUtc = null;
            SetPill("Offline", _gray);
            DeviceText.Text = "The Controller Toggle helper isn't reachable.";
            MainButton.IsEnabled = false;
            AutoCheckBox.IsEnabled = false;
            ExternalText.Text = "";
            ShowWarning("");

            switch (result)
            {
                case ConnectResult.HelperNotRunning:
                    FooterText.Text = "The helper service isn't running. If Smart App Control is on, it blocks the helper: turn it off in Windows Security. Otherwise start the \"Controller Toggle helper\" service, or run the installer again.";
                    break;
                case ConnectResult.AccessDenied:
                    FooterText.Text = "Windows blocked the connection to the helper (access denied). Run the installer again.";
                    break;
                case ConnectResult.Busy:
                    FooterText.Text = "The helper is busy. Retrying…";
                    break;
                default:
                    FooterText.Text = "Couldn't reach the helper (error " + _client.LastError + "). Retrying…";
                    break;
            }
        }

        private void MainButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_builtInFound)
            {
                return;
            }
            string command = _builtInEnabled ? "disable" : "enable";
            if (!_client.Send(command))
            {
                Refresh();
                return;
            }
            _busySinceUtc = DateTime.UtcNow;
            MainButton.IsEnabled = false;
            SetPill(_builtInEnabled ? "Turning off…" : "Turning on…", _amber);
        }

        private void AutoCheckBox_Click(object sender, RoutedEventArgs e)
        {
            bool on = AutoCheckBox.IsChecked == true;
            if (!_client.Send(on ? "auto on" : "auto off"))
            {
                AutoCheckBox.IsChecked = !on;
                Refresh();
            }
        }

        private void CheckBusyTimeout()
        {
            if (_busySinceUtc.HasValue && DateTime.UtcNow - _busySinceUtc.Value > BusyTimeout)
            {
                _busySinceUtc = null;
                MainButton.IsEnabled = _builtInFound;
                FooterText.Text = "The helper didn't answer. Try again.";
            }
        }

        // ---- Rendering the helper's state ----

        private void ApplyState(string line)
        {
            if (_shutDown)
            {
                return;
            }

            JsonObject state;
            if (!JsonObject.TryParse(line.Replace("﻿", "").Trim(), out state))
            {
                return;
            }

            string type = GetString(state, "type");
            if (type == "error")
            {
                _busySinceUtc = null;
                MainButton.IsEnabled = _builtInFound;
                ShowWarning("Helper error: " + GetString(state, "error"));
                return;
            }
            if (type != "state")
            {
                return;
            }

            _busySinceUtc = null;
            _builtInFound = GetBool(state, "builtInFound", false);
            _builtInEnabled = GetBool(state, "builtInEnabled", true);
            bool autoMode = GetBool(state, "autoMode", false);
            int externalCount = (int)GetNumber(state, "externalCount", 0);
            string externalNames = GetNames(state, "externalNames");
            string message = GetString(state, "message");
            string error = GetString(state, "error");
            string helperVersion = GetString(state, "helperVersion");

            if (!_builtInFound)
            {
                SetPill("Not found", _gray);
                DeviceText.Text = "No built-in controller was recognized on this device.";
                MainButton.Content = "Turn off built-in controller";
                MainButton.IsEnabled = false;
            }
            else
            {
                string name = GetString(state, "builtInName");
                DeviceText.Text = name.Length > 0 ? name : "Built-in controller";
                switch (GetString(state, "builtInState"))
                {
                    case "enabled":
                        SetPill("On", _green);
                        break;
                    case "disabled":
                        SetPill("Off", _amber);
                        break;
                    case "pending-restart":
                        SetPill("Off after restart", _amber);
                        break;
                    case "not-working":
                        SetPill("Problem", _red);
                        break;
                    default:
                        SetPill("Unknown", _gray);
                        break;
                }
                MainButton.Content = _builtInEnabled ? "Turn off built-in controller" : "Turn on built-in controller";
                MainButton.IsEnabled = true;
            }

            AutoCheckBox.IsChecked = autoMode;
            AutoCheckBox.IsEnabled = true;

            if (externalCount == 0)
            {
                ExternalText.Text = "No external controllers connected.";
            }
            else
            {
                string list = externalCount == 1 ? "External controller: " + externalNames : externalCount + " external controllers: " + externalNames;
                if (autoMode && _builtInFound && !_builtInEnabled)
                {
                    list += ". The built-in controller turns back on when " + (externalCount == 1 ? "it disconnects." : "they disconnect.");
                }
                ExternalText.Text = list;
            }

            if (error.Length > 0)
            {
                ShowWarning(error);
            }
            else if (_builtInFound && _builtInEnabled && externalCount == 0 && !autoMode)
            {
                ShowWarning("No other controller is connected. If you turn the built-in one off, use touch or a keyboard to turn it back on.");
            }
            else
            {
                ShowWarning("");
            }

            FooterText.Text = message.Length > 0
                ? message
                : "Helper " + helperVersion + " · widget " + GetPackageVersionText();
        }

        private void SetPill(string text, SolidColorBrush brush)
        {
            StateText.Text = text;
            StateDot.Fill = brush;
        }

        private void ShowWarning(string text)
        {
            WarningText.Text = text ?? "";
            WarningText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---- Gamepad focus movement (D-pad / left stick) ----

        private void WidgetPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            FocusNavigationDirection direction;
            switch (e.Key)
            {
                case VirtualKey.GamepadDPadUp:
                case VirtualKey.GamepadLeftThumbstickUp:
                    direction = FocusNavigationDirection.Up;
                    break;
                case VirtualKey.GamepadDPadDown:
                case VirtualKey.GamepadLeftThumbstickDown:
                    direction = FocusNavigationDirection.Down;
                    break;
                default:
                    return;
            }
            if (FocusManager.TryMoveFocus(direction))
            {
                e.Handled = true;
            }
        }

        // ---- Helpers ----

        private static string GetPackageVersionText()
        {
            try
            {
                PackageVersion v = Package.Current.Id.Version;
                return v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string GetString(JsonObject o, string key)
        {
            IJsonValue value;
            if (o.TryGetValue(key, out value) && value != null && value.ValueType == JsonValueType.String)
            {
                return value.GetString();
            }
            return "";
        }

        private static bool GetBool(JsonObject o, string key, bool fallback)
        {
            IJsonValue value;
            if (o.TryGetValue(key, out value) && value != null && value.ValueType == JsonValueType.Boolean)
            {
                return value.GetBoolean();
            }
            return fallback;
        }

        private static double GetNumber(JsonObject o, string key, double fallback)
        {
            IJsonValue value;
            if (o.TryGetValue(key, out value) && value != null && value.ValueType == JsonValueType.Number)
            {
                return value.GetNumber();
            }
            return fallback;
        }

        private static string GetNames(JsonObject o, string key)
        {
            IJsonValue value;
            if (!o.TryGetValue(key, out value) || value == null || value.ValueType != JsonValueType.Array)
            {
                return "";
            }
            var names = new StringBuilder();
            foreach (IJsonValue item in value.GetArray())
            {
                if (item == null || item.ValueType != JsonValueType.String)
                {
                    continue;
                }
                if (names.Length > 0)
                {
                    names.Append(", ");
                }
                names.Append(item.GetString());
            }
            return names.ToString();
        }
    }
}
