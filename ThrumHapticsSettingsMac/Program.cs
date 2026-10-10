namespace ThrumHapticsSettingsMac
{
    using System;
    using System.Threading;

    using Avalonia;
    using Avalonia.Controls;
    using Avalonia.Controls.ApplicationLifetimes;
    using Avalonia.Themes.Fluent;

    using ThrumHapticsSettings;

    /// <summary>
    /// Entry point for the macOS settings window.
    /// </summary>
    /// <remarks>
    /// Mirrors the Windows Program.cs: take the pipe name from the plugin, refuse
    /// to open twice, connect before showing anything, and fail loudly rather than
    /// presenting an empty window if the plugin cannot be reached.
    /// </remarks>
    internal static class Program
    {
        /// <summary>
        /// Measures where the launch time goes. Six lines, only on open.
        /// </summary>
        /// <remarks>
        /// Started at entry, so it misses whatever the OS and the .NET host spend
        /// BEFORE managed code runs - which on a framework-dependent app is a real
        /// share of the total. The plugin logs the launch moment on its side, so
        /// the difference between that and `entered` is exactly the pre-main cost.
        ///
        /// Written to stdout because the plugin captures it into the plugin log,
        /// where anyone reading a startup complaint is already looking.
        /// </remarks>
        private static readonly System.Diagnostics.Stopwatch Clock =
            System.Diagnostics.Stopwatch.StartNew();

        // internal, not private: App is a sibling class rather than a nested one.
        internal static void Mark(String stage) =>
            Console.WriteLine($"[ThrumHaptics][startup] {stage} at {Clock.ElapsedMilliseconds}ms");

        private static void Main(String[] args)
        {
            Mark("entered Main");

            // The pipe name is passed by the plugin, so both sides agree on it
            // without either hard-coding a value the other might not share.
            var pipeName = args.Length > 0 ? args[0] : "ThrumHaptics.Settings." + Environment.UserName;

            // Single instance: pressing the bound action again must not stack up
            // copies that disagree about state. The plugin's pipe server accepts
            // ONE connection at a time, so a second window could not read settings
            // anyway - it would show the "cannot reach the plugin" path and look
            // like a fault rather than a duplicate.
            //
            // Unlike Windows there is no FindWindow to raise the existing window
            // with, so a second copy can only exit. The plugin covers the usual
            // case before it gets this far: it remembers the process it launched
            // and asks macOS to bring that forward instead (MacAppActivator). This
            // guard remains for a copy started some other way, such as a Smart
            // Action pointing straight at the executable.
            using var singleInstance = new Mutex(true, "ThrumHapticsSettingsMac", out var isFirst);

            if (!isFirst)
            {
                Console.WriteLine("settings window already open; not opening a second one");
                return;
            }

            var client = SettingsClient.TryConnect(pipeName);

            if (client == null)
            {
                // Written to stderr rather than shown in a dialog: the plugin
                // captures both streams, so this lands in the plugin log where
                // anyone diagnosing it is already looking.
                Console.Error.WriteLine(
                    "Could not reach the Thrum Haptics plugin over pipe '" + pipeName
                    + "'. Is Logi Options+ running?");

                return;
            }

            Mark("pipe connected");

            try
            {
                App.Client = client;
                App.Values = client.GetAll();

                Mark("settings read");

                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                client.Dispose();
            }
        }

        private static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // Launched by a background service rather than by the user, so
                // without a Dock presence the window can open behind whatever they
                // were looking at, with no way to bring it forward.
                .With(new MacOSPlatformOptions { ShowInDock = true });
    }

    internal sealed class App : Application
    {
        // Avalonia constructs this itself and requires a parameterless
        // constructor, so the connection is handed over statically.
        public static SettingsClient Client;

        public static System.Collections.Generic.Dictionary<String, (Boolean Enabled, String Waveform, Int32 Density)> Values;

        public override void Initialize() =>
            // Fluent follows the system light/dark appearance, so the window
            // matches macOS without us tracking the theme ourselves.
            this.Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                Program.Mark("Avalonia initialised");

                // After Avalonia, before the window: AppKit has a shared
                // NSApplication by now, and the Dock tile appears as the window
                // opens rather than changing under the user a moment later.
                MacDockIcon.Apply();

                var window = new SettingsWindow(Client, Values);

                Program.Mark("window constructed");

                window.Opened += (_, _) =>
                {
                    window.Activate();
                    Program.Mark("window shown");
                };

                desktop.MainWindow = window;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
