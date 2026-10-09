namespace Loupedeck.ThrumHapticsPlugin
{
    using System;
    using System.Collections.Generic;

    using Loupedeck.ThrumHapticsPlugin.Config;
    using Loupedeck.ThrumHapticsPlugin.Haptics;
    using Loupedeck.ThrumHapticsPlugin.Input;
    using Loupedeck.ThrumHapticsPlugin.SettingsUi;

    // This class contains the plugin-level logic of the Loupedeck plugin.

    public class ThrumHapticsPlugin : Plugin
    {
        // Gets a value indicating whether this is an API-only plugin.
        public override Boolean UsesApplicationApiOnly => true;

        // Gets a value indicating whether this is a Universal plugin or an Application plugin.
        // MUST stay true: a Universal plugin is not tied to a focused application,
        // which is what lets our haptics fire system-wide.
        public override Boolean HasNoApplication => true;

        private HapticOutput _haptics;
        private readonly List<IInputSource> _inputSources = new();

        /// <summary>
        /// Per-button haptic configuration. Exposed so bindable actions in
        /// Options+ can read and change it - that is the whole config surface,
        /// deliberately, instead of a companion app.
        /// </summary>
        internal HapticSettings Settings { get; private set; }

        private SettingsServer _settingsServer;

        /// <summary>The settings application this plugin launched, while it runs.</summary>
        private System.Diagnostics.Process _settingsProcess;

        private readonly Object _settingsProcessGate = new();

        /// <summary>
        /// Launches the bundled settings application.
        /// </summary>
        /// <remarks>
        /// The executable sits beside this assembly inside the plugin package. If
        /// it is already running, a second press brings that window forward rather
        /// than opening another: on Windows the application does that itself, and
        /// on macOS, where it cannot, the plugin does it for it.
        /// </remarks>
        internal void ShowSettingsWindow()
        {
            try
            {
                if (this.TryRaiseRunningSettingsWindow())
                {
                    return;
                }

                // Use the SDK's AssemblyFilePath, NOT Assembly.Location. The Plugin
                // Service loads plugins into a collectible load context, which
                // leaves Location empty - GetDirectoryName then returns null and
                // Path.Combine throws.
                var assemblyPath = this.AssemblyFilePath;

                var pluginDir = String.IsNullOrEmpty(assemblyPath)
                    ? null
                    : System.IO.Path.GetDirectoryName(assemblyPath);

                if (String.IsNullOrEmpty(pluginDir))
                {
                    PluginLog.Error("[ThrumHaptics] Could not determine the plugin directory.");
                    return;
                }

                // Separate executables per platform. The Windows one is WinForms and
                // cannot run on macOS; the macOS one is built for osx-arm64 and has
                // no .exe suffix.
                var exePath = System.IO.Path.Combine(
                    pluginDir,
                    OperatingSystem.IsMacOS() ? "ThrumHapticsSettingsMac" : "ThrumHapticsSettings.exe");

                if (!System.IO.File.Exists(exePath))
                {
                    PluginLog.Error($"[ThrumHaptics] Settings application not found at '{exePath}'.");
                    return;
                }

                EnsureExecutable(exePath);

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = SettingsServer.PipeName,
                    UseShellExecute = false,
                    WorkingDirectory = pluginDir,
                };

                // The settings application's output is captured PERMANENTLY on macOS.
                // This was once marked as probe code to delete "once the launch path
                // is proven" - and the path then failed for the first user without
                // .NET installed, where the only evidence anywhere was a line the
                // .NET host wrote to stderr. On macOS that host shows no dialog, so
                // this capture is the difference between a diagnosable report and
                // "it does nothing".
                //
                // Windows is left alone: its host reports a missing runtime in a
                // dialog of its own, and redirecting must not get in the way of that.
                // Read asynchronously so a UI process that never exits cannot block us.
                if (OperatingSystem.IsMacOS())
                {
                    startInfo.RedirectStandardOutput = true;
                    startInfo.RedirectStandardError = true;
                }

                // Started BEFORE Process.Start so the settings app's own "entered
                // Main at 0ms" can be placed on this clock. The gap between them is
                // the pre-main cost - .NET host startup, which the app cannot
                // measure from inside itself.
                var launchClock = System.Diagnostics.Stopwatch.StartNew();

                var process = System.Diagnostics.Process.Start(startInfo);

                if (process == null)
                {
                    PluginLog.Error("[ThrumHaptics] Settings application did not start (no process returned).");
                    return;
                }

                // Read now: the exit handler disposes the process, and a settings
                // application that dies instantly can get there before the log line.
                var processId = process.Id;
                var hostReportedMissingRuntime = false;

                if (OperatingSystem.IsMacOS())
                {
                    process.OutputDataReceived += (_, e) =>
                    {
                        if (!String.IsNullOrWhiteSpace(e.Data))
                        {
                            PluginLog.Info(
                                $"[ThrumHaptics] settings-app stdout (+{launchClock.ElapsedMilliseconds}ms): {e.Data}");
                        }
                    };

                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (!String.IsNullOrWhiteSpace(e.Data))
                        {
                            if (IsMissingRuntimeMessage(e.Data))
                            {
                                hostReportedMissingRuntime = true;
                            }

                            PluginLog.Error($"[ThrumHaptics] settings-app stderr: {e.Data}");
                        }
                    };

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }

                lock (this._settingsProcessGate)
                {
                    this._settingsProcess = process;
                }

                // Handler BEFORE the switch that enables it: a process that has
                // already died by this line would otherwise raise Exited to nobody.
                process.Exited += (_, _) =>
                    this.OnSettingsProcessExited(process, launchClock, () => hostReportedMissingRuntime);

                process.EnableRaisingEvents = true;

                PluginLog.Info($"[ThrumHaptics] Settings application launched (pid {processId}).");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[ThrumHaptics] Failed to launch settings application: {ex.Message}");
            }
        }

        /// <summary>
        /// Brings an already-open settings window forward. macOS only.
        /// </summary>
        /// <remarks>
        /// Returns true when there was a running instance to raise, so the caller
        /// does not launch another. On Windows this always returns false: the
        /// settings application there finds and raises its own window, and that
        /// already works.
        /// </remarks>
        private Boolean TryRaiseRunningSettingsWindow()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return false;
            }

            System.Diagnostics.Process running;

            lock (this._settingsProcessGate)
            {
                running = this._settingsProcess;
            }

            if (running == null)
            {
                return false;
            }

            try
            {
                if (running.HasExited)
                {
                    return false;
                }

                var raised = MacAppActivator.TryActivate(running.Id);

                PluginLog.Info(
                    $"[ThrumHaptics] Settings application already running (pid {running.Id}); "
                    + (raised ? "brought forward." : "macOS declined to bring it forward."));

                return true;
            }
            catch (Exception)
            {
                // Exited and disposed between the check and the call. Launch afresh.
                return false;
            }
        }

        /// <summary>Records how the settings application ended, and why if it is known.</summary>
        /// <remarks>
        /// Before this existed a launch left exactly one line - "launched (pid N)" -
        /// whether the window then opened, crashed, or was never allowed to run. An
        /// exit code turns each of those into something a log can answer.
        /// </remarks>
        private void OnSettingsProcessExited(
            System.Diagnostics.Process process,
            System.Diagnostics.Stopwatch launchClock,
            Func<Boolean> hostReportedMissingRuntime)
        {
            try
            {
                // The parameterless overload also waits for the redirected streams
                // to drain. Exited can arrive before the last stderr line has been
                // read, and that line is the one naming a missing runtime.
                process.WaitForExit();

                var exitCode = process.ExitCode;

                PluginLog.Info(
                    $"[ThrumHaptics] Settings application exited with code {exitCode} "
                    + $"after {launchClock.ElapsedMilliseconds}ms.");

                if (hostReportedMissingRuntime() || IsMissingRuntimeExitCode(exitCode))
                {
                    PluginLog.Error(
                        "[ThrumHaptics] The settings window could not start because the .NET runtime is not "
                        + "installed. Haptics are unaffected. Install "
                        + (OperatingSystem.IsMacOS()
                            ? "the .NET 10 Runtime (macOS Arm64 installer)"
                            : "the .NET 10 Desktop Runtime (x64)")
                        + " from https://dotnet.microsoft.com/download/dotnet/10.0 and press Haptic Settings again.");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[ThrumHaptics] Could not read the settings application's exit: {ex.Message}");
            }
            finally
            {
                lock (this._settingsProcessGate)
                {
                    if (ReferenceEquals(this._settingsProcess, process))
                    {
                        this._settingsProcess = null;
                    }
                }

                process.Dispose();
            }
        }

        /// <summary>Whether a line from the .NET host says the runtime is missing.</summary>
        /// <remarks>
        /// Matched on text because that is all macOS offers: the host's exit code is
        /// truncated to a byte there and 131 is also what a SIGQUIT looks like. The
        /// three phrases cover no .NET at all, the wrong version, and a missing
        /// host library.
        /// </remarks>
        private static Boolean IsMissingRuntimeMessage(String line) =>
            line.Contains("You must install", StringComparison.OrdinalIgnoreCase)
            || line.Contains("No frameworks were found", StringComparison.OrdinalIgnoreCase)
            || line.Contains("hostfxr", StringComparison.OrdinalIgnoreCase);

        /// <summary>The .NET host's own codes for a missing host library or framework.</summary>
        /// <remarks>
        /// Only ever matches on Windows, where the full 32-bit value survives. That
        /// is the platform that needs it, since its output is not captured.
        /// </remarks>
        private static Boolean IsMissingRuntimeExitCode(Int32 exitCode) =>
            exitCode == unchecked((Int32)0x80008083) || exitCode == unchecked((Int32)0x80008096);

        /// <summary>
        /// Restores the execute bit on the settings application, on Unix.
        /// </summary>
        /// <remarks>
        /// A .lplug4 is a zip, and ours is written on Windows - which has no Unix
        /// mode to store. If the bit does not survive the round trip, the extracted
        /// file cannot be exec'd at all and the settings window simply never opens,
        /// with nothing but a permissions error to show for it.
        ///
        /// CONFIRMED REAL on device: the extracted file came out as
        /// "OtherRead, GroupRead, UserWrite, UserRead" - no execute bit anywhere -
        /// so without this the settings window would never open on macOS, with
        /// nothing but a permissions error to explain it.
        ///
        /// Cheap to make unconditional rather than conditional on having detected
        /// the problem: reading and setting a file mode costs nothing next to
        /// launching a process, and it keeps working if the packaging tool's
        /// behaviour ever changes underneath us.
        /// </remarks>
        private static void EnsureExecutable(String path)
        {
            if (OperatingSystem.IsWindows())
            {
                return; // No Unix mode to set, and the APIs throw here.
            }

            try
            {
                var mode = System.IO.File.GetUnixFileMode(path);
                const System.IO.UnixFileMode Execute =
                    System.IO.UnixFileMode.UserExecute
                    | System.IO.UnixFileMode.GroupExecute
                    | System.IO.UnixFileMode.OtherExecute;

                if ((mode & Execute) == Execute)
                {
                    PluginLog.Info($"[ThrumHaptics] Settings application already executable (mode {mode}).");
                    return;
                }

                System.IO.File.SetUnixFileMode(path, mode | Execute);

                PluginLog.Info(
                    $"[ThrumHaptics] Settings application was NOT executable (mode {mode}); "
                    + $"execute bit added -> {System.IO.File.GetUnixFileMode(path)}.");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[ThrumHaptics] Could not set the execute bit on '{path}': {ex.Message}");
            }
        }

        // Initializes a new instance of the plugin class.
        public ThrumHapticsPlugin()
        {
            // Initialize the plugin log.
            PluginLog.Init(this.Log);

            // Initialize the plugin resources.
            PluginResources.Init(this.Assembly);
        }

        // This method is called when the plugin is loaded.
        public override void Load()
        {
            // Register every waveform as its own event. Registration must happen
            // before any RaiseEvent call, or the event name is unknown to Options+.
            foreach (var waveform in Waveforms.All)
            {
                this.PluginEvents.AddEvent(
                    Waveforms.EventNameFor(waveform),
                    waveform,
                    $"Plays the '{waveform}' haptic waveform");
            }

            PluginLog.Info($"[ThrumHaptics] Registered {Waveforms.All.Length} haptic events.");

            this._haptics = new HapticOutput(this);
            this.Settings = new HapticSettings(this);

            // Serves the separate settings application. Started even when no
            // settings window is open, since the window may be launched at any time.
            this._settingsServer = new SettingsServer(this.Settings, this._haptics);
            this._settingsServer.Start();

            // STAGES 1-2: click and scroll haptics.
            //
            // Stage 0 proved RaiseEvent works from a background thread with nothing
            // bound. This is the payoff - a global mouse hook drives it, so haptics
            // fire in every application rather than only when a device button bound
            // in Logi Options+ is pressed.
            // MUST happen before any hook is created. SharpHook's native library
            // cannot be unloaded once mapped into the Plugin Service process, so
            // loading it from the plugin folder would lock a file there and make
            // uninstalling the plugin fail. No-ops off Windows, where SharpHook is
            // not used at all - see MacMouseInputSource for why.
            NativeLibraryRedirect.Configure(System.IO.Path.GetDirectoryName(this.AssemblyFilePath));

            // The two implementations are NOT interchangeable and the difference is
            // not a portability convenience. SharpHook cannot be loaded into the
            // Plugin Service on macOS at all: that process runs with the hardened
            // runtime, so library validation refuses any dylib not signed by Apple
            // or by Logitech. macOS therefore taps CoreGraphics directly and ships
            // no native binary of ours. MacMouseInputSource documents this in full.
            this._inputSources.Add(OperatingSystem.IsMacOS()
                ? new MacMouseInputSource(this._haptics, this.Settings)
                : new MouseInputSource(this._haptics, this.Settings));

            // SPIKE, Windows only, logs and nothing else: can Raw Input tell the
            // MX Master apart from other pointers while Options+ is running? It
            // decides whether per-device filtering is buildable, and it must be
            // gone - or replaced by the real thing - before 1.3.0 ships.
            if (OperatingSystem.IsWindows())
            {
                this._inputSources.Add(new RawInputProbe());
            }

            foreach (var source in this._inputSources)
            {
                try
                {
                    source.Start();
                }
                catch (Exception ex)
                {
                    // One failed source must not take down the plugin: if the mouse
                    // hook cannot be installed we still want the plugin loaded and
                    // the failure visible in the log.
                    PluginLog.Error($"[ThrumHaptics] Input source '{source.Name}' failed to start: {ex}");
                }
            }
        }

        // This method is called when the plugin is unloaded.
        public override void Unload()
        {
            // Deterministic teardown matters here. The Plugin Service reloads this
            // assembly on every rebuild, and a leaked global hook would keep firing
            // from an unloaded copy - and SharpHook allows only ONE global hook per
            // process, so a leak would also stop the reloaded plugin from working.
            foreach (var source in this._inputSources)
            {
                try
                {
                    source.Dispose();
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"[ThrumHaptics] Input source '{source.Name}' failed to dispose: {ex}");
                }
            }

            this._inputSources.Clear();

            // Stop serving settings: the pipe must be released before the reloaded
            // plugin tries to claim the same name.
            this._settingsServer?.Dispose();
            this._settingsServer = null;
            this._haptics = null;

            PluginLog.Info("[ThrumHaptics] Unloaded.");
        }
    }
}
