namespace Loupedeck.ThrumHapticsPlugin.Input
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;

    /// <summary>
    /// Knows which pointing device the user is currently using, on Windows.
    /// </summary>
    /// <remarks>
    /// The low-level hook that drives haptics delivers input already merged across
    /// every pointing device, so a second mouse or a trackpad used to buzz the
    /// MX Master as readily as the MX Master itself. Raw Input is the one Windows
    /// API that tags each event with the device it came from.
    ///
    /// This does NOT replace the hook. It runs beside it and answers one question -
    /// where did the most recent mouse input come from? - which the hook's input
    /// source consults before it plays anything. Keeping the hook as the source of
    /// events means everything it already gets right stays right: thumb buttons,
    /// the detent counting that survives Options+ smooth scrolling, cursor
    /// position for screen edges.
    ///
    /// MEASURED on device, with Logi Options+ running:
    ///
    ///   - The Plugin Service has not registered for mouse Raw Input itself. That
    ///     matters because a process gets ONE target per device class - claiming
    ///     it again would silently take the host's input. It is still checked on
    ///     every start, and this stands down if that ever changes.
    ///   - An MX Master 4 on Bluetooth reports as VID&amp;02046d, and each of its
    ///     events arrives within a millisecond or two of the hook's.
    ///   - Another vendor's mouse can show up as TWO devices: the physical one for
    ///     movement, and a virtual one its driver re-posts clicks and wheel
    ///     through. Neither carries Logitech's vendor id, so both are ignored.
    ///   - The MX Master's BACK AND FORWARD buttons arrive with NO device at all.
    ///     Options+ injects them, on Windows just as it converts them on macOS.
    ///
    /// That last one decides the model. The first version judged injected input by
    /// whichever real device had been used before it, which sounds reasonable and
    /// failed at once: after touching the other mouse, the thumb buttons had no
    /// hardware event of their own to win the answer back, so press after press
    /// was dropped until the MX Master happened to move. So every event is judged
    /// on what it is, not on what came before: Logitech hardware and injected
    /// input drive haptics, other hardware does not.
    ///
    /// Injected input is allowed rather than refused because the thing injecting
    /// mouse input on this machine is overwhelmingly Options+, acting for the very
    /// mouse we want. Touch and pen are the exception - Windows turns those into
    /// injected mouse input too - and they carry a signature that marks them out.
    ///
    /// FAILS OPEN. If this cannot start, or has seen nothing yet, haptics are
    /// allowed exactly as before. A filter that falls silent when it is unsure
    /// would turn a diagnostics problem into a plugin that appears dead.
    ///
    /// The hook and Raw Input hear about one physical event on two threads, in no
    /// guaranteed order. MouseInputSource covers that from its side by giving a
    /// haptic it was about to drop a second look a moment later.
    /// </remarks>
    internal sealed class RawInputDeviceTracker : IInputSource
    {
        private const String Tag = "[ThrumHaptics][device]";

        private const UInt32 WM_DESTROY = 0x0002;
        private const UInt32 WM_CLOSE = 0x0010;
        private const UInt32 WM_INPUT = 0x00FF;

        private const UInt16 UsagePageGenericDesktop = 0x01;
        private const UInt16 UsageMouse = 0x02;

        // Deliver input even when our window is not in the foreground. It never
        // is: this is a message-only window inside a background service.
        private const UInt32 RIDEV_INPUTSINK = 0x00000100;
        private const UInt32 RIDEV_REMOVE = 0x00000001;

        private const UInt32 RID_INPUT = 0x10000003;
        private const UInt32 RIDI_DEVICENAME = 0x20000007;
        private const UInt32 RIM_TYPEMOUSE = 0;

        private static readonly IntPtr MessageOnlyParent = new(-3);

        /// <summary>Logitech's USB vendor id, in each form a device path spells it.</summary>
        /// <remarks>
        /// USB and receivers write VID_046D; Bluetooth LE writes VID&amp;02046d and
        /// classic Bluetooth VID&amp;0002046d, the extra digits naming who assigned
        /// the id. Matching the vendor rather than one product keeps this working
        /// through a Bolt receiver, whose path carries the RECEIVER's product id
        /// and not the mouse's.
        /// </remarks>
        private static readonly Regex LogitechVendor =
            new(@"VID(_|&[0-9A-F]{2,4})046D", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public UInt32 Size;
            public UInt32 Style;
            public IntPtr WindowProcedure;
            public Int32 ClassExtra;
            public Int32 WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public String MenuName;
            public String ClassName;
            public IntPtr SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public IntPtr Window;
            public UInt32 Id;
            public IntPtr WParam;
            public IntPtr LParam;
            public UInt32 Time;
            public Int32 X;
            public Int32 Y;
            public UInt32 Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public UInt16 UsagePage;
            public UInt16 Usage;
            public UInt32 Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputHeader
        {
            public UInt32 Type;
            public UInt32 Size;
            public IntPtr Device;
            public IntPtr WParam;
        }

        // The button fields are a union in the Win32 header, hence the offsets.
        [StructLayout(LayoutKind.Explicit)]
        private struct RawMouse
        {
            [FieldOffset(0)] public UInt16 Flags;
            [FieldOffset(4)] public UInt16 ButtonFlags;
            [FieldOffset(6)] public UInt16 ButtonData;
            [FieldOffset(8)] public UInt32 RawButtons;
            [FieldOffset(12)] public Int32 LastX;
            [FieldOffset(16)] public Int32 LastY;
            [FieldOffset(20)] public UInt32 ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInput
        {
            public RawInputHeader Header;
            public RawMouse Mouse;
        }

        private delegate IntPtr WindowProcedure(IntPtr window, UInt32 message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern UInt16 RegisterClassExW(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Boolean UnregisterClassW(String className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            UInt32 extendedStyle, String className, String windowName, UInt32 style,
            Int32 x, Int32 y, Int32 width, Int32 height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern Boolean DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr window, UInt32 message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern Int32 GetMessageW(out Message message, IntPtr window, UInt32 filterMin, UInt32 filterMax);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref Message message);

        [DllImport("user32.dll")]
        private static extern Boolean PostMessageW(IntPtr window, UInt32 message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(Int32 exitCode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern Boolean RegisterRawInputDevices(
            [In] RawInputDevice[] devices, UInt32 count, UInt32 size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern UInt32 GetRegisteredRawInputDevices(
            [Out] RawInputDevice[] devices, ref UInt32 count, UInt32 size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern UInt32 GetRawInputData(
            IntPtr rawInput, UInt32 command, out RawInput data, ref UInt32 size, UInt32 headerSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern UInt32 GetRawInputDeviceInfoW(
            IntPtr device, UInt32 command, StringBuilder data, ref UInt32 size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(String moduleName);

        private readonly String _className = "ThrumHapticsDeviceTracker." + Guid.NewGuid().ToString("N");

        // Held in a field: the window class keeps a raw function pointer the GC
        // knows nothing about, so a local delegate would be collected under it.
        private WindowProcedure _windowProcedure;

        private Thread _thread;
        private IntPtr _window;
        private Boolean _registered;
        private Boolean _disposed;

        // Only ever touched on the tracker's own thread.
        private readonly Dictionary<IntPtr, Boolean> _isLogitechByDevice = new();
        private IntPtr _lastDevice;
        private Boolean _lastDeviceIsLogitech;

        /// <summary>Where a mouse event came from.</summary>
        private enum Source
        {
            Nothing,
            Logitech,
            Injected,
            Other,
        }

        // Written on the tracker thread, read on the hook's. An Int32 rather than
        // the enum itself only because that is what `volatile` accepts.
        private volatile Boolean _listening;
        private volatile Int32 _lastSource = (Int32)Source.Nothing;

        public String Name => "Device tracker (Raw Input)";

        /// <summary>
        /// Whether a haptic should play for input arriving now.
        /// </summary>
        /// <remarks>
        /// False only when this is running AND the most recent mouse input came from
        /// hardware that is not Logitech's. Every unknown resolves to "allow".
        /// </remarks>
        public Boolean AllowsHaptics =>
            !this._listening || this._lastSource != (Int32)Source.Other;

        public void Start()
        {
            if (this._thread != null || !OperatingSystem.IsWindows())
            {
                return;
            }

            // The window and its message loop must live on the same thread, and the
            // loop blocks, so it needs a thread of its own.
            this._thread = new Thread(this.Run)
            {
                IsBackground = true,
                Name = "ThrumHaptics.DeviceTracker",
            };

            this._thread.Start();
        }

        private void Run()
        {
            try
            {
                if (HostAlreadyOwnsMouseRawInput())
                {
                    PluginLog.Info(
                        $"{Tag} The Plugin Service has registered for mouse Raw Input itself. Registering here "
                        + "would take its input away, so per-device filtering is OFF and every pointer drives "
                        + "haptics as before.");
                    return;
                }

                this._windowProcedure = this.OnMessage;

                var instance = GetModuleHandleW(null);

                var windowClass = new WindowClass
                {
                    Size = (UInt32)Marshal.SizeOf<WindowClass>(),
                    WindowProcedure = Marshal.GetFunctionPointerForDelegate(this._windowProcedure),
                    Instance = instance,
                    ClassName = this._className,
                };

                if (RegisterClassExW(ref windowClass) == 0)
                {
                    PluginLog.Error($"{Tag} RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
                    return;
                }

                this._window = CreateWindowExW(
                    0, this._className, "Thrum Haptics device tracker", 0,
                    0, 0, 0, 0, MessageOnlyParent, IntPtr.Zero, instance, IntPtr.Zero);

                if (this._window == IntPtr.Zero)
                {
                    PluginLog.Error($"{Tag} CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
                    return;
                }

                var devices = new[]
                {
                    new RawInputDevice
                    {
                        UsagePage = UsagePageGenericDesktop,
                        Usage = UsageMouse,
                        Flags = RIDEV_INPUTSINK,
                        Target = this._window,
                    },
                };

                if (!RegisterRawInputDevices(devices, 1, (UInt32)Marshal.SizeOf<RawInputDevice>()))
                {
                    PluginLog.Error($"{Tag} RegisterRawInputDevices failed: {Marshal.GetLastWin32Error()}");
                    return;
                }

                this._registered = true;
                this._listening = true;

                PluginLog.Info($"{Tag} Per-device filtering is ON: only Logitech pointers drive haptics.");

                while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    DispatchMessageW(ref message);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"{Tag} failed, so every pointer drives haptics as before: {ex}");
            }
            finally
            {
                // Before anything else: once this stops listening its last answer
                // goes stale, and a stale "not Logitech" would silence the plugin.
                this._listening = false;

                if (this._registered)
                {
                    var remove = new[]
                    {
                        new RawInputDevice
                        {
                            UsagePage = UsagePageGenericDesktop,
                            Usage = UsageMouse,
                            Flags = RIDEV_REMOVE,
                            Target = IntPtr.Zero,
                        },
                    };

                    RegisterRawInputDevices(remove, 1, (UInt32)Marshal.SizeOf<RawInputDevice>());
                    this._registered = false;
                }

                UnregisterClassW(this._className, GetModuleHandleW(null));
            }
        }

        /// <summary>Whether this process already has a Raw Input target for the mouse.</summary>
        private static Boolean HostAlreadyOwnsMouseRawInput()
        {
            var size = (UInt32)Marshal.SizeOf<RawInputDevice>();

            UInt32 count = 0;
            GetRegisteredRawInputDevices(null, ref count, size);

            if (count == 0)
            {
                return false;
            }

            var registered = new RawInputDevice[count];

            if (GetRegisteredRawInputDevices(registered, ref count, size) == UInt32.MaxValue)
            {
                return false;
            }

            foreach (var device in registered)
            {
                if (device.UsagePage == UsagePageGenericDesktop && device.Usage == UsageMouse)
                {
                    return true;
                }
            }

            return false;
        }

        private IntPtr OnMessage(IntPtr window, UInt32 message, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (message)
                {
                    case WM_INPUT:
                        this.OnRawInput(lParam);
                        break;

                    case WM_CLOSE:
                        DestroyWindow(window);
                        return IntPtr.Zero;

                    case WM_DESTROY:
                        PostQuitMessage(0);
                        return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                // Never let an exception unwind into user32.
                PluginLog.Error($"{Tag} window procedure error: {ex.Message}");
            }

            // WM_INPUT must still reach DefWindowProc, which frees the input buffer.
            return DefWindowProcW(window, message, wParam, lParam);
        }

        private void OnRawInput(IntPtr handle)
        {
            var size = (UInt32)Marshal.SizeOf<RawInput>();

            if (GetRawInputData(handle, RID_INPUT, out var input, ref size, (UInt32)Marshal.SizeOf<RawInputHeader>())
                == UInt32.MaxValue)
            {
                return;
            }

            if (input.Header.Type != RIM_TYPEMOUSE)
            {
                return;
            }

            var device = input.Header.Device;

            var flags = input.Mouse.ButtonFlags;
            var now = Environment.TickCount64;

            Source source;

            if (device != IntPtr.Zero)
            {
                source = this.IsLogitech(device) ? Source.Logitech : Source.Other;

                // A lone movement report from another mouse is not somebody using
                // it. MEASURED: an idle second mouse sent one to four stray reports
                // a second while only the MX Master was in use. Clicks and wheel
                // turns are deliberate and count at once; movement has to keep
                // coming, which real use does within a few hundredths of a second.
                //
                // This matters more than it looks. The thumb wheel cannot answer
                // back for a quarter of a second after another device speaks, so
                // every stray report would cost it a tick.
                if (source == Source.Other && flags == 0)
                {
                    this._otherMoveStreak = (now - this._otherMoveLastMs) <= OtherMoveWindowMs
                        ? this._otherMoveStreak + 1
                        : 1;

                    this._otherMoveLastMs = now;

                    if (this._otherMoveStreak < OtherMovesToCount)
                    {
                        return;
                    }
                }
            }
            else if ((input.Mouse.ExtraInformation & TouchOrPenMask) == TouchOrPenSignature)
            {
                // Touch and pen reach Windows as injected mouse input too, and they
                // are not the MX Master. Windows stamps them with a signature.
                source = Source.Other;
            }
            else if ((flags & WheelFlags) != 0)
            {
                // An injected WHEEL event. MEASURED: this is the only form the
                // MX Master's thumb wheel ever takes - it produces no hardware
                // event at all - so it has to be able to speak for the mouse.
                //
                // But it cannot always. With smooth scrolling on, Options+ keeps
                // posting these for about a second after the wheel has stopped -
                // the glide - and a glide answering "Logitech" on every frame let
                // another mouse's scrolling buzz for as long as it lasted.
                //
                // What separates the two is whether the OTHER device is still in
                // use. Someone who has moved to it is touching it now; someone
                // rolling the MX Master's wheel has let go of it, and a hand takes
                // longer than this to change mice. So a wheel frame speaks for the
                // MX Master only once the other device has gone quiet.
                //
                // Judging a stream once at its start was tried first and was worse:
                // a second roll runs straight into the glide of the first, there is
                // no start to see, and the thumb wheel stayed mute until the mouse
                // was moved.
                if ((now - this._lastOtherDeviceMs) <= OtherDeviceQuietMs)
                {
                    return;
                }

                source = Source.Injected;
            }
            else if (flags != 0)
            {
                // An injected BUTTON: the MX Master's back and forward, which
                // Options+ posts on its behalf. A press is deliberate and has no
                // tail, so it always counts.
                source = Source.Injected;
            }
            else
            {
                return; // Injected movement says nothing about who is scrolling or clicking.
            }

            if (source == Source.Other)
            {
                this._lastOtherDeviceMs = now;
            }

            var previous = (Source)this._lastSource;

            if (source == previous)
            {
                return;
            }

            this._lastSource = (Int32)source;

            // Logged only when the ANSWER changes, which is rare, and not on every
            // hop between the mouse and its own injected buttons, which is not.
            if ((previous == Source.Other) != (source == Source.Other))
            {
                PluginLog.Info(
                    $"{Tag} input now from "
                    + (source == Source.Other ? "another device; haptics paused." : "the Logitech pointer; haptics on."));
            }
        }

        // What Windows writes into the extra-information field of mouse input it
        // synthesised from a touch or a pen stroke.
        private const UInt32 TouchOrPenMask = 0xFFFFFF00;
        private const UInt32 TouchOrPenSignature = 0xFF515700;

        // RI_MOUSE_WHEEL | RI_MOUSE_HWHEEL.
        private const UInt16 WheelFlags = 0x0400 | 0x0800;

        /// <summary>How long another device must be silent before injected scrolling counts.</summary>
        /// <remarks>
        /// Long enough to outlast the gaps inside ordinary use of another mouse -
        /// movement reports arrive every few milliseconds, wheel notches within a
        /// couple of hundred - and shorter than it takes a hand to leave one mouse
        /// and start rolling a wheel on another.
        /// </remarks>
        private const Int64 OtherDeviceQuietMs = 250;

        private Int64 _lastOtherDeviceMs;

        // Movement from another device counts once this many reports arrive close
        // together. A mouse in use sends over a hundred a second.
        private const Int32 OtherMovesToCount = 3;
        private const Int64 OtherMoveWindowMs = 250;

        private Int32 _otherMoveStreak;
        private Int64 _otherMoveLastMs;

        /// <summary>Whether a device is a Logitech pointer, resolved once per device.</summary>
        private Boolean IsLogitech(IntPtr device)
        {
            // This runs for every mouse movement, hundreds of times a second, so the
            // common case - same device as last time - costs one comparison.
            if (device == this._lastDevice)
            {
                return this._lastDeviceIsLogitech;
            }

            if (!this._isLogitechByDevice.TryGetValue(device, out var isLogitech))
            {
                var name = DeviceName(device);

                // A device whose name cannot be read is allowed, not ignored. It
                // might be the MX Master, and silencing that is the worse mistake.
                isLogitech = name.Length == 0 || LogitechVendor.IsMatch(name);
                this._isLogitechByDevice[device] = isLogitech;

                PluginLog.Info(
                    $"{Tag} 0x{device.ToInt64():X} {(isLogitech ? "DRIVES haptics" : "is ignored")}: "
                    + (name.Length == 0 ? "(name unavailable)" : name));
            }

            this._lastDevice = device;
            this._lastDeviceIsLogitech = isLogitech;

            return isLogitech;
        }

        private static String DeviceName(IntPtr device)
        {
            UInt32 length = 0;
            GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, null, ref length);

            var buffer = new StringBuilder((Int32)length + 1);

            return GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, buffer, ref length) == UInt32.MaxValue
                ? String.Empty
                : buffer.ToString();
        }

        public void Stop()
        {
            if (this._thread == null)
            {
                return;
            }

            try
            {
                this._listening = false;

                if (this._window != IntPtr.Zero)
                {
                    // Posted, not sent: the window belongs to the tracker thread, and
                    // closing it there is what ends its message loop.
                    PostMessageW(this._window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }

                // Bounded: a plugin reload must never block on this.
                this._thread.Join(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                PluginLog.Error($"{Tag} teardown error: {ex.Message}");
            }
            finally
            {
                this._window = IntPtr.Zero;
                this._thread = null;
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
