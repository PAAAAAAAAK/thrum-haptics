namespace Loupedeck.ThrumHapticsPlugin.Config
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Loupedeck.ThrumHapticsPlugin.Haptics;

    /// <summary>
    /// One configurable thing that can trigger a haptic.
    /// </summary>
    /// <remarks>
    /// Config is keyed on EVENTS, not on mouse buttons. That distinction is what
    /// keeps the settings UI from growing a new control every time we add a
    /// feature: scroll detents, hovering a link, and a window snapping are all
    /// just more entries in <see cref="HapticEvents.All"/>. The editor renders a
    /// list of whatever is in that array, so later stages add rows rather than
    /// new actions, new bindings, or new UI.
    /// </remarks>
    internal sealed class HapticEventDef
    {
        /// <summary>Stable id, used as the settings key. Never change these - a
        /// rename silently orphans a user's saved preference.</summary>
        public String Id { get; }

        /// <summary>Group heading shown in the editor, e.g. "Clicks".</summary>
        public String Category { get; }

        public String DisplayName { get; }

        public String DefaultWaveform { get; }

        public Boolean DefaultEnabled { get; }

        /// <summary>
        /// Events that form one gesture, e.g. a drag's start and end.
        /// </summary>
        /// <remarks>
        /// Purely presentational. Grouped events stay INDEPENDENTLY toggleable and
        /// separately configurable on purpose - the same button does different jobs
        /// in different applications, and wanting the "grab" without the "let go"
        /// is a reasonable preference. The group only tells the settings window to
        /// show them as belonging together, so the relationship is visible without
        /// taking the choice away.
        /// </remarks>
        public String GroupKey { get; }

        /// <summary>
        /// Events the operating system cannot deliver on macOS.
        /// </summary>
        /// <remarks>
        /// Not a feature gate - these are physically undeliverable there, so a
        /// settings row for one would be a switch wired to nothing. See
        /// MacMouseInputSource for the evidence.
        /// </remarks>
        public Boolean WindowsOnly { get; }

        /// <summary>Events that exist only on macOS.</summary>
        /// <remarks>
        /// The mirror of <see cref="WindowsOnly"/>, and it exists for one event:
        /// macOS cannot tell back from forward, so it offers a single combined
        /// thumb-button row where Windows offers two separate ones.
        /// </remarks>
        public Boolean MacOnly { get; }

        /// <summary>Events whose spacing the user can tune.</summary>
        /// <remarks>
        /// Only the two wheels. A click fires once per click and there is nothing
        /// to space out; a wheel fires continuously while it turns, and how far it
        /// should travel between ticks is a matter of taste that no default gets
        /// right for everyone. It was tuned four times by feel across two platforms
        /// before it became obvious that the tuning belonged to the user.
        /// </remarks>
        public Boolean HasDensity { get; }

        public HapticEventDef(
            String id, String category, String displayName,
            String defaultWaveform, Boolean defaultEnabled = true, String groupKey = null,
            Boolean windowsOnly = false, Boolean macOnly = false, Boolean hasDensity = false)
        {
            this.Id = id;
            this.Category = category;
            this.DisplayName = displayName;
            this.DefaultWaveform = defaultWaveform;
            this.DefaultEnabled = defaultEnabled;
            this.GroupKey = groupKey;
            this.WindowsOnly = windowsOnly;
            this.MacOnly = macOnly;
            this.HasDensity = hasDensity;
        }

        /// <summary>Label shown in the editor's event dropdown.</summary>
        public String EditorLabel => $"{this.Category}: {this.DisplayName}";
    }

    /// <summary>
    /// The catalogue of everything that can fire a haptic.
    /// </summary>
    /// <remarks>
    /// GROWTH POINT: Stage 2 (scroll), Stage 3 (system events) and Stage 4 (hover)
    /// each append entries here. Nothing in the settings UI needs to change.
    /// </remarks>
    internal static class HapticEvents
    {
        // --- Stage 1: clicks -----------------------------------------------------
        public const String MouseLeft = "mouse.left";
        public const String MouseRight = "mouse.right";
        public const String MouseMiddle = "mouse.middle";
        public const String MouseBack = "mouse.back";
        public const String MouseForward = "mouse.forward";

        /// <summary>Back and forward as ONE event. macOS only.</summary>
        /// <remarks>
        /// Not a simplification for its own sake - macOS genuinely cannot tell the
        /// two apart. Options+ converts both presses into an NSEvent gesture, and
        /// every field on that event is identical between them. One row is the
        /// honest shape of what we can detect.
        /// </remarks>
        public const String MouseThumb = "mouse.thumb";

        // --- Stage 2: scroll -----------------------------------------------------
        public const String ScrollVertical = "scroll.vertical";
        public const String ScrollHorizontal = "scroll.horizontal";

        // --- Stage 3: gestures and system events ---------------------------------
        // Drag is per-button: left is the everyday case, while middle and right
        // drag are orbit/pan gestures in CAD and 3D tools where constant haptics
        // would be unwelcome. Ids are stable - see HapticEventDef.Id.
        public const String DragLeftStart = "drag.left.start";
        public const String DragLeftEnd = "drag.left.end";
        public const String DragMiddleStart = "drag.middle.start";
        public const String DragMiddleEnd = "drag.middle.end";
        public const String DragRightStart = "drag.right.start";
        public const String DragRightEnd = "drag.right.end";
        public const String ScreenEdge = "cursor.screenEdge";

        // NOTE: hover-over-element feedback is deliberately absent, and it is NOT
        // a technical limitation - UI Automation can identify clickable elements
        // universally. It is left out because it fires on every element the
        // cursor lands on, including elements that arrive under a STATIONARY
        // cursor while scrolling a list, which no dwell delay can filter out.
        // Before attempting it, read this note's own commit history: it records
        // the two traps that made an earlier attempt look impossible.

        public static readonly HapticEventDef[] All =
        {
            new(MouseLeft, "Clicks", "Left click", Waveforms.SubtleCollision),
            new(MouseRight, "Clicks", "Right click", Waveforms.DampCollision),
            new(MouseMiddle, "Clicks", "Middle click", Waveforms.SharpCollision),

            // The haptic motor sits near the main click buttons, so a thumb resting
            // on the side buttons is further from the source and feels the same
            // waveform noticeably more weakly. These get the strongest click-grade
            // waveform to compensate, rather than simply being switched off.
            //
            // WINDOWS ONLY, and not by choice. On Windows these arrive as
            // XBUTTON1/XBUTTON2, ordinary mouse buttons the hook sees. On macOS
            // they never enter the HID interface at all - measured at four
            // observation points, down to raw HID reports, all absent - because
            // Options+ converts them over a private channel. Nothing to hook, at
            // any level, in any framework.
            //
            // They stay in the catalogue rather than being deleted so Windows keeps
            // working and saved preferences are not orphaned; ForCurrentPlatform
            // filters them out where they cannot fire.
            new(MouseBack, "Clicks", "Back (thumb)", Waveforms.SharpCollision, windowsOnly: true),
            new(MouseForward, "Clicks", "Forward (thumb)", Waveforms.SharpCollision, windowsOnly: true),

            // macOS reaches the thumb buttons INDIRECTLY. The presses themselves
            // never enter the HID interface - that was measured to exhaustion and
            // remains true - but Options+ posts an NSEvent gesture in their place,
            // and that we can see. The gesture carries the posting process's PID,
            // which is what separates it from a real trackpad swipe.
            //
            // OFF BY DEFAULT, unlike its Windows counterparts, because the
            // detection is a heuristic rather than a fact. The thumb WHEEL emits
            // the same gesture from the same process, and the only thing telling
            // them apart is that a wheel roll has scroll events around it. Rolling
            // the wheel after a pause can therefore produce one stray tick. That
            // is a fair trade for someone who wants thumb-button feedback, and not
            // one to impose on someone who never asked for it.
            new(MouseThumb, "Clicks", "Thumb button (back / forward)", Waveforms.SharpCollision,
                defaultEnabled: false, macOnly: true),

            // Scroll fires FAR more often than clicks, so it gets the lightest
            // waveform available - anything heavier becomes exhausting within
            // seconds. See ScrollCooldownMs in MouseInputSource for the rate limit
            // that stops fast scrolling turning into a continuous buzz.
            new(ScrollVertical, "Scroll", "Scroll wheel", Waveforms.SubtleCollision, hasDensity: true),

            // The thumb wheel gets a sharper waveform than the main wheel for two
            // reasons: it sits at the side of the mouse, further from the haptic
            // motor (the same reason the thumb buttons feel weak), and it has no
            // physical detents, so the haptic is the ONLY thing providing a sense
            // of discrete steps. A soft waveform there just reads as vibration.
            new(ScrollHorizontal, "Scroll", "Thumb wheel", Waveforms.SharpCollision, hasDensity: true),

            // Drag start/end bracket a gesture, so they want to feel like a pair:
            // a firmer "grab" and a softer "let go". Grouped for display, but each
            // stays independently switchable.
            new(DragLeftStart, "Gestures", "Drag (left)  -  start", Waveforms.SharpStateChange,
                groupKey: "drag.left"),
            new(DragLeftEnd, "Gestures", "Drag (left)  -  end", Waveforms.DampStateChange,
                groupKey: "drag.left"),

            // Middle and right drag are OFF by default. In CAD and 3D modelling
            // they are the orbit and pan gestures - held down and moved constantly -
            // so haptics there would be relentless for exactly the people who use
            // them most. Available for anyone who wants them, imposed on no one.
            new(DragMiddleStart, "Gestures", "Drag (middle)  -  start", Waveforms.SharpStateChange,
                defaultEnabled: false, groupKey: "drag.middle"),
            new(DragMiddleEnd, "Gestures", "Drag (middle)  -  end", Waveforms.DampStateChange,
                defaultEnabled: false, groupKey: "drag.middle"),
            new(DragRightStart, "Gestures", "Drag (right)  -  start", Waveforms.SharpStateChange,
                defaultEnabled: false, groupKey: "drag.right"),
            new(DragRightEnd, "Gestures", "Drag (right)  -  end", Waveforms.DampStateChange,
                defaultEnabled: false, groupKey: "drag.right"),

            // Hitting the edge of the desktop is the one place a mouse has a real
            // physical boundary with no feedback at all. Off by default: it fires
            // during ordinary cursor movement rather than on a deliberate action,
            // so it should be opted into rather than sprung on people.
            new(ScreenEdge, "Gestures", "Screen edge", Waveforms.SubtleCollision, defaultEnabled: false),
        };

        /// <summary>
        /// The events this operating system can actually deliver.
        /// </summary>
        /// <remarks>
        /// Use this for anything user-facing. <see cref="All"/> is the full
        /// catalogue and stays complete on every platform, so ids remain stable
        /// and a preference saved on one machine is never orphaned by opening the
        /// settings on another.
        ///
        /// This file is compiled into the plugin AND both settings applications,
        /// so all three filter identically without anything crossing the pipe.
        /// </remarks>
        public static readonly HapticEventDef[] ForCurrentPlatform =
            All.Where(e =>
                (!e.WindowsOnly || OperatingSystem.IsWindows())
                && (!e.MacOnly || OperatingSystem.IsMacOS())).ToArray();

        /// <summary>
        /// How closely wheel haptics are spaced. 1 is sparsest, 5 densest.
        /// </summary>
        /// <remarks>
        /// An abstract SCALE, not a value, because the underlying knob is different
        /// everywhere: milliseconds for the vertical wheel, rotation units for the
        /// thumb wheel on Windows, points of travel for it on macOS. Each input
        /// source maps this level onto its own units, so one control means the same
        /// thing to the user on both platforms without pretending the mechanisms
        /// are the same.
        ///
        /// Defined here because this file is compiled into the plugin and both
        /// settings applications, so the labels cannot drift between them.
        /// </remarks>
        public const Int32 DefaultDensity = 3;

        public static readonly String[] DensityLabels =
        {
            "Sparse", "Light", "Standard", "Dense", "Very dense",
        };

        /// <summary>Clamps a stored or received level into the valid range.</summary>
        public static Int32 ClampDensity(Int32 level) =>
            level < 1 ? 1 : level > DensityLabels.Length ? DensityLabels.Length : level;

        private static readonly Dictionary<String, HapticEventDef> ById =
            All.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        public static HapticEventDef Find(String id) =>
            id != null && ById.TryGetValue(id, out var def) ? def : null;
    }
}
