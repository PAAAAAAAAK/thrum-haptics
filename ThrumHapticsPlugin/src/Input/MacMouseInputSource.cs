namespace Loupedeck.ThrumHapticsPlugin.Input
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Threading;

    using Loupedeck.ThrumHapticsPlugin.Config;
    using Loupedeck.ThrumHapticsPlugin.Haptics;

    /// <summary>
    /// Fires haptics on mouse buttons, scroll and drag on macOS, in every application.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS INSTEAD OF SHARPHOOK. SharpHook works fine on macOS in an
    /// application you control, but it cannot be used here. It reaches libuiohook
    /// through DllImport, which means dlopen of our own uiohook.dylib - and the
    /// Logi Plugin Service is signed with the hardened runtime:
    ///
    ///   flags=0x10000(runtime)   TeamIdentifier=QED4VVPZWA
    ///   entitlements: apple-events, allow-dyld-environment-variables, allow-jit
    ///
    /// Hardened runtime enables LIBRARY VALIDATION unless
    /// com.apple.security.cs.disable-library-validation is present, and it is not.
    /// Library validation permits loading only code signed by Apple or by the
    /// host's own Team ID. Our dylib is neither, and signing it ourselves cannot
    /// help - it would carry our team, not Logitech's. On Apple Silicon it is
    /// blocked twice over, since arm64 rejects unsigned libraries outright.
    ///
    /// That is also why every macOS haptic plugin on the Marketplace is pure
    /// managed code: .NET assemblies are loaded by CoreCLR, never dlopen'd, so
    /// library validation never inspects them.
    ///
    /// The way through is to ship NO native binary of our own. CoreGraphics and
    /// CoreFoundation are Apple-signed, so P/Invoke into them is explicitly
    /// permitted, and CGEventTap gives us exactly the system-wide mouse events
    /// libuiohook would have provided.
    ///
    /// MEASURED ON DEVICE (macOS 26.6.1, Apple Silicon): the tap is created and
    /// enabled inside the Plugin Service, events arrive on a run loop we own on a
    /// background thread, and no kCGEventTapDisabledByTimeout was observed across
    /// several minutes of continuous clicking and scrolling.
    /// </remarks>
    internal sealed class MacMouseInputSource : IInputSource
    {
        private const String CoreGraphics =
            "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        private const String CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        private const String ApplicationServices =
            "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

        private readonly HapticOutput _haptics;
        private readonly HapticSettings _settings;

        private IntPtr _tap;
        private IntPtr _runLoopSource;
        private IntPtr _runLoop;
        private Thread _thread;
        private Boolean _disposed;

        // The delegate must be held in a field. Marshalling it produces a raw
        // function pointer that the GC knows nothing about, so a local would be
        // collected while CoreGraphics still holds the pointer - producing a crash
        // inside the Plugin Service minutes after everything looked fine.
        private CGEventTapCallBack _callback;

        // --- Thumb buttons, the indirect route --------------------------------
        //
        // A SECOND tap, at session level, listen-only. Session level specifically:
        // the primary tap sits at HID level, UPSTREAM of where Options+ injects,
        // so the gesture it posts in place of a thumb button is invisible there.
        //
        // This exists because the earlier conclusion was too broad. Four
        // observation points proved the thumb button PRESSES never enter the HID
        // interface, and that remains true - but every one of them looked for a
        // mouse button. None looked for what Options+ sends instead, which turns
        // out to be an NSEvent gesture that arrives here perfectly reliably.
        private CGEventTapCallBack _diagnosticCallback;
        private IntPtr _diagnosticTap;
        private IntPtr _diagnosticRunLoopSource;

        // Which device an event came from. Both taps ask it, and both run on the
        // one tap thread, so it is shared without a lock.
        private readonly MacDeviceFilter _devices;

        public String Name => "Mouse (macOS)";

        public MacMouseInputSource(HapticOutput haptics, HapticSettings settings)
        {
            this._haptics = haptics ?? throw new ArgumentNullException(nameof(haptics));
            this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this._devices = new MacDeviceFilter(this._settings);
        }

        // --- CoreGraphics / CoreFoundation ------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CGEventTapCallBack(
            IntPtr proxy, UInt32 type, IntPtr @event, IntPtr userInfo);

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGEventTapCreate(
            UInt32 tap, UInt32 place, UInt32 options, UInt64 eventsOfInterest,
            CGEventTapCallBack callback, IntPtr userInfo);

        [DllImport(CoreGraphics)]
        private static extern void CGEventTapEnable(IntPtr tap, Boolean enable);

        [DllImport(CoreGraphics)]
        private static extern Int64 CGEventGetIntegerValueField(IntPtr @event, UInt32 field);

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint
        {
            public Double X;
            public Double Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGSize
        {
            public Double Width;
            public Double Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect
        {
            public CGPoint Origin;
            public CGSize Size;
        }

        /// <summary>Cursor position in global display space, origin top-left.</summary>
        /// <remarks>
        /// NOT CGEventGetUnflippedLocation, which puts the origin at the bottom
        /// left. This one shares its coordinate system with CGDisplayBounds, so the
        /// two can be compared without converting between them.
        /// </remarks>
        [DllImport(CoreGraphics)]
        private static extern CGPoint CGEventGetLocation(IntPtr @event);

        [DllImport(CoreGraphics)]
        private static extern Int32 CGGetActiveDisplayList(
            UInt32 maxDisplays, [Out] UInt32[] activeDisplays, out UInt32 displayCount);

        [DllImport(CoreGraphics)]
        private static extern CGRect CGDisplayBounds(UInt32 display);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFMachPortCreateRunLoopSource(
            IntPtr allocator, IntPtr port, IntPtr order);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFRunLoopGetCurrent();

        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);

        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopRun();

        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopStop(IntPtr runLoop);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);

        // Releasing a CFMachPort is NOT the same as invalidating it. CFRelease
        // drops our reference, but the port stays registered and the tap stays
        // alive in the system - measured on device with CGGetEventTapList, which
        // found six taps owned by LogiPluginService where there should have been
        // one, five of them stale and the oldest still carrying the event mask of
        // the very first build. Every plugin reload leaked one.
        [DllImport(CoreFoundation)]
        private static extern void CFMachPortInvalidate(IntPtr port);

        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopSourceInvalidate(IntPtr source);

        /// <summary>Whether this process holds macOS Accessibility permission.</summary>
        /// <remarks>
        /// The grant attaches to the HOST process - LogiPluginService - not to us,
        /// so we can only report it, never request it usefully. Logged at load
        /// because a tap that silently never fires looks identical to a bug in our
        /// own code, and this distinguishes the two immediately.
        /// </remarks>
        [DllImport(ApplicationServices)]
        private static extern Boolean AXIsProcessTrusted();

        // Event tap placement and behaviour.
        //
        // TWO attachment points exist, and which one we use decides whether the
        // thumb buttons are visible at all:
        //
        //   device -> [HID tap] -> ... Logi Options+ ... -> [session tap] -> apps
        //
        // At the session tap the thumb buttons never arrived - thousands of events
        // logged, not one with buttonNumber 3 or 4 - while middle click came
        // through as buttonNumber 2. Options+ has back/forward bound and appears to
        // CONSUME those presses upstream, re-posting its own navigation action,
        // which would put them beyond a session tap but in front of an HID tap.
        //
        // So we ask for the HID tap and fall back to the session tap. The HID tap
        // needs the same Accessibility permission we already hold, and being
        // earlier is arguably more correct for haptics anyway: the feedback should
        // answer "you pressed the button", not "an application agreed to act on it".
        private const UInt32 HidEventTap = 0;
        private const UInt32 SessionEventTap = 1;
        private const UInt32 HeadInsertEventTap = 0;

        // LISTEN ONLY, deliberately. The alternative lets a tap alter or swallow
        // events, which would put us in the path of every click on the system -
        // a far worse failure mode, and a far worse thing to ask users to trust.
        // We only ever read.
        private const UInt32 EventTapOptionListenOnly = 1;

        private const UInt32 EventLeftMouseDown = 1;
        private const UInt32 EventLeftMouseUp = 2;
        private const UInt32 EventRightMouseDown = 3;
        private const UInt32 EventRightMouseUp = 4;
        private const UInt32 EventMouseMoved = 5;
        private const UInt32 EventLeftMouseDragged = 6;
        private const UInt32 EventRightMouseDragged = 7;
        private const UInt32 EventScrollWheel = 22;
        private const UInt32 EventOtherMouseDown = 25;
        private const UInt32 EventOtherMouseUp = 26;
        private const UInt32 EventOtherMouseDragged = 27;

        // Delivered in place of a real event when the OS switches the tap off.
        private const UInt32 EventTapDisabledByTimeout = 0xFFFFFFFE;
        private const UInt32 EventTapDisabledByUserInput = 0xFFFFFFFF;

        // --- Gesture event types, for the back/forward diagnostic --------------
        //
        // These are NSEvent types that appear in the CGEventTap stream but have no
        // kCGEvent* constant, which is why the original thumb-button measurement
        // never saw them: every observation point looked for a mouse BUTTON, and
        // ruled buttons out conclusively. It never looked at what Options+ posts
        // INSTEAD. A swipe is not a mouse event and would not have shown up.
        //
        // NOTE what is deliberately absent: 10 (keyDown), 11 (keyUp) and 12
        // (flagsChanged). Widening into gestures is not a licence to widen into
        // the keyboard, which this plugin does not tap on any platform, ever.
        private const UInt32 EventRotate = 18;
        private const UInt32 EventBeginGesture = 19;
        private const UInt32 EventEndGesture = 20;
        private const UInt32 EventGesture = 29;
        private const UInt32 EventMagnify = 30;
        private const UInt32 EventSwipe = 31;
        private const UInt32 EventSmartMagnify = 32;

        private const UInt32 FieldMouseEventButtonNumber = 3;
        private const UInt32 FieldMouseEventDeltaX = 4;
        private const UInt32 FieldMouseEventDeltaY = 5;
        private const UInt32 FieldScrollWheelEventDeltaAxis1 = 11; // vertical, LINES, accelerated
        private const UInt32 FieldScrollWheelEventDeltaAxis2 = 12; // horizontal, LINES, accelerated

        // Diagnostics for detent reconstruction. Windows gives one event per
        // physical detent; macOS reports the same wheel at high resolution, so
        // several events arrive per detent and something here has to stand in for
        // the detent boundary. Logged until we know which field actually tracks
        // physical movement rather than post-acceleration distance.
        /// <summary>PID of the process that posted the event, or 0 for real hardware.</summary>
        /// <remarks>
        /// The single most useful field in the back/forward diagnostic. If Options+
        /// substitutes a navigation gesture for the thumb buttons, the substitute
        /// carries Options+'s PID; a genuine wheel roll carries 0. That tells
        /// injected apart from physical without any guesswork about deltas.
        /// </remarks>
        private const UInt32 FieldEventSourceUnixProcessId = 41;

        private const UInt32 FieldScrollWheelEventIsContinuous = 88;
        private const UInt32 FieldScrollWheelEventPointDeltaAxis1 = 96;
        private const UInt32 FieldScrollWheelEventPointDeltaAxis2 = 97;
        private const UInt32 FieldScrollWheelEventScrollPhase = 99;
        private const UInt32 FieldScrollWheelEventMomentumPhase = 123;

        private static UInt64 MaskOf(params UInt32[] types)
        {
            UInt64 mask = 0;

            foreach (var t in types)
            {
                mask |= 1UL << (Int32)t;
            }

            return mask;
        }

        // --- Buttons ----------------------------------------------------------

        /// <summary>
        /// The buttons we recognise, independent of how macOS delivers them.
        /// </summary>
        /// <remarks>
        /// Left and right arrive as their own event types; everything else comes
        /// through the OtherMouse* types carrying a button number. Keeping a
        /// logical id lets drag tracking work the same way for all three
        /// drag-capable buttons without caring which route the event took.
        /// </remarks>
        private enum Button
        {
            None,
            Left,
            Right,
            Middle,
            Back,
            Forward,
        }

        /// <summary>Maps macOS "other" button numbers onto logical buttons.</summary>
        /// <remarks>
        /// 2 is the wheel button; 3 and 4 are the thumb buttons. Anything else is
        /// a button this mouse does not have and is ignored rather than guessed at
        /// - but see OnEvent, which logs every unmapped number so a wrong
        /// assumption here shows up in the log instead of failing silently.
        /// </remarks>
        private static Button ButtonForOtherNumber(Int64 number) => number switch
        {
            2 => Button.Middle,
            3 => Button.Back,
            4 => Button.Forward,
            _ => Button.None,
        };

        private static String EventIdFor(Button button) => button switch
        {
            Button.Left => HapticEvents.MouseLeft,
            Button.Right => HapticEvents.MouseRight,
            Button.Middle => HapticEvents.MouseMiddle,
            Button.Back => HapticEvents.MouseBack,
            Button.Forward => HapticEvents.MouseForward,
            _ => null,
        };

        /// <summary>Which buttons can start a drag, and the events they raise.</summary>
        /// <remarks>
        /// Back and forward are deliberately absent, exactly as on Windows:
        /// holding a thumb button and moving is not a drag by any normal meaning
        /// of the word.
        /// </remarks>
        private static readonly Dictionary<Button, (String Start, String End)> DragEvents = new()
        {
            [Button.Left] = (HapticEvents.DragLeftStart, HapticEvents.DragLeftEnd),
            [Button.Right] = (HapticEvents.DragRightStart, HapticEvents.DragRightEnd),
            [Button.Middle] = (HapticEvents.DragMiddleStart, HapticEvents.DragMiddleEnd),
        };

        // --- Pacing and thresholds --------------------------------------------
        //
        // These mirror MouseInputSource exactly, and the reasoning behind each is
        // documented there rather than repeated. They are duplicated on purpose
        // for now: extracting shared pacing logic is worth doing, but not before
        // the macOS side has settled enough to know what genuinely IS shared.

        /// <summary>Minimum gap between wheel haptics, in milliseconds.</summary>
        /// <remarks>
        /// Higher than Windows' 50, and the reason is measured rather than felt.
        /// macOS reports this wheel as CONTINUOUS (kCGScrollWheelEventIsContinuous
        /// is 1 on every event) and accelerates hard: rolled slowly one detent
        /// reports pt1 of about 3, and in a fast flick about 147. Same wheel, same
        /// detents, 45x the reported distance.
        ///
        /// So physical detents CANNOT be reconstructed here. Neither lines nor
        /// points survive the acceleration, which rules out the accumulator that
        /// synthesizes them on Windows. What is left is a rate limit, and its value
        /// is a judgement about feel rather than a correct answer.
        ///
        /// Indexed by the user's density level, and identical to the Windows table
        /// on purpose: someone moving between machines should not have to relearn
        /// what a flick feels like. Level 3 is the default at 35ms.
        ///
        /// Getting here took going the WRONG WAY first. 70 was tried on the theory
        /// that a fast flick was over-firing into a buzz; on device it read as too
        /// sparse to track the detents. The flick wanted MORE haptics, not fewer,
        /// and this floor is the only thing withholding them - macOS delivers
        /// roughly 100 accelerated events a second, so nothing else paces them.
        ///
        /// Which is the whole argument for making it a setting: it was tuned by
        /// feel four times across two platforms and landed somewhere different each
        /// time, because there is no correct answer to find.
        /// </remarks>
        private static readonly Int64[] ScrollCooldownByDensity = { 70, 50, 35, 25, 20 };

        /// <summary>Minimum gap between thumb-wheel haptics.</summary>
        /// <remarks>
        /// Longer than the main wheel for two reasons that compound: the thumb
        /// wheel has NO physical detents, so the haptic is the only thing marking
        /// steps and an uneven one reads as a rattle rather than a scale; and it
        /// carries the sharpest click-grade waveform because it sits furthest from
        /// the motor. Sharp plus frequent is the worst combination available.
        ///
        /// It rarely binds - the log shows thumb-wheel events arriving 127-955ms
        /// apart - so this is a ceiling for fast rolls rather than everyday pacing.
        /// It still scales with density, or the densest levels would be capped by a
        /// floor the user never chose.
        /// </remarks>
        private static readonly Int64[] ThumbWheelCooldownByDensity = { 90, 70, 50, 35, 25 };

        /// <summary>Thumb-wheel travel, in points, between synthesized detents.</summary>
        /// <remarks>
        /// The macOS counterpart to ThumbWheelDetentUnits on Windows. Both answer
        /// the same question - how far should the thumb move between ticks - because
        /// this wheel has no physical detents and the haptic IS the detent.
        ///
        /// Firing on the line counter alone capped the thumb wheel at roughly 4.6
        /// ticks a second regardless of speed, which is what "not dense enough"
        /// meant. Accumulating points instead ties ticks to travel: roll slowly and
        /// they come slowly, roll fast and they come fast.
        ///
        /// Indexed by density level; 12 points is the default. Note that the line
        /// counter still fires a tick on its own, so the sparse levels converge on
        /// that floor - roughly 4.6 a second - rather than going quieter still.
        /// </remarks>
        private static readonly Int64[] ThumbWheelPointsByDensity = { 36, 24, 12, 8, 5 };

        private Int64 _thumbWheelPixels;

        /// <summary>The time floor for an event at the user's chosen density.</summary>
        private Int64 CooldownFor(String eventId, Int64[] table) =>
            table[HapticEvents.ClampDensity(this._settings.DensityFor(eventId)) - 1];

        private const Int32 DragThresholdPx = 5;
        private const Int64 DragMinSeparationMs = 150;
        private const Int32 ScreenEdgeReleasePx = 8;

        /// <summary>How far past an edge to look for another display.</summary>
        /// <remarks>
        /// Small, because macOS snaps display arrangements together - but not zero,
        /// since the point exactly on a boundary belongs to neither rectangle.
        /// </remarks>
        private const Double EdgeProbePx = 2;

        private Boolean _atScreenEdge;

        // EVERY display, not their union. Cached because mouse-move fires constantly
        // and this must stay cheap; re-read periodically so plugging in a monitor is
        // picked up without a display-reconfiguration callback.
        private CGRect[] _displays = Array.Empty<CGRect>();
        private Int64 _displaysReadMs;
        private const Int64 DisplaysTtlMs = 5000;

        private readonly Dictionary<String, Int64> _lastFiredMs = new();

        // Feed the Verbose scroll trace in OnScroll, which is how the pacing above
        // was tuned and how it would be re-tuned.
        private Int64 _lastScrollMs;
        private Int32 _scrollSubLineEvents;

        /// <summary>Per-button drag tracking. Buttons can be held simultaneously.</summary>
        /// <remarks>
        /// Displacement is accumulated from the event stream rather than read as an
        /// absolute cursor position. CGEventGetLocation returns a CGPoint by value,
        /// and summing the signed per-event deltas gives the same answer - net
        /// distance from where the button went down - without marshalling a struct
        /// back across the boundary.
        /// </remarks>
        private sealed class DragState
        {
            public Boolean ButtonDown;
            public Boolean Dragging;
            public Int64 DisplacementX;
            public Int64 DisplacementY;
            public Int64 StartMs;
        }

        private readonly Dictionary<Button, DragState> _dragStates = new()
        {
            [Button.Left] = new DragState(),
            [Button.Right] = new DragState(),
            [Button.Middle] = new DragState(),
        };

        public void Start()
        {
            if (this._thread != null)
            {
                return;
            }

            PluginLog.Info(
                $"[ThrumHaptics] macOS Accessibility permission for this process: {AXIsProcessTrusted()}");

            // The tap and its run loop must live on the SAME thread, and CFRunLoopRun
            // blocks forever, so this needs a thread of its own. Background so it can
            // never hold up process shutdown.
            this._thread = new Thread(this.RunTapThread)
            {
                IsBackground = true,
                Name = "ThrumHaptics.MacEventTap",
            };

            this._thread.Start();
        }

        private void RunTapThread()
        {
            try
            {
                this._callback = this.OnEvent;

                var mask = MaskOf(
                    EventLeftMouseDown, EventLeftMouseUp, EventLeftMouseDragged,
                    EventRightMouseDown, EventRightMouseUp, EventRightMouseDragged,
                    EventOtherMouseDown, EventOtherMouseUp, EventOtherMouseDragged,
                    EventScrollWheel, EventMouseMoved);

                // HID tap preferred, session tap as a fallback. The HID tap can be
                // refused where the session tap is allowed, and a working plugin
                // that misses two buttons beats no plugin at all.
                var location = HidEventTap;

                this._tap = CGEventTapCreate(
                    location, HeadInsertEventTap, EventTapOptionListenOnly,
                    mask, this._callback, IntPtr.Zero);

                if (this._tap == IntPtr.Zero)
                {
                    PluginLog.Info("[ThrumHaptics] HID-level event tap refused; falling back to the session tap.");

                    location = SessionEventTap;

                    this._tap = CGEventTapCreate(
                        location, HeadInsertEventTap, EventTapOptionListenOnly,
                        mask, this._callback, IntPtr.Zero);
                }

                if (this._tap == IntPtr.Zero)
                {
                    // Overwhelmingly the Accessibility permission: CGEventTapCreate
                    // returns null rather than failing loudly when it is missing.
                    PluginLog.Error(
                        "[ThrumHaptics] CGEventTapCreate returned NULL at both tap locations - no event "
                        + "tap could be created. This is almost always missing Accessibility permission "
                        + "for LogiPluginService (System Settings > Privacy & Security > Accessibility).");
                    return;
                }

                PluginLog.Info(
                    $"[ThrumHaptics] Event tap created at {(location == HidEventTap ? "HID" : "session")} level.");

                this._runLoopSource = CFMachPortCreateRunLoopSource(IntPtr.Zero, this._tap, IntPtr.Zero);

                if (this._runLoopSource == IntPtr.Zero)
                {
                    PluginLog.Error("[ThrumHaptics] CFMachPortCreateRunLoopSource returned NULL.");
                    return;
                }

                this._runLoop = CFRunLoopGetCurrent();

                CFRunLoopAddSource(this._runLoop, this._runLoopSource, GetCommonModes());
                CGEventTapEnable(this._tap, true);

                PluginLog.Info("[ThrumHaptics] macOS event tap ENABLED - now receiving system-wide mouse events.");

                // Before the run loop starts, so it is settled before the first event.
                this._devices.Start();

                // Shares this thread's run loop deliberately: one thread, one loop,
                // two sources. A second thread would buy nothing and would need its
                // own shutdown path.
                this.StartDiagnosticTap();

                // Blocks until CFRunLoopStop is called from Stop().
                CFRunLoopRun();

                PluginLog.Info("[ThrumHaptics] macOS event tap run loop exited.");
            }
            catch (Exception ex)
            {
                // A P/Invoke failure here would otherwise vanish silently on a
                // background thread, leaving a plugin that loads fine and never buzzes.
                PluginLog.Error($"[ThrumHaptics] macOS event tap failed: {ex}");
            }
        }

        /// <summary>
        /// Starts the session-level diagnostic tap. Never fires haptics.
        /// </summary>
        /// <remarks>
        /// Failure here is deliberately non-fatal and logged at Info rather than
        /// Error: the plugin's actual job is done by the primary tap, and a
        /// diagnostic that cannot start must not look like a broken plugin.
        /// </remarks>
        private void StartDiagnosticTap()
        {
            this._diagnosticCallback = this.OnDiagnosticEvent;

            // Gestures, plus scroll. Scroll is not acted on here - the primary tap
            // already handles it - but its TIMING is what distinguishes a thumb
            // wheel roll from a thumb button press, so this tap has to see it.
            var mask = MaskOf(
                EventRotate, EventBeginGesture, EventEndGesture, EventGesture,
                EventMagnify, EventSwipe, EventSmartMagnify,
                EventScrollWheel);

            this._diagnosticTap = CGEventTapCreate(
                SessionEventTap, HeadInsertEventTap, EventTapOptionListenOnly,
                mask, this._diagnosticCallback, IntPtr.Zero);

            if (this._diagnosticTap == IntPtr.Zero)
            {
                PluginLog.Info(
                    "[ThrumHaptics] session-level gesture tap refused; thumb-button haptics unavailable. "
                    + "Everything else is unaffected.");

                this._diagnosticCallback = null;
                return;
            }

            this._diagnosticRunLoopSource =
                CFMachPortCreateRunLoopSource(IntPtr.Zero, this._diagnosticTap, IntPtr.Zero);

            if (this._diagnosticRunLoopSource == IntPtr.Zero)
            {
                PluginLog.Info("[ThrumHaptics] gesture tap run loop source is NULL; skipping it.");
                return;
            }

            CFRunLoopAddSource(this._runLoop, this._diagnosticRunLoopSource, GetCommonModes());
            CGEventTapEnable(this._diagnosticTap, true);

            PluginLog.Info("[ThrumHaptics] session-level gesture tap enabled (thumb buttons).");
        }

        /// <summary>
        /// How long after a scroll event a gesture is still assumed to belong to it.
        /// </summary>
        /// <remarks>
        /// The thumb WHEEL and the thumb BUTTONS both emit type 29 from the same
        /// process, so the source PID cannot separate them and only timing can. A
        /// wheel roll has scroll events all around its gestures; a button press
        /// had no scroll within eighteen seconds.
        ///
        /// 600ms is generous on purpose. The leak it cannot close is a roll that
        /// starts after a pause: the wheel emits its first gesture about 150ms
        /// BEFORE its first scroll event, so nothing backward-looking can catch it.
        /// Closing that would mean holding the haptic back to see whether scroll
        /// follows, and a delayed click haptic is worse than an occasional extra
        /// one. This is why the event ships disabled by default.
        /// </remarks>
        private const Int64 GestureScrollProximityMs = 600;

        /// <summary>Collapses one press's burst of gesture events into one haptic.</summary>
        /// <remarks>
        /// A press arrives as two events milliseconds apart, and a wheel roll as
        /// bursts of three or four. Firing on the first and ignoring the rest until
        /// things go quiet gives one haptic per press without waiting to count -
        /// waiting would add latency to the very thing being acknowledged.
        /// </remarks>
        private const Int64 GestureDebounceMs = 300;

        private Int64 _lastDiagnosticScrollMs;
        private Int64 _lastIsolatedGestureMs;

        /// <summary>Turns an Options+ navigation gesture into a thumb-button haptic.</summary>
        private IntPtr OnDiagnosticEvent(IntPtr proxy, UInt32 type, IntPtr @event, IntPtr userInfo)
        {
            try
            {
                if (type == EventTapDisabledByTimeout || type == EventTapDisabledByUserInput)
                {
                    CGEventTapEnable(this._diagnosticTap, true);
                    return @event;
                }

                // Before the scroll bookkeeping below, not after it: another
                // device's scrolling must not be what makes a thumb press look
                // like a thumb-wheel roll.
                if (!this._devices.Allows(type, @event))
                {
                    return @event;
                }

                var now = Environment.TickCount64;

                if (type == EventScrollWheel)
                {
                    this._lastDiagnosticScrollMs = now;
                    return @event;
                }

                var isGesture =
                    type is EventRotate or EventBeginGesture or EventEndGesture
                        or EventGesture or EventMagnify or EventSwipe or EventSmartMagnify;

                if (!isGesture)
                {
                    return @event;
                }

                // A gesture from real HARDWARE has no posting process. That is what
                // a trackpad swipe looks like, and it is measured: swipes carried
                // fieldid 45 and no source PID, while every thumb-button press
                // carried source PID 1613 - Options+. Without this check a
                // three-finger swipe would buzz the mouse.
                if (CGEventGetIntegerValueField(@event, FieldEventSourceUnixProcessId) == 0)
                {
                    return @event;
                }

                // Scroll nearby means the thumb WHEEL, not a thumb button.
                if ((now - this._lastDiagnosticScrollMs) < GestureScrollProximityMs)
                {
                    return @event;
                }

                // One haptic per burst.
                if (this._lastIsolatedGestureMs != 0
                    && (now - this._lastIsolatedGestureMs) < GestureDebounceMs)
                {
                    this._lastIsolatedGestureMs = now;
                    return @event;
                }

                this._lastIsolatedGestureMs = now;

                this.Fire(HapticEvents.MouseThumb, cooldownMs: 0);
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[ThrumHaptics] gesture tap callback error: {ex.Message}");
            }

            return @event;
        }

        /// <summary>Reads kCFRunLoopCommonModes, an exported CFStringRef variable.</summary>
        /// <remarks>
        /// It is DATA, not a function, so DllImport cannot reach it. Load the
        /// address of the variable and dereference it to get the CFStringRef.
        /// Common modes rather than default mode, so the tap keeps delivering
        /// while the run loop is in a modal or tracking mode.
        /// </remarks>
        private static IntPtr GetCommonModes()
        {
            var handle = NativeLibrary.Load(CoreFoundation);
            var symbol = NativeLibrary.GetExport(handle, "kCFRunLoopCommonModes");

            return Marshal.ReadIntPtr(symbol);
        }

        private IntPtr OnEvent(IntPtr proxy, UInt32 type, IntPtr @event, IntPtr userInfo)
        {
            try
            {
                // macOS switches a tap OFF if its callback is too slow, which is the
                // direct analogue of Windows evicting a low-level hook on
                // LowLevelHooksTimeout. Without re-enabling here, haptics would just
                // stop mid-session with no error anywhere.
                if (type == EventTapDisabledByTimeout || type == EventTapDisabledByUserInput)
                {
                    PluginLog.Info($"[ThrumHaptics] Event tap disabled by the OS (type={type}); re-enabling.");
                    CGEventTapEnable(this._tap, true);
                    return @event;
                }

                // Another device's input stops HERE, before any of it is counted.
                // Dropping it only at the moment of playing would still let a
                // trackpad fill the scroll accumulators and start drags that the
                // MX Master's next event then finishes.
                if (!this._devices.Allows(type, @event))
                {
                    return @event;
                }

                switch (type)
                {
                    case EventLeftMouseDown:
                        this.OnButtonDown(Button.Left);
                        break;

                    case EventRightMouseDown:
                        this.OnButtonDown(Button.Right);
                        break;

                    case EventLeftMouseUp:
                        this.OnButtonUp(Button.Left);
                        break;

                    case EventRightMouseUp:
                        this.OnButtonUp(Button.Right);
                        break;

                    case EventLeftMouseDragged:
                        this.OnDragged(Button.Left, @event);
                        break;

                    case EventRightMouseDragged:
                        this.OnDragged(Button.Right, @event);
                        break;

                    case EventOtherMouseDown:
                    case EventOtherMouseUp:
                    case EventOtherMouseDragged:
                        this.OnOtherMouse(type, @event);
                        break;

                    case EventMouseMoved:
                        this.OnMouseMoved(@event);
                        break;

                    case EventScrollWheel:
                        this.OnScroll(@event);
                        break;
                }
            }
            catch (Exception ex)
            {
                // NEVER let an exception escape into CoreGraphics. Unwinding through
                // native frames would take down the whole Plugin Service.
                PluginLog.Error($"[ThrumHaptics] Event tap callback error: {ex.Message}");
            }

            // Listen-only, so the return value is ignored - but returning the event
            // unchanged is the documented contract and costs nothing.
            return @event;
        }

        /// <summary>
        /// Handles the middle and thumb buttons, which all arrive as OtherMouse*.
        /// </summary>
        /// <remarks>
        /// The raw button number is logged for EVERY such event, including numbers
        /// we do not map. An earlier build mapped them silently, so a button that
        /// produced no haptic was indistinguishable from a button whose events
        /// never arrived at all - and those two have completely different fixes.
        /// </remarks>
        private void OnOtherMouse(UInt32 type, IntPtr @event)
        {
            var number = CGEventGetIntegerValueField(@event, FieldMouseEventButtonNumber);
            var button = ButtonForOtherNumber(number);

            PluginLog.Info($"[ThrumHaptics] other-mouse type={type} buttonNumber={number} -> {button}");

            if (button == Button.None)
            {
                return;
            }

            switch (type)
            {
                case EventOtherMouseDown:
                    this.OnButtonDown(button);
                    break;

                case EventOtherMouseUp:
                    this.OnButtonUp(button);
                    break;

                case EventOtherMouseDragged:
                    this.OnDragged(button, @event);
                    break;
            }
        }

        private void OnButtonDown(Button button)
        {
            if (this._dragStates.TryGetValue(button, out var state))
            {
                state.ButtonDown = true;
                state.Dragging = false;
                state.DisplacementX = 0;
                state.DisplacementY = 0;
            }

            // Fire on PRESS rather than release, so the haptic coincides with the
            // physical switch actuating instead of trailing it.
            this.Fire(EventIdFor(button), cooldownMs: 0);
        }

        private void OnButtonUp(Button button)
        {
            if (!this._dragStates.TryGetValue(button, out var state))
            {
                return; // Not a drag-capable button (back / forward).
            }

            state.ButtonDown = false;

            if (!state.Dragging)
            {
                return; // Plain click - already handled on press.
            }

            state.Dragging = false;

            // Too quick for the pair to read as two distinct taps: keep the start,
            // drop the end, rather than deliver a smear.
            if ((Environment.TickCount64 - state.StartMs) < DragMinSeparationMs)
            {
                return;
            }

            this.Fire(DragEvents[button].End, cooldownMs: 0);
        }

        private void OnDragged(Button button, IntPtr @event)
        {
            if (!this._dragStates.TryGetValue(button, out var state) || !state.ButtonDown || state.Dragging)
            {
                return;
            }

            // Signed accumulation, so this measures net displacement from the press
            // point rather than total path length. Summing absolute values would let
            // a slow wiggle in place cross the threshold and report a drag that
            // never went anywhere.
            state.DisplacementX += CGEventGetIntegerValueField(@event, FieldMouseEventDeltaX);
            state.DisplacementY += CGEventGetIntegerValueField(@event, FieldMouseEventDeltaY);

            if (Math.Abs(state.DisplacementX) < DragThresholdPx
                && Math.Abs(state.DisplacementY) < DragThresholdPx)
            {
                return;
            }

            state.Dragging = true;
            state.StartMs = Environment.TickCount64;

            this.Fire(DragEvents[button].Start, cooldownMs: 0);
        }

        private void OnMouseMoved(IntPtr @event)
        {
            // Cheapest possible early-out: this runs on every pixel of cursor
            // movement, so it must do nothing at all when the feature is off.
            if (!this._settings.IsEnabled(HapticEvents.ScreenEdge))
            {
                return;
            }

            this.RefreshDisplays();

            var p = CGEventGetLocation(@event);
            var current = this.DisplayContaining(p.X, p.Y);

            if (current == null)
            {
                return; // Cursor is nowhere we know about; nothing to compare to.
            }

            if (this.AtWall(current.Value, p, tolerance: 1))
            {
                if (!this._atScreenEdge)
                {
                    this._atScreenEdge = true;
                    this.Fire(HapticEvents.ScreenEdge, cooldownMs: 0);
                }

                return;
            }

            // Re-arm only after a clear retreat, so resting against the edge does
            // not machine-gun on sub-pixel jitter.
            if (this._atScreenEdge && !this.AtWall(current.Value, p, ScreenEdgeReleasePx))
            {
                this._atScreenEdge = false;
            }
        }

        /// <summary>
        /// Whether the cursor is against an edge it genuinely cannot cross.
        /// </summary>
        /// <remarks>
        /// The bounding box of all displays is the WRONG model, and a stacked
        /// arrangement shows why. Put a wide external monitor above a narrower
        /// laptop screen and the union is wider than either: while the cursor is on
        /// the laptop it can never reach the union's left or right, because those
        /// x-coordinates only exist on the display above. Only the top and bottom of
        /// the union are ever touchable, which is exactly how it behaved.
        ///
        /// A union also cannot tell a wall from the seam between two monitors, where
        /// the cursor passes straight through and a haptic would be wrong.
        ///
        /// So the question is asked per edge of the display the cursor is actually
        /// on: is there another display just beyond it? If not, it is a wall.
        /// </remarks>
        private Boolean AtWall(CGRect d, CGPoint p, Double tolerance)
        {
            var right = d.Origin.X + d.Size.Width;
            var bottom = d.Origin.Y + d.Size.Height;

            // Probed from the DISPLAY's edge rather than the cursor, so the answer
            // does not change as the cursor creeps within the tolerance band.
            if (p.X <= d.Origin.X + tolerance
                && this.DisplayContaining(d.Origin.X - EdgeProbePx, p.Y) == null)
            {
                return true;
            }

            if (p.X >= right - 1 - tolerance
                && this.DisplayContaining(right + EdgeProbePx, p.Y) == null)
            {
                return true;
            }

            if (p.Y <= d.Origin.Y + tolerance
                && this.DisplayContaining(p.X, d.Origin.Y - EdgeProbePx) == null)
            {
                return true;
            }

            return p.Y >= bottom - 1 - tolerance
                && this.DisplayContaining(p.X, bottom + EdgeProbePx) == null;
        }

        private CGRect? DisplayContaining(Double x, Double y)
        {
            foreach (var d in this._displays)
            {
                if (x >= d.Origin.X && x < d.Origin.X + d.Size.Width
                    && y >= d.Origin.Y && y < d.Origin.Y + d.Size.Height)
                {
                    return d;
                }
            }

            return null;
        }

        /// <summary>Refreshes the cached bounds of every active display.</summary>
        private void RefreshDisplays()
        {
            var now = Environment.TickCount64;

            if (this._displays.Length > 0 && (now - this._displaysReadMs) <= DisplaysTtlMs)
            {
                return;
            }

            this._displaysReadMs = now;

            var ids = new UInt32[16];

            if (CGGetActiveDisplayList((UInt32)ids.Length, ids, out var count) != 0 || count == 0)
            {
                return;
            }

            var bounds = new CGRect[count];

            for (var i = 0; i < count; i++)
            {
                bounds[i] = CGDisplayBounds(ids[i]);
            }

            this._displays = bounds;
        }

        private void OnScroll(IntPtr @event)
        {
            // MOMENTUM first, before anything is counted or accumulated. These are
            // inertial scrolling the OS continues AFTER the wheel has stopped, so a
            // haptic there is feedback for input the user is no longer giving.
            if (CGEventGetIntegerValueField(@event, FieldScrollWheelEventMomentumPhase) != 0)
            {
                return;
            }

            var vertical = CGEventGetIntegerValueField(@event, FieldScrollWheelEventDeltaAxis1);
            var horizontal = CGEventGetIntegerValueField(@event, FieldScrollWheelEventDeltaAxis2);
            var horizontalPoints = CGEventGetIntegerValueField(@event, FieldScrollWheelEventPointDeltaAxis2);

            // The thumb wheel is paced by DISTANCE ROLLED, the same idea as
            // ThumbWheelDetentUnits on Windows, and for the same reason: it has no
            // physical detents, so the haptic is the only thing marking steps.
            //
            // It has to accumulate here, before the sub-line early-out below, or the
            // pixels carried by sub-line events are thrown away. That was the actual
            // cause of the thumb wheel feeling sparse: haptics could only fire when
            // the LINE counter ticked, and the log shows those arriving 150-870ms
            // apart - about 4.6 a second no matter how fast the wheel is rolled.
            var thumbWheelStep = this.CooldownFor(HapticEvents.ScrollHorizontal, ThumbWheelPointsByDensity);

            this._thumbWheelPixels += Math.Abs(horizontalPoints);

            var pixelStepReached = this._thumbWheelPixels >= thumbWheelStep;

            // MOST events carry zero on both line axes - the wheel reports at high
            // resolution and macOS only ticks the line counter once a full line
            // accumulates. They are still worth counting, and now worth acting on
            // when enough thumb-wheel travel has built up.
            if (vertical == 0 && horizontal == 0 && !pixelStepReached)
            {
                this._scrollSubLineEvents++;
                return;
            }

            // NOTE the line delta is ACCELERATED by macOS: the same wheel reports
            // pt1 of about 3 rolled slowly and about 147 in a fast flick. It is a
            // distance, never a detent count, which is why the VERTICAL wheel has no
            // accumulator - there is nothing left to count that corresponds to a
            // physical notch, and only a rate limit remains.
            //
            // Verbose: one line per scroll event would swamp an Info log. Raise it
            // when tuning - `dt` is the gap since the last event that counted, `sub`
            // how many sub-line events preceded it, `px` the thumb-wheel travel
            // accumulator, and SUPPRESSED marks where the floor bound rather than
            // the wheel.
            var now = Environment.TickCount64;
            var horizontalScroll = horizontal != 0 || pixelStepReached;

            var eventId = horizontalScroll ? HapticEvents.ScrollHorizontal : HapticEvents.ScrollVertical;

            var cooldown = horizontalScroll
                ? this.CooldownFor(eventId, ThumbWheelCooldownByDensity)
                : this.CooldownFor(eventId, ScrollCooldownByDensity);

            var suppressed =
                this._lastFiredMs.TryGetValue(eventId, out var lastFired)
                && (now - lastFired) < cooldown;

            PluginLog.Verbose(
                $"[ThrumHaptics][scroll] dt={(this._lastScrollMs == 0 ? 0 : now - this._lastScrollMs)}"
                + $" sub={this._scrollSubLineEvents}"
                + $" axis1={vertical} axis2={horizontal}"
                + $" pt1={CGEventGetIntegerValueField(@event, FieldScrollWheelEventPointDeltaAxis1)}"
                + $" pt2={horizontalPoints} px={this._thumbWheelPixels}"
                + $" cont={CGEventGetIntegerValueField(@event, FieldScrollWheelEventIsContinuous)}"
                + $" phase={CGEventGetIntegerValueField(@event, FieldScrollWheelEventScrollPhase)}"
                + $" -> {(suppressed ? "SUPPRESSED" : "fire")}");

            this._lastScrollMs = now;
            this._scrollSubLineEvents = 0;

            // SUBTRACT rather than zero, so leftover travel carries into the next
            // step. Zeroing loses distance on every tick and drifts slower than the
            // thumb actually moved - the same reasoning as the Windows accumulator.
            if (pixelStepReached)
            {
                this._thumbWheelPixels -= thumbWheelStep;
            }
            else if (horizontalScroll)
            {
                this._thumbWheelPixels = 0;
            }

            this.Fire(eventId, cooldown);
        }

        /// <summary>Plays an event's waveform, honouring its enable flag and rate limit.</summary>
        private void Fire(String eventId, Int64 cooldownMs)
        {
            if (eventId == null || !this._settings.IsEnabled(eventId))
            {
                return;
            }

            if (cooldownMs > 0)
            {
                var now = Environment.TickCount64;

                if (this._lastFiredMs.TryGetValue(eventId, out var last) && (now - last) < cooldownMs)
                {
                    return;
                }

                this._lastFiredMs[eventId] = now;
            }

            var waveform = this._settings.WaveformFor(eventId);

            if (cooldownMs > 0)
            {
                PluginLog.Verbose($"[ThrumHaptics] {eventId} -> {waveform}");
            }
            else
            {
                PluginLog.Info($"[ThrumHaptics] {eventId} -> {waveform}");
            }

            this._haptics.Play(waveform);
        }

        public void Stop()
        {
            if (this._thread == null)
            {
                return;
            }

            try
            {
                if (this._tap != IntPtr.Zero)
                {
                    CGEventTapEnable(this._tap, false);
                }

                if (this._diagnosticTap != IntPtr.Zero)
                {
                    CGEventTapEnable(this._diagnosticTap, false);
                }

                // Breaks CFRunLoopRun on the tap thread so it can exit.
                if (this._runLoop != IntPtr.Zero)
                {
                    CFRunLoopStop(this._runLoop);
                }

                // Bounded: a plugin reload must never block on this.
                this._thread.Join(TimeSpan.FromSeconds(2));

                // INVALIDATE before releasing, and only once the run loop thread
                // has exited. Releasing alone leaves the port registered and the
                // tap alive, which leaked one tap per plugin reload - and the
                // Plugin Service reloads this assembly on every rebuild.
                if (this._runLoopSource != IntPtr.Zero)
                {
                    CFRunLoopSourceInvalidate(this._runLoopSource);
                    CFRelease(this._runLoopSource);
                }

                if (this._tap != IntPtr.Zero)
                {
                    CFMachPortInvalidate(this._tap);
                    CFRelease(this._tap);
                }

                // Same invalidate-then-release order as the primary tap, and for the
                // same reason: releasing alone leaves the port registered and leaks
                // a live tap on every plugin reload.
                if (this._diagnosticRunLoopSource != IntPtr.Zero)
                {
                    CFRunLoopSourceInvalidate(this._diagnosticRunLoopSource);
                    CFRelease(this._diagnosticRunLoopSource);
                }

                if (this._diagnosticTap != IntPtr.Zero)
                {
                    CFMachPortInvalidate(this._diagnosticTap);
                    CFRelease(this._diagnosticTap);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[ThrumHaptics] macOS event tap teardown error: {ex.Message}");
            }
            finally
            {
                this._runLoopSource = IntPtr.Zero;
                this._runLoop = IntPtr.Zero;
                this._tap = IntPtr.Zero;
                this._diagnosticRunLoopSource = IntPtr.Zero;
                this._diagnosticTap = IntPtr.Zero;
                this._thread = null;

                // Released only after the run loop has stopped: CoreGraphics still
                // holds the function pointer until then.
                this._callback = null;
                this._diagnosticCallback = null;

                PluginLog.Info("[ThrumHaptics] macOS event tap stopped.");
            }
        }

        public void Dispose()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this.Stop();
        }
    }
}
