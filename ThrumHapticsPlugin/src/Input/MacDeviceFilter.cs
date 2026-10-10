namespace Loupedeck.ThrumHapticsPlugin.Input
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading.Tasks;

    using Loupedeck.ThrumHapticsPlugin.Config;

    /// <summary>
    /// Tells Logitech pointer input apart from every other device's, on macOS.
    /// </summary>
    /// <remarks>
    /// The macOS counterpart to RawInputDeviceTracker, and simpler than it for a
    /// reason worth stating. Windows reports the device on a second channel that
    /// has to be matched to the hook by timing; here every event says where it
    /// came from. So there is no second thread and no second look - each event
    /// is judged on what it carries, in the callback that receives it.
    ///
    /// MEASURED ON DEVICE, inside the Plugin Service, with an MX Master 4 on
    /// Bluetooth, a Kensington SlimBlade Pro beside it, and Options+ running:
    ///
    ///   - A hardware event carries the registry id of the service that sent it
    ///     in integer field 87. It resolved to "MX Master 4", vendor 0x046D, for
    ///     movement, clicks and drags, and to vendor 0x047D for the trackball.
    ///   - The trackball's clicks and wheel come from a DIFFERENT sender than its
    ///     movement: a virtual pointer its driver re-posts them through, just as
    ///     the Elecom driver does on Windows. Same vendor, so matching the vendor
    ///     covers both.
    ///   - The MX Master's two wheels carry NO sender. Options+ posts them, so
    ///     they arrive with its process id in field 41 instead. Windows showed
    ///     the same thing, except that here the injector can be named.
    ///   - The read costs nothing measurable, and a sender is resolved once.
    ///
    /// The vendor is matched rather than the product so that this keeps working
    /// through a Bolt receiver, which reports its own product id, not the
    /// mouse's. Nothing about one particular mouse is stored: the sender id is a
    /// number macOS hands out at connection and changes on the next one.
    ///
    /// FIELD 87 IS NOT DOCUMENTED. It is read through the public
    /// CGEventGetIntegerValueField, which answers zero for a field it does not
    /// know, so nothing here can fault - but Apple is free to change what it
    /// holds. Hence the rule that governs everything below.
    ///
    /// IT ENGAGES ONLY ON PROOF. Nothing is held back until a Logitech pointer
    /// has actually been identified as a sender on this machine. If the field
    /// stops meaning what it means today, or something sits between the mouse
    /// and the system and re-issues its input under another name, that proof
    /// never arrives and the plugin behaves exactly as 1.2.0 did. A filter that
    /// falls silent when unsure would turn a diagnostics problem into a plugin
    /// that looks dead.
    ///
    /// Everything but Start runs on the tap thread - both taps share one run
    /// loop - so none of this needs a lock.
    /// </remarks>
    internal sealed class MacDeviceFilter
    {
        private const String Tag = "[ThrumHaptics][device]";

        private const String CoreGraphics =
            "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        private const String CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        private const String IOKit =
            "/System/Library/Frameworks/IOKit.framework/IOKit";

        private const String LibProc = "/usr/lib/libproc.dylib";

        [DllImport(CoreGraphics)]
        private static extern Int64 CGEventGetIntegerValueField(IntPtr @event, UInt32 field);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFStringCreateWithCString(
            IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] String text, UInt32 encoding);

        // CoreFoundation's Boolean is one byte. Declared as such rather than as a
        // managed Boolean, which would read four.
        [DllImport(CoreFoundation)]
        private static extern Byte CFStringGetCString(
            IntPtr text, [Out] Byte[] buffer, IntPtr bufferSize, UInt32 encoding);

        [DllImport(CoreFoundation)]
        private static extern UIntPtr CFGetTypeID(IntPtr cf);

        [DllImport(CoreFoundation)]
        private static extern UIntPtr CFStringGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern UIntPtr CFNumberGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern Byte CFNumberGetValue(IntPtr number, IntPtr type, out Int64 value);

        // Returns a dictionary that IOServiceGetMatchingService CONSUMES, so it is
        // never released here.
        [DllImport(IOKit)]
        private static extern IntPtr IORegistryEntryIDMatching(UInt64 entryId);

        [DllImport(IOKit)]
        private static extern UInt32 IOServiceGetMatchingService(UInt32 mainPort, IntPtr matching);

        [DllImport(IOKit)]
        private static extern IntPtr IORegistryEntrySearchCFProperty(
            UInt32 entry, [MarshalAs(UnmanagedType.LPUTF8Str)] String plane,
            IntPtr key, IntPtr allocator, UInt32 options);

        [DllImport(IOKit)]
        private static extern Int32 IOObjectRelease(UInt32 entry);

        [DllImport(LibProc)]
        private static extern Int32 proc_pidpath(Int32 pid, [Out] Byte[] buffer, UInt32 bufferSize);

        private const UInt32 Utf8 = 0x08000100;
        private const Int32 SInt64Type = 4;
        private const String ServicePlane = "IOService";

        // kIORegistryIterateRecursively | kIORegistryIterateParents: look at the
        // entry, then walk UP. The sender is a driver sitting on top of the
        // device, and the vendor id may live on either.
        private const UInt32 ThroughParents = 1 | 2;

        /// <summary>PID of the process that posted the event, or 0 for real hardware.</summary>
        private const UInt32 FieldPoster = 41;

        /// <summary>Registry id of the service that sent the event, or 0 if it was posted.</summary>
        private const UInt32 FieldSender = 87;

        private const Int64 LogitechVendorId = 0x046D;

        private const UInt32 EventMouseMoved = 5;
        private const UInt32 EventLeftMouseDragged = 6;
        private const UInt32 EventRightMouseDragged = 7;
        private const UInt32 EventOtherMouseDragged = 27;

        private enum Verdict
        {
            Unknown,
            Logitech,
            Other,
        }

        private readonly record struct Known(Verdict Verdict, Int64 AtMs);

        private readonly Dictionary<UInt64, Known> _senders = new();
        private readonly Dictionary<Int64, Known> _posters = new();

        // A handful of devices and one or two posting processes is the whole
        // population on a real machine. The bound is there for the machine that
        // is not behaving like one.
        private const Int32 MaxRemembered = 64;

        /// <summary>How long a sender that could not be looked up is left alone.</summary>
        private const Int64 RetryUnresolvedMs = 5000;

        /// <summary>How long a process id is trusted to still be the same program.</summary>
        /// <remarks>
        /// Process ids are reused. Options+ restarting and something else taking
        /// its old number is unlikely, and re-reading the path is one system call.
        /// </remarks>
        private const Int64 PosterTtlMs = 30_000;

        /// <summary>How many senders may fail to resolve before the field is no longer believed.</summary>
        /// <remarks>
        /// If field 87 ever stops holding a registry id, every event would bring a
        /// new number and a registry search that finds nothing - at the rate the
        /// mouse moves. Fifty different unresolvable senders is not a machine with
        /// odd hardware; it is a field that means something else now.
        /// </remarks>
        private const Int32 MaxUnresolvedSenders = 50;

        // --- When Options+ may speak for the mouse ------------------------------
        //
        // The same three numbers as RawInputDeviceTracker, and deliberately: this
        // is the same rule, arrived at the same way, and someone moving between
        // machines should find it behaving the same.

        /// <summary>How long another device must be quiet before posted wheel input counts.</summary>
        /// <remarks>
        /// The wheels have no sender of their own - Options+ posts them - so left
        /// alone they buzz whatever else is going on. MEASURED: with the trackball
        /// in use, the thumb wheel was still reporting a point or two of travel
        /// every second or so - a brush, not a roll - and each one became a tick
        /// or a thumb-button haptic, felt as the trackball buzzing the mouse.
        ///
        /// What separates a roll from that is whether the OTHER device is in use.
        /// Someone on the trackball is touching it now; someone rolling the
        /// MX Master's wheel has let go of it, and a hand takes longer than this
        /// to change devices.
        /// </remarks>
        private const Int64 OtherDeviceQuietMs = 250;

        /// <summary>Movement reports from another device before it counts as in use.</summary>
        /// <remarks>
        /// A lone report is not somebody using it. An idle optical mouse was
        /// measured sending one to four strays a second on Windows, and with the
        /// quiet period above each would cost the wheels a quarter second. Clicks
        /// and wheel turns are deliberate and count at once.
        /// </remarks>
        private const Int32 OtherMovesToCount = 3;

        private const Int64 OtherMoveWindowMs = 250;

        private Int64 _lastOtherUseMs = Int64.MinValue / 2;
        private Int64 _otherMoveLastMs = Int64.MinValue / 2;
        private Int32 _otherMoveStreak;

        // --- Proof, and remembering it ------------------------------------------

        /// <summary>Whether a Logitech pointer was identified the last time this ran.</summary>
        /// <remarks>
        /// Without this the proof has to be earned again after every restart, and
        /// until the MX Master is next touched everything else drives it. MEASURED:
        /// fifteen haptics in the first five seconds, because the trackball
        /// happened to be used first. With a laptop that is the common case - the
        /// trackpad, with the mouse buzzing on the desk beside it.
        ///
        /// So last run's proof carries over. It is cleared at start and set again
        /// only when a Logitech pointer is actually seen, which keeps it honest:
        /// a machine where the mouse has stopped identifying itself is wrong for
        /// one run, then goes back to waiting.
        /// </remarks>
        private const String SeenLastRunKey = "device.logitechPointerSeen";

        private readonly HapticSettings _settings;

        private Boolean _off;
        private Boolean _engaged;
        private Boolean _seenThisRun;
        private Boolean _lastHardwareIsLogitech = true;
        private Int32 _unresolvedSenders;

        public MacDeviceFilter(HapticSettings settings) =>
            this._settings = settings ?? throw new ArgumentNullException(nameof(settings));

        /// <summary>Picks up last run's proof. Called before any event is delivered.</summary>
        public void Start()
        {
            try
            {
                this._engaged = this._settings.GetFlag(SeenLastRunKey);

                if (this._engaged)
                {
                    // Written here, synchronously, and not from a task: the write
                    // that sets it again can follow within milliseconds, and the
                    // two must not be able to land in the wrong order.
                    this._settings.SetFlag(SeenLastRunKey, false);
                }

                PluginLog.Info(
                    this._engaged
                        ? $"{Tag} Per-device filtering is ON from the start: a Logitech pointer was identified last time."
                        : $"{Tag} Per-device filtering starts once a Logitech pointer has been identified.");
            }
            catch (Exception ex)
            {
                this._engaged = false;
                PluginLog.Error($"{Tag} could not read last run's state, so starting unproven: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether this event may produce a haptic, or touch any state that leads
        /// to one.
        /// </summary>
        /// <remarks>
        /// Asked before ANYTHING is counted, not just before playing. The Windows
        /// side learned that the expensive way: another mouse's scrolling was
        /// banked in a shared accumulator and refused only at the last step, so
        /// the MX Master's next events tipped a counter somebody else had filled.
        /// </remarks>
        public Boolean Allows(UInt32 type, IntPtr @event)
        {
            if (this._off)
            {
                return true;
            }

            try
            {
                var now = Environment.TickCount64;
                var allowed = this.Judge(type, @event, now);

                this.Tally(allowed, now);

                return allowed;
            }
            catch (Exception ex)
            {
                // Fails OPEN, and for good. Every pointer drives haptics again,
                // which is what 1.2.0 did, and the tap thread is never asked to
                // survive the same failure a thousand times a second.
                this._off = true;

                PluginLog.Error(
                    $"{Tag} failed, so every pointer drives haptics as before: {ex.Message}");

                return true;
            }
        }

        private Boolean Judge(UInt32 type, IntPtr @event, Int64 now)
        {
            var sender = (UInt64)CGEventGetIntegerValueField(@event, FieldSender);

            if (sender != 0)
            {
                switch (this.SenderVerdict(sender, now))
                {
                    case Verdict.Logitech:
                        this._fromLogitech++;
                        this.LogitechPointerUsed();
                        return true;

                    case Verdict.Other:
                        this._fromOther++;
                        this.OtherDeviceUsed(type, now);
                        return !this._engaged;

                    default:
                        // A device that could not be looked up. Not evidence of
                        // anything, so it neither plays the part of the Logitech
                        // pointer nor takes the answer away from it.
                        this._unresolved++;
                        return true;
                }
            }

            var poster = CGEventGetIntegerValueField(@event, FieldPoster);

            if (poster != 0 && this.PosterVerdict(poster, now) != Verdict.Other)
            {
                // Options+, acting for the very mouse we want - or a process whose
                // path could not be read, which gets the benefit of the doubt
                // rather than costing the MX Master its wheels.
                this._postedByLogitech++;

                // Everything Options+ has been seen to post is a wheel or the
                // gesture it sends for a thumb button, and the thumb WHEEL sends
                // that same gesture, so none of it can be told from a wheel. All
                // of it waits for the other device to go quiet.
                if (this._engaged && (now - this._lastOtherUseMs) <= OtherDeviceQuietMs)
                {
                    this._postedHeld++;
                    return false;
                }

                return true;
            }

            if (poster != 0)
            {
                this._postedByOther++;
            }
            else
            {
                this.NoteNoSource(type);
            }

            // Posted by some other program, or carrying no source at all. It has
            // nothing of its own to be judged by, so it belongs to whichever real
            // device was touched last - a scrolling utility smoothing the
            // MX Master's wheel plays, one doing the same for a trackball does not.
            return !this._engaged || this._lastHardwareIsLogitech;
        }

        private void LogitechPointerUsed()
        {
            if (!this._seenThisRun)
            {
                this._seenThisRun = true;

                if (!this._engaged)
                {
                    this._engaged = true;

                    PluginLog.Info($"{Tag} Per-device filtering is ON: only Logitech pointers drive haptics.");
                }

                // Off the tap thread: the settings store writes to disk, and this
                // is the path every mouse event takes.
                var settings = this._settings;

                Task.Run(() =>
                {
                    try
                    {
                        settings.SetFlag(SeenLastRunKey, true);
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error($"{Tag} could not remember that a Logitech pointer was seen: {ex.Message}");
                    }
                });
            }

            this.HardwareIs(logitech: true);
        }

        private void OtherDeviceUsed(UInt32 type, Int64 now)
        {
            if (type is EventMouseMoved or EventLeftMouseDragged
                or EventRightMouseDragged or EventOtherMouseDragged)
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

            this._lastOtherUseMs = now;
            this.HardwareIs(logitech: false);
        }

        private void HardwareIs(Boolean logitech)
        {
            if (this._lastHardwareIsLogitech == logitech)
            {
                return;
            }

            this._lastHardwareIsLogitech = logitech;

            // Only once engaged. Before that nothing is being paused, and saying
            // so would send someone reading the log after the wrong problem.
            if (this._engaged)
            {
                PluginLog.Info(
                    $"{Tag} input now from "
                    + (logitech ? "the Logitech pointer; haptics on." : "another device; haptics paused."));
            }
        }

        private Verdict SenderVerdict(UInt64 sender, Int64 now)
        {
            var seenBefore = this._senders.TryGetValue(sender, out var known);

            if (seenBefore
                && (known.Verdict != Verdict.Unknown || (now - known.AtMs) < RetryUnresolvedMs))
            {
                return known.Verdict;
            }

            var verdict = Verdict.Unknown;

            // Consumed by the call below, whether or not it finds anything.
            var matching = IORegistryEntryIDMatching(sender);
            var service = matching == IntPtr.Zero ? 0 : IOServiceGetMatchingService(0, matching);

            if (service != 0)
            {
                try
                {
                    var vendor = ReadNumber(service, "VendorID");

                    verdict = vendor == LogitechVendorId ? Verdict.Logitech : Verdict.Other;

                    PluginLog.Info(
                        $"{Tag} 0x{sender:X} {(verdict == Verdict.Logitech ? "IS" : "is not")} a Logitech pointer: "
                        + $"\"{ReadText(service, "Product") ?? "(unnamed)"}\""
                        + $" vendor {(vendor == null ? "unknown" : $"0x{vendor.Value:X4}")}");
                }
                finally
                {
                    IOObjectRelease(service);
                }
            }
            else if (!seenBefore && ++this._unresolvedSenders >= MaxUnresolvedSenders)
            {
                throw new InvalidOperationException(
                    "events no longer carry a device id that can be looked up");
            }

            if (this._senders.Count >= MaxRemembered)
            {
                this._senders.Clear();
            }

            this._senders[sender] = new Known(verdict, now);
            return verdict;
        }

        private Verdict PosterVerdict(Int64 pid, Int64 now)
        {
            var seenBefore = this._posters.TryGetValue(pid, out var known);

            if (seenBefore && (now - known.AtMs) < PosterTtlMs)
            {
                return known.Verdict;
            }

            var path = PathOf(pid);

            var verdict =
                path == null ? Verdict.Unknown
                : IsLogitechProgram(path) ? Verdict.Logitech
                : Verdict.Other;

            // Said once per program, not once per re-check.
            if (!seenBefore || known.Verdict != verdict)
            {
                PluginLog.Info(
                    $"{Tag} input posted by pid {pid} ({path ?? "path unavailable"}) "
                    + (verdict == Verdict.Other
                        ? "follows whichever device was used last."
                        : "counts as the Logitech pointer."));
            }

            if (this._posters.Count >= MaxRemembered)
            {
                this._posters.Clear();
            }

            this._posters[pid] = new Known(verdict, now);
            return verdict;
        }

        /// <summary>Whether a posting process is Logitech's own software.</summary>
        /// <remarks>
        /// Measured as .../Logitech.localized/LogiOptionsPlus/
        /// logioptionsplus_agent.app/Contents/MacOS/logioptionsplus_agent. Matched
        /// on the names rather than the whole path, which is Logitech's to move.
        /// </remarks>
        private static Boolean IsLogitechProgram(String path) =>
            path.Contains("logioptionsplus", StringComparison.OrdinalIgnoreCase)
            || path.Contains("logitech", StringComparison.OrdinalIgnoreCase);

        private static String PathOf(Int64 pid)
        {
            try
            {
                var buffer = new Byte[4096]; // PROC_PIDPATHINFO_MAXSIZE
                var length = proc_pidpath((Int32)pid, buffer, (UInt32)buffer.Length);

                return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
            }
            catch (Exception)
            {
                // Unreadable is not the same as unwelcome: the caller allows it.
                return null;
            }
        }

        private static IntPtr CopyProperty(UInt32 entry, String key)
        {
            var name = CFStringCreateWithCString(IntPtr.Zero, key, Utf8);

            if (name == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                return IORegistryEntrySearchCFProperty(entry, ServicePlane, name, IntPtr.Zero, ThroughParents);
            }
            finally
            {
                CFRelease(name);
            }
        }

        private static String ReadText(UInt32 entry, String key)
        {
            var value = CopyProperty(entry, key);

            if (value == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (CFGetTypeID(value) != CFStringGetTypeID())
                {
                    return null;
                }

                var buffer = new Byte[256];

                if (CFStringGetCString(value, buffer, (IntPtr)buffer.Length, Utf8) == 0)
                {
                    return null;
                }

                var length = Array.IndexOf(buffer, (Byte)0);

                return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
            }
            finally
            {
                CFRelease(value);
            }
        }

        private static Int64? ReadNumber(UInt32 entry, String key)
        {
            var value = CopyProperty(entry, key);

            if (value == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (CFGetTypeID(value) != CFNumberGetTypeID())
                {
                    return null;
                }

                return CFNumberGetValue(value, (IntPtr)SInt64Type, out var number) != 0 ? number : null;
            }
            finally
            {
                CFRelease(value);
            }
        }

        // --- TEMPORARY: counts for the first runs on device --------------------
        //
        // The runs so far saw a mouse and a trackball but no trackpad. These lines
        // say how events are being sorted - and, above all, whether anything
        // arrives with no source at all, which the rules above can only guess
        // about. They come out once a trackpad has been seen on device.

        private const Int64 TallyEveryMs = 10_000;

        private readonly HashSet<UInt32> _noSourceTypes = new();

        private Int64 _lastTallyMs = Environment.TickCount64;
        private Int64 _fromLogitech;
        private Int64 _fromOther;
        private Int64 _unresolved;
        private Int64 _postedByLogitech;
        private Int64 _postedHeld;
        private Int64 _postedByOther;
        private Int64 _noSource;
        private Int64 _heldBack;

        private void NoteNoSource(UInt32 type)
        {
            this._noSource++;

            if (this._noSourceTypes.Add(type))
            {
                PluginLog.Info($"{Tag} event type {type} arrived with no sender and no posting process.");
            }
        }

        private void Tally(Boolean allowed, Int64 now)
        {
            if (!allowed)
            {
                this._heldBack++;
            }

            if ((now - this._lastTallyMs) < TallyEveryMs)
            {
                return;
            }

            this._lastTallyMs = now;

            PluginLog.Info(
                $"{Tag} 10s: logitech {this._fromLogitech}, other device {this._fromOther}, "
                + $"unresolved {this._unresolved}, posted by logitech {this._postedByLogitech} "
                + $"(of which held {this._postedHeld}), posted by other {this._postedByOther}, "
                + $"no source {this._noSource} -> held back {this._heldBack}");

            this._fromLogitech = 0;
            this._fromOther = 0;
            this._unresolved = 0;
            this._postedByLogitech = 0;
            this._postedHeld = 0;
            this._postedByOther = 0;
            this._noSource = 0;
            this._heldBack = 0;
        }
    }
}
