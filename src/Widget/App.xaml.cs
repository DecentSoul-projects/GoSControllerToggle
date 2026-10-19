using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace ControllerToggleWidget
{
    /// <summary>
    /// Activation follows Microsoft's XboxGameBarSamples (WidgetSampleCS): Game Bar activates us
    /// with the ms-gamebarwidget protocol, and we must create and hold an XboxGameBarWidget for
    /// each new widget window.
    /// </summary>
    sealed partial class App : Application
    {
        private XboxGameBarWidget _widget;

        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            XboxGameBarWidgetActivatedEventArgs widgetArgs = null;
            if (args.Kind == ActivationKind.Protocol)
            {
                var protocolArgs = args as IProtocolActivatedEventArgs;
                if (protocolArgs != null && protocolArgs.Uri != null && protocolArgs.Uri.Scheme.Equals("ms-gamebarwidget"))
                {
                    widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
                }
            }

            if (widgetArgs == null || !widgetArgs.IsLaunchActivation)
            {
                // Repeat activations for an existing widget window need nothing from us.
                return;
            }

            var rootFrame = new Frame();
            rootFrame.NavigationFailed += OnNavigationFailed;
            Window.Current.Content = rootFrame;

            // Creating the XboxGameBarWidget bootstraps the connection with Game Bar; keep a reference.
            _widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, rootFrame);
            rootFrame.Navigate(typeof(WidgetPage), _widget);

            Window.Current.Closed += WidgetWindow_Closed;
            Window.Current.Activate();
        }

        private void WidgetWindow_Closed(object sender, CoreWindowEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            var page = frame != null ? frame.Content as WidgetPage : null;
            if (page != null)
            {
                page.Shutdown();
            }
            _widget = null;
            Window.Current.Closed -= WidgetWindow_Closed;
        }

        /// <summary>Launched outside Game Bar (for example from Visual Studio): show the same page in a window.</summary>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (!e.PrelaunchActivated)
            {
                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(WidgetPage), null);
                }
                Window.Current.Activate();
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            SuspendingDeferral deferral = e.SuspendingOperation.GetDeferral();
            // Close our pipe connections so the helper doesn't keep zombie clients around
            // (a lesson from GoTweaks: suspended widgets never close their end on their own).
            HelperClient.DisconnectAll();
            _widget = null;
            deferral.Complete();
        }
    }
}
