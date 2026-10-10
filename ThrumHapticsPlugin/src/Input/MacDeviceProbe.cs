namespace Loupedeck.ThrumHapticsPlugin.Input
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;

    /// <summary>
    /// SPIKE - logs which device each macOS mouse event came from, and does
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// Not a feature. It fires no haptics and changes no behaviour; it exists to
    /// answer whether per-device filtering is possible on macOS WITHOUT a new
    /// permission, before anything is built on the assumption that it is.
    ///
    /// The documented route is IOHIDManager, and it is closed: it needs Input
    /// Monitoring, which LogiPluginService does not hold (docs/macos-handoff.md,
    /// fact 8). Asking every user to add the Plugin Service by hand in System
    /// Settings is not something this plugin should require.
    ///
    /// What has NOT been tried is asking the events we already receive. A CGEvent
    /// born from hardware is built from an IOHIDEvent, and that carries the
    /// registry id of the service that sent it. Two ways to read it, neither of
    /// them public API:
    ///
    ///   - CGEventCopyIOHIDEvent, then IOHIDEventGetSenderID.
    ///   - integer field 87 of the CGEvent, which is said to hold the same id and
    ///     would be far cheaper if it does.
    ///
    /// The registry id resolves to a device through IOKit's registry calls, which
    /// ARE public and need no permission - `ioreg` reads the same properties.
    ///
    /// Questions this answers, from the log rather than from expectation:
    ///
    ///   1. Does an event seen by our tap, inside the Plugin Service, carry a
    ///      sender at all - and for which kinds of event?
    ///   2. Does that sender resolve to a vendor id that separates the MX Master
    ///      from a second mouse and from the trackpad?
    ///   3. What does input posted by Options+ look like? On Windows the thumb
    ///      wheel and thumb buttons turned out to be injected; here the posting
    ///      process is readable (field 41), so the injector can be named.
    ///   4. Does field 87 agree with the sender id?
    ///   5. What does the lookup cost? It would run on every mouse event.
    ///
    /// Everything runs on the tap thread - both taps share one run loop - so none
    /// of this needs a lock.
    ///
    /// Delete this file once the question is answered either way.
    /// </remarks>
    internal sealed class MacDeviceProbe
    {
        private const String Tag = "[ThrumHaptics][dev]";

        public const String HidTap = "hid";
        public const String SessionTapAsPrimary = "session(primary)";
        public const String SessionTap = "session";

        private const String CoreGraphics =
            "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        private const String CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        private const String IOKit =
            "/System/Library/Frameworks/IOKit.framework/IOKit";

        private const String SkyLight =
            "/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight";

        private const String LibProc = "/usr/lib/libproc.dylib";

        // --- The two private calls --------------------------------------------
        //
        // Resolved by name at run time rather than through DllImport, so that a
        // macOS release which drops either one is a line in the log instead of an
        // EntryPointNotFoundException on the tap thread.

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CopyHidEvent(IntPtr cgEvent);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate UInt64 GetSenderId(IntPtr hidEvent);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate UInt32 GetHidEventType(IntPtr hidEvent);

        private CopyHidEvent _copyHidEvent;
        private GetSenderId _getSenderId;
        private GetHidEventType _getHidEventType;

        // --- Public API from here on ------------------------------------------

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
        private static extern IntPtr IOServiceMatching([MarshalAs(UnmanagedType.LPUTF8Str)] String name);

        [DllImport(IOKit)]
        private static extern UInt32 IOServiceGetMatchingService(UInt32 mainPort, IntPtr matching);

        [DllImport(IOKit)]
        private static extern Int32 IOServiceGetMatchingServices(
            UInt32 mainPort, IntPtr matching, out UInt32 iterator);

        [DllImport(IOKit)]
        private static extern UInt32 IOIteratorNext(UInt32 iterator);

        [DllImport(IOKit)]
        private static extern IntPtr IORegistryEntrySearchCFProperty(
            UInt32 entry, [MarshalAs(UnmanagedType.LPUTF8Str)] String plane,
            IntPtr key, IntPtr allocator, UInt32 options);

        [DllImport(IOKit)]
        private static extern Int32 IORegistryEntryGetParentEntry(
            UInt32 entry, [MarshalAs(UnmanagedType.LPUTF8Str)] String plane, out UInt32 parent);

        [DllImport(IOKit)]
        private static extern Int32 IORegistryEntryGetRegistryEntryID(UInt32 entry, out UInt64 entryId);

        [DllImport(IOKit)]
        private static extern Int32 IOObjectGetClass(UInt32 entry, [Out] Byte[] className);

        [DllImport(IOKit)]
        private static extern Int32 IOObjectRelease(UInt32 entry);

        /// <summary>Reports a permission WITHOUT prompting for it.</summary>
        /// <remarks>
        /// IOHIDRequestAccess is the call that raises the dialog, and it is
        /// deliberately not here: a spike has no business putting a permission
        /// prompt in front of anyone.
        /// </remarks>
        [DllImport(IOKit)]
        private static extern UInt32 IOHIDCheckAccess(UInt32 requestType);

        [DllImport(LibProc)]
        private static extern Int32 proc_pidpath(Int32 pid, [Out] Byte[] buffer, UInt32 bufferSize);

        private const UInt32 Utf8 = 0x08000100;
        private const Int32 SInt64Type = 4;
        private const String ServicePlane = "IOService";

        // kIORegistryIterateRecursively | kIORegistryIterateParents: look at the
        // entry, then walk UP. The sender is a driver sitting on top of the
        // device, and the vendor id may live on either.
        private const UInt32 ThroughParents = 1 | 2;

        private const UInt32 RequestPostEvent = 0;
        private const UInt32 RequestListenEvent = 1;

        private const UInt32 EventScrollWheel = 22;
        private const UInt32 EventOtherMouseDown = 25;
        private const UInt32 EventOtherMouseUp = 26;
        private const UInt32 EventOtherMouseDragged = 27;

        private const UInt32 FieldMouseEventButtonNumber = 3;
        private const UInt32 FieldScrollLinesAxis1 = 11;
        private const UInt32 FieldScrollLinesAxis2 = 12;
        private const UInt32 FieldSourceUnixProcessId = 41;
        private const UInt32 FieldUndocumented87 = 87;
        private const UInt32 FieldScrollIsContinuous = 88;
        private const UInt32 FieldScrollPointsAxis1 = 96;
        private const UInt32 FieldScrollPointsAxis2 = 97;
        private const UInt32 FieldScrollMomentumPhase = 123;

        // --- Bounds on what gets written --------------------------------------
        //
        // The log has to stay readable, and this runs at the rate the mouse moves.

        private const Int32 MaxKinds = 120;
        private const Int32 MaxFieldDumps = 40;
        private const Int32 MaxDevicesListed = 60;
        private const Int64 SummaryEveryMs = 10_000;
        private const Int32 TotalsEverySummaries = 3;

        /// <summary>One distinguishable kind of event: where, what, and from whom.</summary>
        private readonly record struct Kind(
            String Tap, UInt32 Type, Int32 Detail, Boolean HasHidEvent, UInt64 Sender, Int64 Poster);

        private sealed class Seen
        {
            public String Label;
            public Int64 Total;
            public Int64 SinceSummary;
            public Int64 Field87IsSender;
            public Int64 Field87Zero;
            public Int64 Field87Other;
        }

        private readonly Dictionary<Kind, Seen> _seen = new();
        private readonly Dictionary<UInt64, String> _senderNames = new();
        private readonly Dictionary<Int64, String> _posters = new();

        private Boolean _started;
        private Boolean _broken;
        private Int32 _fieldDumps;
        private Int64 _unlisted;
        private Int64 _lastSummaryMs;
        private Int32 _summaries;
        private Int64 _lookupTicks;
        private Int64 _lookups;

        // --- Not taking the host down twice -----------------------------------
        //
        // Two of the calls here are undocumented, and this runs inside
        // LogiPluginService. A managed exception is caught below; a native fault
        // is not catchable and would take the service with it - after which
        // Options+ restarts it, the plugin loads, the first mouse event arrives,
        // and it happens again.
        //
        // So a marker file exists for as long as the probe is doing something it
        // has not done before, and is removed once that has gone quietly. Finding
        // it at start means the last run ended in the middle of something.
        //
        // Once is forgiven - the service being killed during an install looks the
        // same from here - and the marker says what was underway, so the log does
        // too. Twice running and the probe stays off. The plugin itself is
        // untouched either way.
        private static readonly String MarkerPath =
            Path.Combine(Path.GetTempPath(), "thrumhaptics-device-probe.running");

        private const Int32 QuietEventsToDisarm = 300;
        private const Int32 MaxUncleanRuns = 2;

        private Boolean _armed;
        private Int32 _uncleanRuns;
        private Int32 _eventsSinceNewKind;

        public void Start()
        {
            try
            {
                if (File.Exists(MarkerPath))
                {
                    var left = File.ReadAllText(MarkerPath).Split('|', 2);

                    Int32.TryParse(left[0], out this._uncleanRuns);

                    var doing = left.Length > 1 ? left[1] : "unknown";

                    if (this._uncleanRuns >= MaxUncleanRuns)
                    {
                        PluginLog.Info(
                            $"{Tag} probe OFF: the last {this._uncleanRuns} runs ended during '{doing}'. "
                            + $"Delete {MarkerPath} to try again.");
                        return;
                    }

                    PluginLog.Info($"{Tag} the previous run ended during '{doing}'; trying once more.");
                }

                this.Arm("start");

                this._copyHidEvent = Resolve<CopyHidEvent>(
                    "CGEventCopyIOHIDEvent", out var copyFrom, CoreGraphics, SkyLight);

                this._getSenderId = Resolve<GetSenderId>(
                    "IOHIDEventGetSenderID", out var senderFrom, IOKit);

                this._getHidEventType = Resolve<GetHidEventType>(
                    "IOHIDEventGetType", out _, IOKit);

                PluginLog.Info(
                    $"{Tag} probe on. CGEventCopyIOHIDEvent: {Where(copyFrom)}. "
                    + $"IOHIDEventGetSenderID: {Where(senderFrom)}.");

                PluginLog.Info(
                    $"{Tag} permissions, read without prompting: "
                    + $"listen={AccessName(IOHIDCheckAccess(RequestListenEvent))} "
                    + $"post={AccessName(IOHIDCheckAccess(RequestPostEvent))}");

                LogHidDevices();

                this._lastSummaryMs = Environment.TickCount64;
                this._started = true;
            }
            catch (Exception ex)
            {
                this._broken = true;
                PluginLog.Error($"{Tag} probe could not start: {ex}");
                this.Disarm();
            }
        }

        /// <summary>Records one event. Never alters it, never fires anything.</summary>
        public void Observe(String tap, UInt32 type, IntPtr @event)
        {
            if (!this._started || this._broken)
            {
                return;
            }

            try
            {
                var poster = CGEventGetIntegerValueField(@event, FieldSourceUnixProcessId);
                var field87 = CGEventGetIntegerValueField(@event, FieldUndocumented87);

                var hasHidEvent = false;
                UInt64 sender = 0;
                UInt32 hidEventType = 0;

                if (this._copyHidEvent != null && this._getSenderId != null)
                {
                    var before = Stopwatch.GetTimestamp();
                    var hidEvent = this._copyHidEvent(@event);

                    if (hidEvent != IntPtr.Zero)
                    {
                        hasHidEvent = true;
                        sender = this._getSenderId(hidEvent);
                        hidEventType = this._getHidEventType?.Invoke(hidEvent) ?? 0;

                        // "Copy" in the name: the reference is ours to drop.
                        CFRelease(hidEvent);
                    }

                    this._lookupTicks += Stopwatch.GetTimestamp() - before;
                    this._lookups++;
                }

                var kind = new Kind(tap, type, DetailOf(type, @event), hasHidEvent, sender, poster);

                if (!this._seen.TryGetValue(kind, out var seen))
                {
                    if (this._seen.Count >= MaxKinds)
                    {
                        this._unlisted++;
                        return;
                    }

                    this.Arm($"{tap} {TypeName(type)}");
                    this._eventsSinceNewKind = 0;

                    seen = this.Introduce(kind, hidEventType, field87, @event);
                    this._seen[kind] = seen;
                }
                else if (this._armed && ++this._eventsSinceNewKind >= QuietEventsToDisarm)
                {
                    this.Disarm();
                }

                seen.Total++;
                seen.SinceSummary++;

                if (field87 == 0)
                {
                    seen.Field87Zero++;
                }
                else if ((UInt64)field87 == sender)
                {
                    seen.Field87IsSender++;
                }
                else
                {
                    seen.Field87Other++;
                }

                var now = Environment.TickCount64;

                if ((now - this._lastSummaryMs) >= SummaryEveryMs)
                {
                    this._lastSummaryMs = now;
                    this.LogSummary("10s", sinceSummary: true);

                    // Running totals as well, and often: the log is usually copied
                    // while the service is still up, so Stop() may never be reached.
                    if (++this._summaries % TotalsEverySummaries == 0)
                    {
                        this.LogSummary("TOTAL", sinceSummary: false);
                    }
                }
            }
            catch (Exception ex)
            {
                // One failure ends the probe. It must never become a line per event,
                // and it must never reach CoreGraphics.
                this._broken = true;
                PluginLog.Error($"{Tag} probe stopped after an error: {ex}");
                this.Disarm();
            }
        }

        /// <summary>Totals for the whole session. Called once the taps are down.</summary>
        public void Stop()
        {
            if (!this._started)
            {
                return;
            }

            try
            {
                this.LogSummary("TOTAL", sinceSummary: false);

                if (this._unlisted > 0)
                {
                    PluginLog.Info($"{Tag} {this._unlisted} events fell outside the first {MaxKinds} kinds.");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"{Tag} probe summary failed: {ex.Message}");
            }
            finally
            {
                this.Disarm();
                this._started = false;
            }
        }

        /// <summary>Logs a kind of event the first time it is seen.</summary>
        private Seen Introduce(Kind kind, UInt32 hidEventType, Int64 field87, IntPtr @event)
        {
            var what = $"{kind.Tap} {TypeName(kind.Type)}{DetailText(kind.Type, kind.Detail)}";
            var from = this.SenderName(kind.Sender, out var device);

            var poster = kind.Poster == 0 ? "hardware" : this.PosterName(kind.Poster);

            PluginLog.Info(
                $"{Tag} NEW {what}"
                + $" | hidEvent={(kind.HasHidEvent ? $"yes(type {hidEventType})" : "none")}"
                + $" sender=0x{kind.Sender:X} field87=0x{field87:X}"
                + $" | posted by: {poster}"
                + (device == null ? String.Empty : $" | {device}"));

            // Every integer field the event carries, once per kind. If field 87 is
            // not the sender, the answer may be sitting in a neighbour - and the
            // scroll events may carry an unaccelerated delta worth knowing about.
            if (this._fieldDumps < MaxFieldDumps)
            {
                this._fieldDumps++;
                PluginLog.Info($"{Tag}     fields:{DumpFields(@event)}");
            }

            return new Seen
            {
                Label = $"{what} <- {(kind.Poster == 0 ? from : $"{from} via pid {kind.Poster}")}",
            };
        }

        private void LogSummary(String heading, Boolean sinceSummary)
        {
            var text = new StringBuilder();

            foreach (var seen in this._seen.Values)
            {
                var count = sinceSummary ? seen.SinceSummary : seen.Total;

                if (count == 0)
                {
                    continue;
                }

                text.Append(text.Length == 0 ? String.Empty : " ; ")
                    .Append(seen.Label).Append(" x").Append(count);

                if (!sinceSummary)
                {
                    text.Append(" [f87 =sender ").Append(seen.Field87IsSender)
                        .Append(", zero ").Append(seen.Field87Zero)
                        .Append(", other ").Append(seen.Field87Other).Append(']');
                }

                seen.SinceSummary = 0;
            }

            if (text.Length == 0)
            {
                return;
            }

            var micros = this._lookups == 0
                ? 0
                : this._lookupTicks * 1_000_000 / Stopwatch.Frequency / this._lookups;

            PluginLog.Info($"{Tag} {heading}: {text} ; lookup avg {micros}us over {this._lookups}");
        }

        // --- Who sent it -------------------------------------------------------

        /// <summary>A short name for a sender, and its full description once.</summary>
        private String SenderName(UInt64 sender, out String description)
        {
            description = null;

            if (sender == 0)
            {
                return "no sender";
            }

            if (this._senderNames.TryGetValue(sender, out var known))
            {
                return known;
            }

            var name = $"0x{sender:X}";

            // Consumed by the call below, whether or not it finds anything.
            var matching = IORegistryEntryIDMatching(sender);
            var service = matching == IntPtr.Zero ? 0 : IOServiceGetMatchingService(0, matching);

            if (service == 0)
            {
                description = "sender is not a registry entry";
            }
            else
            {
                try
                {
                    name = ReadText(service, "Product", ThroughParents) ?? name;
                    description = $"{DescribeDevice(service, ThroughParents)} | {ParentChain(service)}";
                }
                finally
                {
                    IOObjectRelease(service);
                }
            }

            this._senderNames[sender] = name;
            return name;
        }

        private String PosterName(Int64 pid)
        {
            if (this._posters.TryGetValue(pid, out var known))
            {
                return known;
            }

            String path;

            try
            {
                var buffer = new Byte[4096];
                var length = proc_pidpath((Int32)pid, buffer, (UInt32)buffer.Length);

                path = length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : "(path unavailable)";
            }
            catch (Exception ex)
            {
                path = $"(lookup failed: {ex.GetType().Name})";
            }

            var text = $"pid {pid} {path}";

            this._posters[pid] = text;
            return text;
        }

        /// <summary>Every HID device in the registry, so sender ids have something to be matched against.</summary>
        private static void LogHidDevices()
        {
            var matching = IOServiceMatching("IOHIDDevice");

            if (matching == IntPtr.Zero
                || IOServiceGetMatchingServices(0, matching, out var iterator) != 0
                || iterator == 0)
            {
                PluginLog.Info($"{Tag} could not list HID devices.");
                return;
            }

            try
            {
                var count = 0;

                for (var entry = IOIteratorNext(iterator); entry != 0; entry = IOIteratorNext(iterator))
                {
                    try
                    {
                        if (count++ < MaxDevicesListed)
                        {
                            PluginLog.Info(
                                $"{Tag} device #{IdOf(entry):X} {ClassOf(entry)} {DescribeDevice(entry, 0)}");
                        }
                    }
                    finally
                    {
                        IOObjectRelease(entry);
                    }
                }

                PluginLog.Info($"{Tag} {count} HID devices in the registry.");
            }
            finally
            {
                IOObjectRelease(iterator);
            }
        }

        private static String DescribeDevice(UInt32 entry, UInt32 options) =>
            $"\"{ReadText(entry, "Product", options) ?? "(unnamed)"}\""
            + $" vendor={Hex(ReadNumber(entry, "VendorID", options))}"
            + $" product={Hex(ReadNumber(entry, "ProductID", options))}"
            + $" transport={ReadText(entry, "Transport", options) ?? "?"}"
            + $" usage={Hex(ReadNumber(entry, "PrimaryUsagePage", options))}"
            + $"/{Hex(ReadNumber(entry, "PrimaryUsage", options))}";

        /// <summary>The entry and its ancestors, nearest first, as class#id.</summary>
        private static String ParentChain(UInt32 entry)
        {
            var text = new StringBuilder();
            var current = entry;
            var owned = false; // The caller owns `entry`; we own each parent we fetch.

            for (var depth = 0; depth < 6; depth++)
            {
                text.Append(depth == 0 ? String.Empty : " < ")
                    .Append(ClassOf(current)).Append('#').Append(IdOf(current).ToString("X"));

                if (IORegistryEntryGetParentEntry(current, ServicePlane, out var parent) != 0 || parent == 0)
                {
                    break;
                }

                if (owned)
                {
                    IOObjectRelease(current);
                }

                current = parent;
                owned = true;
            }

            if (owned)
            {
                IOObjectRelease(current);
            }

            return text.ToString();
        }

        private static UInt64 IdOf(UInt32 entry) =>
            IORegistryEntryGetRegistryEntryID(entry, out var id) == 0 ? id : 0;

        private static String ClassOf(UInt32 entry)
        {
            var buffer = new Byte[128]; // io_name_t

            return IOObjectGetClass(entry, buffer) == 0 ? FromCString(buffer) : "?";
        }

        private static IntPtr CopyProperty(UInt32 entry, String key, UInt32 options)
        {
            var name = CFStringCreateWithCString(IntPtr.Zero, key, Utf8);

            if (name == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                return IORegistryEntrySearchCFProperty(entry, ServicePlane, name, IntPtr.Zero, options);
            }
            finally
            {
                CFRelease(name);
            }
        }

        private static String ReadText(UInt32 entry, String key, UInt32 options)
        {
            var value = CopyProperty(entry, key, options);

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

                return CFStringGetCString(value, buffer, (IntPtr)buffer.Length, Utf8) != 0
                    ? FromCString(buffer)
                    : null;
            }
            finally
            {
                CFRelease(value);
            }
        }

        private static Int64? ReadNumber(UInt32 entry, String key, UInt32 options)
        {
            var value = CopyProperty(entry, key, options);

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

        // --- Formatting --------------------------------------------------------

        /// <summary>What kind of scroll, or which button - folded into one number so it costs nothing per event.</summary>
        private static Int32 DetailOf(UInt32 type, IntPtr @event)
        {
            if (type == EventScrollWheel)
            {
                var detail = 0;

                if (CGEventGetIntegerValueField(@event, FieldScrollLinesAxis1) != 0
                    || CGEventGetIntegerValueField(@event, FieldScrollPointsAxis1) != 0)
                {
                    detail |= 1;
                }

                if (CGEventGetIntegerValueField(@event, FieldScrollLinesAxis2) != 0
                    || CGEventGetIntegerValueField(@event, FieldScrollPointsAxis2) != 0)
                {
                    detail |= 2;
                }

                if (CGEventGetIntegerValueField(@event, FieldScrollIsContinuous) != 0)
                {
                    detail |= 4;
                }

                if (CGEventGetIntegerValueField(@event, FieldScrollMomentumPhase) != 0)
                {
                    detail |= 8;
                }

                return detail;
            }

            if (type is EventOtherMouseDown or EventOtherMouseUp or EventOtherMouseDragged)
            {
                return (Int32)CGEventGetIntegerValueField(@event, FieldMouseEventButtonNumber);
            }

            return 0;
        }

        private static String DetailText(UInt32 type, Int32 detail)
        {
            if (type == EventScrollWheel)
            {
                var axis = (detail & 3) switch
                {
                    1 => "vertical",
                    2 => "horizontal",
                    3 => "both axes",
                    _ => "no delta",
                };

                return $"({axis}{((detail & 4) != 0 ? ", continuous" : String.Empty)}"
                    + $"{((detail & 8) != 0 ? ", momentum" : String.Empty)})";
            }

            return type is EventOtherMouseDown or EventOtherMouseUp or EventOtherMouseDragged
                ? $"(button {detail})"
                : String.Empty;
        }

        private static String TypeName(UInt32 type) => type switch
        {
            1 => "leftDown",
            2 => "leftUp",
            3 => "rightDown",
            4 => "rightUp",
            5 => "moved",
            6 => "leftDragged",
            7 => "rightDragged",
            18 => "rotate",
            19 => "beginGesture",
            20 => "endGesture",
            22 => "scroll",
            25 => "otherDown",
            26 => "otherUp",
            27 => "otherDragged",
            29 => "gesture",
            30 => "magnify",
            31 => "swipe",
            32 => "smartMagnify",
            _ => $"type{type}",
        };

        private static String DumpFields(IntPtr @event)
        {
            var text = new StringBuilder();

            for (UInt32 field = 0; field < 256; field++)
            {
                var value = CGEventGetIntegerValueField(@event, field);

                if (value != 0)
                {
                    text.Append(' ').Append(field).Append('=').Append(value);
                }
            }

            return text.ToString();
        }

        private static String AccessName(UInt32 access) => access switch
        {
            0 => "granted",
            1 => "denied",
            2 => "never asked",
            _ => $"unknown({access})",
        };

        private static String Hex(Int64? value) => value == null ? "?" : $"0x{value.Value:X4}";

        private static String Where(String library) =>
            library == null ? "NOT FOUND" : $"found in {Path.GetFileName(library)}";

        private static String FromCString(Byte[] buffer)
        {
            var length = Array.IndexOf(buffer, (Byte)0);

            return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
        }

        private static T Resolve<T>(String symbol, out String foundIn, params String[] libraries)
            where T : Delegate
        {
            foreach (var library in libraries)
            {
                if (NativeLibrary.TryLoad(library, out var handle)
                    && NativeLibrary.TryGetExport(handle, symbol, out var address))
                {
                    foundIn = library;
                    return Marshal.GetDelegateForFunctionPointer<T>(address);
                }
            }

            foundIn = null;
            return null;
        }

        private void Arm(String doing)
        {
            if (this._armed)
            {
                return;
            }

            this._armed = true;

            try
            {
                File.WriteAllText(MarkerPath, $"{this._uncleanRuns + 1}|{doing}");
            }
            catch (Exception)
            {
                // No marker means no protection against a repeat, not no probe.
            }
        }

        private void Disarm()
        {
            if (!this._armed)
            {
                return;
            }

            this._armed = false;
            this._uncleanRuns = 0; // It went quietly; the next surprise starts from one.

            try
            {
                File.Delete(MarkerPath);
            }
            catch (Exception)
            {
                // Left behind, it only keeps the probe off next time.
            }
        }
    }
}
