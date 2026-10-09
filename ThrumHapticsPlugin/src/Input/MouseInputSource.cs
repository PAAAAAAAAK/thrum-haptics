namespace Loupedeck.ThrumHapticsPlugin.Input
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;

    using Loupedeck.ThrumHapticsPlugin.Config;
    using Loupedeck.ThrumHapticsPlugin.Haptics;

    using SharpHook;
    using SharpHook.Data;

    /// <summary>
    /// Fires haptics on mouse buttons and scroll, in every application.
    /// </summary>
    /// <remarks>
    /// Buttons and scroll live in ONE source deliberately. SharpHook allows only a
    /// single IGlobalHook per process - libuiohook keeps its callback in static
    /// state, so starting a second hook corrupts the first. Splitting these into
    /// two IInputSource implementations would mean two hooks and a broken plugin,
    /// so anything driven by the global mouse hook belongs here. Stage 4's hover
    /// detection uses UI Automation instead, so that genuinely does get its own
    /// source.
    /// </remarks>
    internal sealed class MouseInputSource : IInputSource
    {
        private readonly HapticOutput _haptics;
        private readonly HapticSettings _settings;
        private IGlobalHook _hook;
        private Boolean _disposed;

        /// <summary>
        /// Minimum gap between two scroll haptics, per direction.
        /// </summary>
        /// <remarks>
        /// A scroll wheel produces events far faster than a human can register
        /// distinct taps, and the MX Master's free-spin mode can emit hundreds per
        /// second. Without a floor here the motor is asked to play a new waveform
        /// before the previous one has finished, which stops feeling like discrete
        /// detents and turns into one continuous buzz.
        ///
        /// Indexed by the user's density level, 1 (sparsest) to 5. Level 3 is the
        /// default at 35ms, roughly 28 haptics a second.
        ///
        /// The bottom of the range is bounded by the MOTOR, not by taste: asked to
        /// start a waveform before the last one has finished, it stops producing
        /// discrete taps and becomes one continuous buzz. 20ms is as far down as
        /// this goes for that reason, and level 5 is deliberately the edge rather
        /// than a comfortable setting.
        /// </remarks>
        private static readonly Int64[] ScrollCooldownByDensity = { 70, 50, 35, 25, 20 };

        /// <summary>
        /// Rotation units that count as one synthesized detent on the thumb wheel.
        /// </summary>
        /// <remarks>
        /// The vertical wheel has PHYSICAL detents, so in ratchet mode the hardware
        /// paces the events for us and a time-based cooldown is enough. The thumb
        /// wheel has no detents at all - it rolls smoothly - so pacing it by time
        /// produces a constant buzz at a fixed rate no matter how fast you roll it.
        /// That reads as vibration rather than ticks.
        ///
        /// Pacing by DISTANCE instead synthesizes the detents the hardware lacks:
        /// roll slowly and ticks come slowly, roll fast and they come fast, which is
        /// how a real ratchet behaves.
        ///
        /// MEASURED ON DEVICE: this mouse reports rotation = +/-360 per thumb-wheel
        /// event (delta = 120, the standard WHEEL_DELTA, so rotation runs at 3x
        /// that). One event therefore equals one scroll unit, and the wheel emits
        /// them faster than they read as separate taps - which is why an earlier
        /// threshold of 120, being below a single event's rotation, fired on every
        /// event and produced a continuous buzz.
        ///
        /// Indexed by density level. 360 equals exactly one event, so level 5 fires
        /// on every one; 1440 fires on every fourth.
        ///
        /// The intermediate values are NOT wasted, which I got wrong once and want
        /// to correct here: because the accumulator SUBTRACTS the threshold rather
        /// than zeroing, leftover rotation carries forward and thresholds between
        /// multiples of 360 produce fractional rates. 480 fires three times per four
        /// events, 540 twice per three. The scale is continuous, not stepped.
        /// </remarks>
        private static readonly Int32[] ThumbWheelUnitsByDensity = { 1440, 1080, 720, 480, 360 };

        private readonly Dictionary<String, Int64> _lastFiredMs = new();
        private Int32 _thumbWheelAccumulator;

        /// <summary>
        /// Pixels the cursor must travel while held before it counts as a drag.
        /// </summary>
        /// <remarks>
        /// Without this, a click with a few pixels of hand shake registers as a
        /// complete drag - you would feel drag start and drag end on top of the
        /// click haptic, for what was simply a click. Windows uses a similar idea
        /// for its own drag threshold (SM_CXDRAG, typically 4px).
        /// </remarks>
        private const Int32 DragThresholdPx = 5;

        /// <summary>
        /// Minimum drag duration before the "drag end" haptic is worth playing.
        /// </summary>
        /// <remarks>
        /// Start and end are meant to bracket a gesture, but on a very quick drag
        /// they land close enough together to blur into one indistinct buzz - the
        /// motor is often still finishing the first waveform when the second
        /// arrives. Below this threshold only the start plays: it has already
        /// signalled that a drag began, and one clean tap reads far better than
        /// two smeared ones.
        /// </remarks>
        private const Int64 DragMinSeparationMs = 150;

        /// <summary>Which buttons can start a drag, and the events they raise.</summary>
        /// <remarks>
        /// Back and forward are deliberately absent: holding a thumb button and
        /// moving is not a drag by any normal meaning of the word.
        /// </remarks>
        private static readonly Dictionary<MouseButton, (String Start, String End)> DragEvents = new()
        {
            [MouseButton.Button1] = (HapticEvents.DragLeftStart, HapticEvents.DragLeftEnd),
            [MouseButton.Button2] = (HapticEvents.DragRightStart, HapticEvents.DragRightEnd),
            [MouseButton.Button3] = (HapticEvents.DragMiddleStart, HapticEvents.DragMiddleEnd),
        };

        /// <summary>Per-button drag tracking. Buttons can be held simultaneously.</summary>
        private sealed class DragState
        {
            public Boolean ButtonDown;
            public Boolean Dragging;
            public Int16 PressX;
            public Int16 PressY;
            public Int64 StartMs;
        }

        private readonly Dictionary<MouseButton, DragState> _dragStates = new()
        {
            [MouseButton.Button1] = new DragState(),
            [MouseButton.Button2] = new DragState(),
            [MouseButton.Button3] = new DragState(),
        };

        /// <summary>
        /// How far the cursor must leave an edge before that edge can fire again.
        /// </summary>
        /// <remarks>
        /// Without hysteresis, a cursor parked against the edge would re-fire on
        /// every jittery pixel of movement. Requiring a clear retreat means one
        /// haptic per deliberate arrival at the boundary.
        /// </remarks>
        private const Int32 ScreenEdgeReleasePx = 8;

        private Boolean _atScreenEdge;

        /// <summary>How far past an edge to look for another display.</summary>
        private const Int32 EdgeProbePx = 2;

        // EVERY display, not their union - see AtWall. Cached because MouseMoved
        // fires constantly and this must stay cheap; re-read periodically so
        // docking, undocking or a resolution change is picked up without needing
        // a display-change hook.
        private DisplayRect[] _displays = Array.Empty<DisplayRect>();
        private Int64 _displaysReadMs;
        private const Int64 DisplaysTtlMs = 5000;

        /// <summary>A display's bounds. Right and Bottom are exclusive, as Win32 gives them.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayRect
        {
            public Int32 Left;
            public Int32 Top;
            public Int32 Right;
            public Int32 Bottom;
        }

        private delegate Boolean MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, ref DisplayRect bounds, IntPtr data);

        // Plain user32 rather than WinForms' Screen class: the plugin assembly must
        // not reference System.Windows.Forms. The Logi plugin verifier cannot
        // resolve desktop framework assemblies, so a WinForms reference here fails
        // packaging - which is why the settings window is a separate executable.
        [DllImport("user32.dll")]
        private static extern Boolean EnumDisplayMonitors(
            IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        private static readonly IntPtr PerMonitorAwareV2 = new(-4);

        public String Name => "Mouse";

        /// <summary>
        /// Says whether the device in use right now should drive haptics.
        /// </summary>
        /// <remarks>
        /// The hook cannot answer this itself: it sees input merged across every
        /// pointing device, with nothing to say which one moved. Null means no
        /// filtering, which is how this behaved before the question could be asked.
        /// </remarks>
        private readonly Func<Boolean> _deviceAllowsHaptics;

        public MouseInputSource(
            HapticOutput haptics, HapticSettings settings, Func<Boolean> deviceAllowsHaptics = null)
        {
            this._haptics = haptics ?? throw new ArgumentNullException(nameof(haptics));
            this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this._deviceAllowsHaptics = deviceAllowsHaptics;
        }

        /// <summary>
        /// Maps SharpHook's physical button numbering onto our event ids.
        /// </summary>
        /// <remarks>
        /// SharpHook numbers buttons physically, not semantically:
        ///   Button1 = left, Button2 = right, Button3 = middle/wheel,
        ///   Button4 = back (thumb), Button5 = forward (thumb).
        /// Everything downstream deals in event ids, so the settings store and the
        /// editor never need to know about SharpHook's enum.
        /// </remarks>
        private static String EventIdFor(MouseButton button) => button switch
        {
            MouseButton.Button1 => HapticEvents.MouseLeft,
            MouseButton.Button2 => HapticEvents.MouseRight,
            MouseButton.Button3 => HapticEvents.MouseMiddle,
            MouseButton.Button4 => HapticEvents.MouseBack,
            MouseButton.Button5 => HapticEvents.MouseForward,
            _ => null,
        };

        public void Start()
        {
            if (this._hook != null)
            {
                return;
            }

            // GlobalHookType.Mouse is a hard requirement, not a preference.
            //
            // On Windows this installs ONLY the low-level mouse hook (WH_MOUSE_LL).
            // The alternative, GlobalHookType.All, would also install WH_KEYBOARD_LL
            // - the exact API keyloggers use, which sees every keystroke including
            // passwords. That is the single biggest reason security software flags
            // software like this. We need button states and coordinates, never
            // keystrokes, so we never ask for the keyboard hook at all.
            //
            // EventLoopGlobalHook (rather than SimpleGlobalHook) runs our handlers
            // on its own event-loop thread instead of the hook callback thread.
            // That matters: Windows silently EVICTS a low-level hook whose callback
            // takes too long (LowLevelHooksTimeout, 300ms by default). Doing work
            // on the callback thread risks killing the hook for the whole session,
            // with no error - clicks would just stop buzzing.
            this._hook = new EventLoopGlobalHook(GlobalHookType.Mouse);

            this._hook.MousePressed += this.OnMousePressed;
            this._hook.MouseReleased += this.OnMouseReleased;
            this._hook.MouseDragged += this.OnMouseDragged;
            this._hook.MouseMoved += this.OnMouseMoved;
            this._hook.MouseWheel += this.OnMouseWheel;

            // HookEnabled is the only trustworthy confirmation that the OS actually
            // installed the hook. Logging straight after RunAsync would only prove
            // we ASKED for it - installation can still fail (permissions, another
            // libuiohook instance already running in this process).
            this._hook.HookEnabled += (_, _) =>
                PluginLog.Info("[ThrumHaptics] Mouse hook ENABLED - now receiving system-wide button and scroll events.");

            // Windows can evict a low-level hook without warning if a callback runs
            // long. Without this line that would look like "haptics randomly stopped".
            this._hook.HookDisabled += (_, _) =>
                PluginLog.Info("[ThrumHaptics] Mouse hook DISABLED.");

            // RunAsync so plugin loading is never blocked. Run() would block until
            // the hook is disposed, which would hang the Plugin Service on load.
            //
            // The returned Task must be observed: a plain fire-and-forget would
            // swallow any startup exception, leaving a plugin that loads "fine" and
            // silently never buzzes.
            var hookTask = this._hook.RunAsync();

            hookTask.ContinueWith(
                t => PluginLog.Error($"[ThrumHaptics] Mouse hook terminated with an error: {t.Exception}"),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);

            PluginLog.Info("[ThrumHaptics] Mouse hook starting (mouse-only, no keyboard hook)...");
        }

        private void OnMousePressed(Object sender, MouseHookEventArgs e)
        {
            // Fire on PRESS rather than release: the haptic should coincide with the
            // physical switch actuating, which is what makes it feel like part of
            // the click instead of a delayed response to it.
            if (this._dragStates.TryGetValue(e.Data.Button, out var state))
            {
                state.ButtonDown = true;
                state.Dragging = false;
                state.PressX = e.Data.X;
                state.PressY = e.Data.Y;
            }

            this.Fire(EventIdFor(e.Data.Button), cooldownMs: 0);
        }

        private void OnMouseReleased(Object sender, MouseHookEventArgs e)
        {
            if (!this._dragStates.TryGetValue(e.Data.Button, out var state))
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

            this.Fire(DragEvents[e.Data.Button].End, cooldownMs: 0);
        }

        private void OnMouseDragged(Object sender, MouseHookEventArgs e)
        {
            // The drag event does not reliably say WHICH button is held, so every
            // armed button is checked. Holding two at once is legal, if unusual.
            foreach (var (button, state) in this._dragStates)
            {
                if (!state.ButtonDown || state.Dragging)
                {
                    continue;
                }

                // A press on its own is a click; it only becomes a drag once the
                // cursor travels far enough while held. Firing on any movement
                // would turn a slightly shaky click into a full drag.
                var dx = Math.Abs(e.Data.X - state.PressX);
                var dy = Math.Abs(e.Data.Y - state.PressY);

                if (dx < DragThresholdPx && dy < DragThresholdPx)
                {
                    continue;
                }

                state.Dragging = true;
                state.StartMs = Environment.TickCount64;

                this.Fire(DragEvents[button].Start, cooldownMs: 0);
            }
        }

        private void OnMouseMoved(Object sender, MouseHookEventArgs e)
        {
            // Cheapest possible early-out: this runs on every pixel of cursor
            // movement, so it must do nothing at all when the feature is off.
            if (!this._settings.IsEnabled(HapticEvents.ScreenEdge))
            {
                return;
            }

            this.RefreshDisplays();

            // A low-level hook reports where the cursor is TRYING to go, which can
            // lie past the edge it is being held against. Pull the point back onto
            // the nearest display first, or pushing into a wall would read as
            // being on no display at all.
            if (!this.TryNearestDisplay(e.Data.X, e.Data.Y, out var display, out var x, out var y))
            {
                return;
            }

            if (this.AtWall(display, x, y, tolerance: 0))
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
            if (this._atScreenEdge && !this.AtWall(display, x, y, ScreenEdgeReleasePx))
            {
                this._atScreenEdge = false;
            }
        }

        /// <summary>
        /// Whether the cursor is against an edge it genuinely cannot cross.
        /// </summary>
        /// <remarks>
        /// The bounding box of all displays is the WRONG model, and this used to
        /// use it. Put a small monitor beside a tall one and the union is taller
        /// than the small one: while the cursor is on the small monitor it can
        /// never reach the union's top or bottom, because those y-coordinates only
        /// exist on the display next door. Those edges simply never fired.
        ///
        /// A union also cannot tell a wall from the seam between two monitors,
        /// where the cursor passes straight through and a haptic would be wrong.
        ///
        /// So the question is asked per edge of the display the cursor is actually
        /// on: is there another display just beyond it? If not, it is a wall. The
        /// same model, for the same reason, as MacMouseInputSource.AtWall - macOS
        /// got it first only because that is where the flaw was noticed.
        /// </remarks>
        private Boolean AtWall(DisplayRect d, Int32 x, Int32 y, Int32 tolerance)
        {
            if (x <= d.Left + tolerance && !this.AnyDisplayContains(d.Left - EdgeProbePx, y))
            {
                return true;
            }

            // Right and Bottom are exclusive, so the last addressable pixel is one
            // less than the edge.
            if (x >= d.Right - 1 - tolerance && !this.AnyDisplayContains(d.Right - 1 + EdgeProbePx, y))
            {
                return true;
            }

            if (y <= d.Top + tolerance && !this.AnyDisplayContains(x, d.Top - EdgeProbePx))
            {
                return true;
            }

            return y >= d.Bottom - 1 - tolerance && !this.AnyDisplayContains(x, d.Bottom - 1 + EdgeProbePx);
        }

        private Boolean AnyDisplayContains(Int32 x, Int32 y)
        {
            foreach (var d in this._displays)
            {
                if (x >= d.Left && x < d.Right && y >= d.Top && y < d.Bottom)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the display a point belongs to, clamping the point onto it.
        /// </summary>
        private Boolean TryNearestDisplay(
            Int32 rawX, Int32 rawY, out DisplayRect display, out Int32 x, out Int32 y)
        {
            display = default;
            x = rawX;
            y = rawY;

            var best = Int64.MaxValue;

            foreach (var d in this._displays)
            {
                var clampedX = Math.Clamp(rawX, d.Left, d.Right - 1);
                var clampedY = Math.Clamp(rawY, d.Top, d.Bottom - 1);

                Int64 dx = rawX - clampedX;
                Int64 dy = rawY - clampedY;

                var distance = (dx * dx) + (dy * dy);

                if (distance < best)
                {
                    best = distance;
                    display = d;
                    x = clampedX;
                    y = clampedY;
                }
            }

            return best != Int64.MaxValue;
        }

        /// <summary>Refreshes the cached bounds of every display if they are stale.</summary>
        private void RefreshDisplays()
        {
            var now = Environment.TickCount64;

            if (this._displays.Length > 0 && (now - this._displaysReadMs) <= DisplaysTtlMs)
            {
                return;
            }

            this._displaysReadMs = now;

            var found = new List<DisplayRect>();

            MonitorEnumProc collect = (IntPtr _, IntPtr _, ref DisplayRect bounds, IntPtr _) =>
            {
                found.Add(bounds);
                return true;
            };

            // The hook delivers PHYSICAL pixels whatever the host process's DPI
            // awareness is, while display bounds are scaled to match the calling
            // thread's. If the Plugin Service were not per-monitor aware, the two
            // would disagree on any scaled display and edges would fire in the
            // middle of the screen. Asking in per-monitor mode for the length of
            // this one call makes both sides speak physical pixels.
            var previous = IntPtr.Zero;

            try
            {
                previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            }
            catch (EntryPointNotFoundException)
            {
                // Before Windows 10 1607. Bounds come back in whatever the process
                // already uses, which is what this code relied on previously.
            }

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero);
            }
            finally
            {
                if (previous != IntPtr.Zero)
                {
                    SetThreadDpiAwarenessContext(previous);
                }

                // The callback is only ever invoked during the call above, but the
                // delegate must outlive it as far as the GC is concerned.
                GC.KeepAlive(collect);
            }

            if (found.Count > 0)
            {
                this._displays = found.ToArray();
            }
        }

        private void OnMouseWheel(Object sender, MouseWheelHookEventArgs e)
        {
            // Logged so the detent threshold can be tuned against what the device
            // actually reports rather than assumed - coarse vs high-resolution
            // wheels send very different rotation magnitudes.
            PluginLog.Verbose(
                $"[ThrumHaptics] wheel dir={e.Data.Direction} type={e.Data.Type} " +
                $"rotation={e.Data.Rotation} delta={e.Data.Delta}");

            var horizontal = e.Data.Direction == MouseWheelScrollDirection.Horizontal;
            var rotation = e.Data.Rotation;

            // Judged HERE, before any rotation is counted, and not left to Fire.
            // Both wheels pace themselves by accumulating rotation, and those
            // counters belong to the MX Master. Letting another mouse's scrolling
            // into them and refusing it only at the end meant its rotation had
            // already been banked - so the next few MX Master events tipped a
            // counter that somebody else had filled, and buzzed on its behalf.
            if (this._deviceAllowsHaptics != null && !this._deviceAllowsHaptics())
            {
                this.LookAgainOnceTheDeviceIsKnown(() => this.CountWheel(horizontal, rotation));
                return;
            }

            this.CountWheel(horizontal, rotation);
        }

        private readonly Object _wheelGate = new();

        /// <summary>Adds one wheel event's rotation to the right counter.</summary>
        private void CountWheel(Boolean horizontal, Int32 rotation)
        {
            // Locked because a second look arrives on a pool thread while the hook
            // thread may be counting too.
            lock (this._wheelGate)
            {
                if (horizontal)
                {
                    this.FireThumbWheel(rotation);
                }
                else
                {
                    this.FireVerticalWheel(rotation);
                }
            }
        }

        /// <summary>
        /// Rotation units in one physical detent of the vertical wheel.
        /// </summary>
        /// <remarks>
        /// MEASURED: this wheel reports rotation in multiples of 360, one per
        /// detent, and delta is always the standard WHEEL_DELTA of 120.
        ///
        /// It does NOT do that when Logi Options+ has SMOOTH SCROLLING enabled.
        /// Options+ then splits each detent into a stream of fractional events -
        /// rotation 3, 6, 9, 12, 15, 18, 21, 24 out of the same 360 - so that pages
        /// glide. It applies per application, which is why this appeared in Chrome
        /// and Edge and nowhere else.
        ///
        /// Firing per EVENT therefore produced dozens of haptics per detent,
        /// limited only by the rate limit - so slow and fast scrolling both came
        /// out as the same continuous buzz, and the tail of the glide kept buzzing
        /// after the wheel had stopped.
        ///
        /// Counting rotation instead makes both modes agree: one haptic per detent,
        /// whether the OS delivers that detent as one event or thirty.
        /// </remarks>
        private const Int32 VerticalDetentUnits = 360;

        /// <summary>Rotation still owed before the next detent's haptic is due.</summary>
        private Int32 _verticalWheelPending;

        /// <summary>Paces vertical-wheel haptics by detents, not by events.</summary>
        /// <remarks>
        /// Fires on the LEADING edge of each detent - the haptic plays first, and
        /// the rotation it represents is charged afterwards.
        ///
        /// Accumulating first and firing on arrival at 360 is the obvious way round
        /// and it feels late. Under smooth scrolling the units arrive as a 60Hz
        /// stream of roughly 111, so a detent takes three or four events to
        /// complete - and waiting for the last of them puts every haptic 50-70ms
        /// behind the wheel. Firing on the leading edge spends the same total
        /// rotation per tick and puts each one at the start of its detent instead.
        /// </remarks>
        private void FireVerticalWheel(Int32 rotation)
        {
            if (this._verticalWheelPending <= 0)
            {
                this.Fire(HapticEvents.ScrollVertical, this.CooldownFor(HapticEvents.ScrollVertical));
                this._verticalWheelPending += VerticalDetentUnits;
            }

            this._verticalWheelPending -= Math.Abs(rotation);

            // Free-spin delivers 720 in a single event - two detents at once - and
            // the motor cannot play two haptics simultaneously. Without this floor
            // the debt would run away and every later event would fire regardless
            // of how far the wheel had actually turned.
            if (this._verticalWheelPending < -VerticalDetentUnits)
            {
                this._verticalWheelPending = -VerticalDetentUnits;
            }
        }

        /// <summary>
        /// Paces thumb-wheel haptics by distance rolled, synthesizing detents.
        /// </summary>
        private void FireThumbWheel(Int32 rotation)
        {
            // Direction is irrelevant to the accumulator - rolling back and forth
            // should still tick. Reset on reversal would suppress ticks exactly
            // when the user is scrubbing, which is when feedback matters most.
            var step = ThumbWheelUnitsByDensity[
                HapticEvents.ClampDensity(this._settings.DensityFor(HapticEvents.ScrollHorizontal)) - 1];

            this._thumbWheelAccumulator += Math.Abs(rotation);

            if (this._thumbWheelAccumulator < step)
            {
                return;
            }

            // Remainder rather than zero, so leftover rotation carries into the next
            // detent. Zeroing would slowly lose distance and drift slower than the
            // real movement; a single subtraction would let the accumulator grow
            // without limit once the threshold drops below one event's rotation,
            // which the densest setting does.
            this._thumbWheelAccumulator %= step;

            // Still honour a time floor: a very fast flick could otherwise cross
            // several detents' worth between events and outrun the motor.
            this.Fire(HapticEvents.ScrollHorizontal, this.CooldownFor(HapticEvents.ScrollHorizontal));
        }

        /// <summary>The time floor for an event at the user's chosen density.</summary>
        private Int64 CooldownFor(String eventId) =>
            ScrollCooldownByDensity[HapticEvents.ClampDensity(this._settings.DensityFor(eventId)) - 1];

        /// <summary>
        /// Plays an event's waveform, honouring its enable flag and rate limit.
        /// </summary>
        private void Fire(String eventId, Int64 cooldownMs)
        {
            if (eventId == null || !this._settings.IsEnabled(eventId))
            {
                return; // Unsupported input, or turned off by the user.
            }

            // Another mouse, or a trackpad. Checked here rather than at each caller
            // so no path to the motor can forget it.
            if (this._deviceAllowsHaptics != null && !this._deviceAllowsHaptics())
            {
                this.LookAgainOnceTheDeviceIsKnown(() => this.Play(eventId, cooldownMs));
                return;
            }

            this.Play(eventId, cooldownMs);
        }

        /// <summary>How long to wait before judging a held-back haptic a second time.</summary>
        private const Int32 DeviceSettleMs = 8;

        private Int32 _secondLookPending;

        /// <summary>
        /// Gives a held-back haptic one more chance, a moment later.
        /// </summary>
        /// <remarks>
        /// The hook and the device tracker hear about the same physical event on
        /// two different threads, in no guaranteed order. So when this is reached,
        /// "the wrong device" can simply mean the tracker has not caught up yet -
        /// and that is exactly the case at the moment of switching back to the
        /// MX Master, where on device it cost the first click its haptic.
        ///
        /// Only a haptic that was about to be DROPPED waits. Everything the tracker
        /// already allows plays at once, so ordinary use pays nothing for this; the
        /// cost is one haptic arriving a hundredth of a second late, once, at the
        /// instant of changing device.
        ///
        /// One look at a time: someone scrolling the other mouse sends a hundred
        /// held-back events a second, and each queueing its own timer would be a
        /// lot of work to conclude "still the other mouse".
        /// </remarks>
        private void LookAgainOnceTheDeviceIsKnown(Action whenAllowed)
        {
            if (Interlocked.CompareExchange(ref this._secondLookPending, 1, 0) != 0)
            {
                return;
            }

            Task.Delay(DeviceSettleMs).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref this._secondLookPending, 0);

                try
                {
                    if (!this._disposed && this._deviceAllowsHaptics())
                    {
                        whenAllowed();
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"[ThrumHaptics] Second look failed: {ex.Message}");
                }
            });
        }

        private readonly Object _playGate = new();

        /// <summary>Applies the rate limit and plays. Callable from any thread.</summary>
        private void Play(String eventId, Int64 cooldownMs)
        {
            if (cooldownMs > 0)
            {
                // Cooldown is tracked PER EVENT, so scrolling the main wheel never
                // suppresses a thumbwheel tick (or a click) that happens to land in
                // the same window. Environment.TickCount64 is used rather than
                // DateTime.UtcNow because this runs on the input path and needs to
                // be cheap and monotonic - DateTime jumps when the clock changes.
                var now = Environment.TickCount64;

                // Locked because a second look arrives on a pool thread while the
                // hook thread may be in here too, and Dictionary tolerates neither.
                lock (this._playGate)
                {
                    if (this._lastFiredMs.TryGetValue(eventId, out var last) && (now - last) < cooldownMs)
                    {
                        return;
                    }

                    this._lastFiredMs[eventId] = now;
                }
            }

            var waveform = this._settings.WaveformFor(eventId);

            // Scroll fires orders of magnitude more often than clicks - one short
            // roll produced several hundred events - so it logs at Verbose to keep
            // the shared Plugin Service log readable. Clicks stay at Info.
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
            if (this._hook == null)
            {
                return;
            }

            this._hook.MousePressed -= this.OnMousePressed;
            this._hook.MouseReleased -= this.OnMouseReleased;
            this._hook.MouseDragged -= this.OnMouseDragged;
            this._hook.MouseMoved -= this.OnMouseMoved;
            this._hook.MouseWheel -= this.OnMouseWheel;

            // Dispose rather than Stop: a disposed hook cannot be restarted, and we
            // always build a fresh one in Start(). Leaving a hook alive across a
            // plugin reload would leak a global hook into an unloaded assembly - and
            // since only one hook may exist per process, that would also stop the
            // reloaded plugin from ever working.
            this._hook.Dispose();
            this._hook = null;

            PluginLog.Info("[ThrumHaptics] Mouse hook stopped.");
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
