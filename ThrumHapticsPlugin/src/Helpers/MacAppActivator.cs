namespace Loupedeck.ThrumHapticsPlugin
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Brings another process's windows to the front, on macOS.
    /// </summary>
    /// <remarks>
    /// Exists for one job: a second press of Haptic Settings while the settings
    /// window is already open. The settings application is single-instance, and
    /// macOS offers nothing like FindWindow for a second copy to raise the first
    /// with - so that second copy used to exit quietly, and a window lost behind
    /// others stayed lost while the press appeared to do nothing.
    ///
    /// The plugin knows the process it launched, so it asks AppKit to activate it
    /// directly. Only Apple's own libraries are called, which matters here more
    /// than anywhere: this runs INSIDE LogiPluginService, whose hardened runtime
    /// refuses any library not signed by Apple or Logitech.
    /// </remarks>
    internal static class MacAppActivator
    {
        private const String ObjC = "/usr/lib/libobjc.dylib";

        // NSApplicationActivateAllWindows | NSApplicationActivateIgnoringOtherApps.
        // The second is advisory on recent macOS, which decides for itself whether
        // a background process may take focus; it is passed because it is honoured
        // on the versions that still honour it and ignored harmlessly on the rest.
        private const UInt64 ActivateAllWindowsIgnoringOtherApps = (1UL << 0) | (1UL << 1);

        [DllImport(ObjC, EntryPoint = "objc_getClass")]
        private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] String name);

        [DllImport(ObjC, EntryPoint = "sel_registerName")]
        private static extern IntPtr GetSelector([MarshalAs(UnmanagedType.LPUTF8Str)] String name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendProcessId(IntPtr receiver, IntPtr selector, Int32 processId);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern Byte SendOptions(IntPtr receiver, IntPtr selector, UIntPtr options);

        /// <summary>
        /// Asks macOS to bring the given process forward. False if it could not.
        /// </summary>
        public static Boolean TryActivate(Int32 processId)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return false;
            }

            var pool = IntPtr.Zero;

            try
            {
                // This can be called from any thread the Plugin Service chooses, and
                // runningApplicationWithProcessIdentifier: returns an autoreleased
                // object. Without a pool of our own it would leak on every press.
                pool = Send(Send(GetClass("NSAutoreleasePool"), GetSelector("alloc")), GetSelector("init"));

                var runningApplication = GetClass("NSRunningApplication");

                if (runningApplication == IntPtr.Zero)
                {
                    return false; // AppKit is not loaded in this process.
                }

                var application = SendProcessId(
                    runningApplication,
                    GetSelector("runningApplicationWithProcessIdentifier:"),
                    processId);

                if (application == IntPtr.Zero)
                {
                    return false;
                }

                return SendOptions(
                    application,
                    GetSelector("activateWithOptions:"),
                    (UIntPtr)ActivateAllWindowsIgnoringOtherApps) != 0;
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[ThrumHaptics] Could not activate process {processId}: {ex.Message}");
                return false;
            }
            finally
            {
                if (pool != IntPtr.Zero)
                {
                    Send(pool, GetSelector("drain"));
                }
            }
        }
    }
}
