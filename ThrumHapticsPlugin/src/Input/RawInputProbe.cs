namespace Loupedeck.ThrumHapticsPlugin.Input
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;

    /// <summary>
    /// SPIKE - logs Windows Raw Input for the mouse and does nothing else.
    /// </summary>
    /// <remarks>
    /// Not a feature. It fires no haptics and changes no behaviour; it exists to
    /// answer whether per-device filtering is possible at all before anything is
    /// built on the assumption that it is.
    ///
    /// The low-level hook the plugin uses today delivers input already merged
    /// across every pointing device, so a trackpad or a second mouse drives the
    /// motor too. Raw Input is the one Windows API that tags each event with the
    /// device it came from. Three things have to be true for it to be usable, and
    /// none of them is known:
    ///
    ///   1. The MX Master's events arrive with a device handle that tells it apart
    ///      from other pointers - while Logi Options+ is running, which is the
    ///      only condition that matters.
    ///   2. The physical wheel still arrives when Options+ smooth scrolling is on.
    ///      That feature replaces the wheel stream the hook sees with an eased
    ///      animation; injected input reaches Raw Input with a NULL device, so the
    ///      real detents may be separable here where they are not there.
    ///   3. Registering does not take input away from the host. A process gets
    ///      ONE Raw Input target per device class, so if LogiPluginService has
    ///      already claimed the mouse, claiming it again would silently reroute
    ///      its input to this window. That is checked first, and this probe
    ///      stands down rather than find out the hard way.
    ///
    /// Delete this file once the question is answered either way.
    /// </remarks>
    internal sealed class RawInputProbe : IInputSource
    {
        private const String Tag = "[ThrumHaptics][raw]";

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

        private readonly String _className = "ThrumHapticsRawInputProbe." + Guid.NewGuid().ToString("N");

        // Held in a field: the window class keeps a raw function pointer the GC
        // knows nothing about, so a local delegate would be collected under it.
        private WindowProcedure _windowProcedure;

        private Thread _thread;
        private IntPtr _window;
        private Boolean _registered;
        private Boolean _disposed;

        private readonly Dictionary<IntPtr, String> _deviceNames = new();
        private readonly Dictionary<IntPtr, Int32> _movesByDevice = new();
        private Int64 _movesFlushedMs;

        public String Name => "Raw Input probe (diagnostic)";

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
                Name = "ThrumHaptics.RawInputProbe",
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
                        $"{Tag} RESULT: the Plugin Service has ALREADY registered for mouse Raw Input. "
                        + "A process gets one target per device class, so registering here would take its "
                        + "input away. Probe standing down - Raw Input is not usable in-process.");
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
                    0, this._className, "Thrum Haptics Raw Input probe", 0,
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

                PluginLog.Info($"{Tag} listening. Mouse Raw Input is being LOGGED ONLY - no haptics come from here.");

                while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    DispatchMessageW(ref message);
                }

                PluginLog.Info($"{Tag} message loop exited.");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"{Tag} failed: {ex}");
            }
            finally
            {
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
                PluginLog.Info(
                    $"{Tag} host registration: page=0x{device.UsagePage:X2} usage=0x{device.Usage:X2} "
                    + $"flags=0x{device.Flags:X} target=0x{device.Target.ToInt64():X}");

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
            var mouse = input.Mouse;

            this.Describe(device);

            if (mouse.ButtonFlags == 0)
            {
                // Pure movement arrives hundreds of times a second. Which DEVICE is
                // moving is the whole question, so it is counted per device and
                // summarised once a second rather than logged event by event.
                this._movesByDevice[device] = this._movesByDevice.GetValueOrDefault(device) + 1;
            }
            else
            {
                // ButtonData is the wheel delta when a wheel flag is set: signed,
                // 120 per detent, or a fraction of that from a high-resolution wheel.
                PluginLog.Info(
                    $"{Tag} dev={Short(device)} flags=0x{mouse.ButtonFlags:X4} ({Decode(mouse.ButtonFlags)}) "
                    + $"data={unchecked((Int16)mouse.ButtonData)} extra=0x{mouse.ExtraInformation:X}");
            }

            this.FlushMoves();
        }

        private void FlushMoves()
        {
            var now = Environment.TickCount64;

            if ((now - this._movesFlushedMs) < 1000 || this._movesByDevice.Count == 0)
            {
                return;
            }

            this._movesFlushedMs = now;

            foreach (var entry in this._movesByDevice)
            {
                PluginLog.Info($"{Tag} moves in the last second: dev={Short(entry.Key)} x{entry.Value}");
            }

            this._movesByDevice.Clear();
        }

        /// <summary>Logs a device's name the first time it is seen.</summary>
        private void Describe(IntPtr device)
        {
            if (this._deviceNames.ContainsKey(device))
            {
                return;
            }

            var name = "(NULL device - injected or synthesised, not hardware)";

            if (device != IntPtr.Zero)
            {
                UInt32 length = 0;
                GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, null, ref length);

                var buffer = new StringBuilder((Int32)length + 1);

                name = GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, buffer, ref length) == UInt32.MaxValue
                    ? "(name unavailable)"
                    : buffer.ToString();
            }

            this._deviceNames[device] = name;

            // The name carries the vendor and product ids - VID_046D is Logitech -
            // which is what a real filter would match on.
            PluginLog.Info($"{Tag} NEW DEVICE {Short(device)} = {name}");
        }

        private static String Short(IntPtr device) => "0x" + device.ToInt64().ToString("X");

        private static String Decode(UInt16 flags)
        {
            var names = new List<String>();

            void Add(UInt16 bit, String label)
            {
                if ((flags & bit) != 0)
                {
                    names.Add(label);
                }
            }

            Add(0x0001, "left-down");
            Add(0x0002, "left-up");
            Add(0x0004, "right-down");
            Add(0x0008, "right-up");
            Add(0x0010, "middle-down");
            Add(0x0020, "middle-up");
            Add(0x0040, "button4-down");
            Add(0x0080, "button4-up");
            Add(0x0100, "button5-down");
            Add(0x0200, "button5-up");
            Add(0x0400, "WHEEL");
            Add(0x0800, "HWHEEL");

            return String.Join("+", names);
        }

        public void Stop()
        {
            if (this._thread == null)
            {
                return;
            }

            try
            {
                if (this._window != IntPtr.Zero)
                {
                    // Posted, not sent: the window belongs to the probe thread, and
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
