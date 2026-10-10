# macOS — technical reference

**The port is done and shipped in 1.2.0.** This was the handoff document written
to continue the work on a Mac; it is kept as a reference because the measurements
in it were expensive to obtain and are not recoverable from the code.

Read this, then read `git log`. The commit messages carry the reasoning behind
every decision, not just the changes.

**Read fact 6 with its correction.** It concluded the thumb buttons were closed,
and that conclusion was too broad — the presses genuinely never arrive, but every
observation point in that table looked for a mouse *button* and none looked for
what Options+ sends in their place. That turned out to be an NSEvent gesture,
which is detectable, and it ships. The table is still correct about what it
measured. It was the inference that overreached.

Three things below are also superseded by the shipped implementation:

- **Device filtering** is implemented, and not through `IOHIDManager`. Every
  event already says which device sent it — see *The mission*, item 2.
- **Screen edge** is implemented, per display rather than by bounding box.
- **Scroll pacing** is a user setting now, not a constant. The line delta is
  accelerated beyond recovery — the same wheel reports ~3 points rolled slowly
  and ~147 in a flick — so physical detents cannot be reconstructed at all, and
  the honest answer was to let the user choose the spacing.

---

## Where the port stands

Shipped in 1.2.0. Verified on macOS 26.6.1, Apple Silicon, with an MX Master 4
over Bluetooth.

**Working:** left / right / middle click, drag start and end, vertical scroll,
thumb-wheel scroll, screen edge, and the settings window. Haptics fire in every
application. The event taps survive sustained use with no OS timeouts.

**Thumb buttons:** the presses themselves are unreachable (fact 6), but the
gesture Options+ posts in their place is not. They ship as one combined
`mouse.thumb` event, off by default — see *The mission*, item 1.

**Device filtering:** added after 1.2.0. Only Logitech pointers drive haptics;
`MacDeviceFilter` judges each event by the device that sent it — see *The
mission*, item 2.

**Screen edge:** implemented per display in `MacMouseInputSource.AtWall`, using
`CGDisplayBounds` over `CGGetActiveDisplayList`. A bounding box of all displays
was tried first and is the wrong model; Windows later took the same per-display
logic for the same reason.

---

## Established facts — do not re-derive these

Each cost real time to establish. They are recorded in commit messages too.

### 1. No native library of ours can load inside the Plugin Service

This is the constraint that shapes everything. `LogiPluginService.app` is signed
with the hardened runtime:

```
flags=0x10000(runtime)   TeamIdentifier=QED4VVPZWA
entitlements: apple-events, allow-dyld-environment-variables, allow-jit
```

No `com.apple.security.cs.disable-library-validation`. Hardened runtime enables
library validation, which permits `dlopen` only for code signed by Apple or by
the host's own team. **Our dylibs cannot load, and signing them does not help** —
they would carry our team, not Logitech's. On Apple Silicon it fails twice over,
since arm64 rejects unsigned libraries outright.

Consequences:

- **SharpHook is unusable on macOS.** Not because it lacks macOS support — it has
  it — but because it reaches libuiohook through `DllImport`. This is why
  `MacMouseInputSource` exists instead.
- Apple-signed system frameworks are fine, which is why P/Invoking CoreGraphics
  and CoreFoundation works.
- The **settings application is a separate process**, so this does not apply to
  it. Avalonia's `libSkiaSharp.dylib` loads there without trouble.

### 2. Accessibility is already granted to LogiPluginService

Both Options+ and LogiPluginService appear in System Settings → Privacy &
Security → Accessibility, enabled. `CGEventTapCreate` therefore succeeds and
macOS costs the user no extra setup step. `AXIsProcessTrusted()` is logged at
plugin load to make this visible.

### 3. Packaging strips the execute bit

A `.lplug4` is a zip written on Windows, which has no Unix mode to store. The
extracted settings executable came out `OtherRead, GroupRead, UserWrite,
UserRead` — no execute bit anywhere. `EnsureExecutable` in `ThrumHapticsPlugin.cs`
restores it at launch. **If the build moves to the Mac and the zip starts
preserving modes, keep this anyway** — it is cheap and it protects a failure mode
that is invisible until it happens.

### 4. Gatekeeper does not block the settings application

The .NET SDK ad-hoc signs the arm64 apphost even when cross-building from
Windows (`LC_CODE_SIGNATURE` present). Quarantine is not propagated to files the
Plugin Service unpacks, so an ad-hoc signed, un-notarized executable launches
fine. Confirmed on device.

### 5. Scroll behaves nothing like Windows

`continuous=1` always — the wheel is reported as a high-resolution scroller, so
several events arrive per physical detent. The line delta (`DeltaAxis1/2`) is
**accelerated**: the same detent reports 1 rolled slowly and 16 rolled fast, so
it measures post-acceleration distance and is never a detent count. The Windows
approach of counting rotation toward a fixed threshold (1080) is meaningless
here.

Momentum-phase events are skipped — that is inertial scrolling the OS continues
after the wheel stops, and it was the bulk of the over-firing.

Pacing is no longer a constant. The vertical wheel is rate-limited by the user's
Density setting (35 ms at Standard), and the thumb wheel accumulates points of
travel towards a per-density step, since it has no physical detents at all. See
`ScrollCooldownByDensity` and `ThumbWheelPointsByDensity` in
`MacMouseInputSource`.

### 6. The thumb buttons are unreachable, and this is now measured

Established on device 2026-08-10, on the Mac, against the live v1.0.104 build.
This closes the question rather than narrowing it. Do not reopen it without new
information — every available observation point was tried:

| Observation point | Back / forward |
|---|---|
| `CGEventTap`, session level | absent |
| `CGEventTap`, **HID level, first tap in the system** | absent |
| `IOHIDManager`, parsed input values | **absent** (buttons 1,2,3 fire) |
| `IOHIDManager`, **raw input reports** (534 captured) | **absent** |

The presses never enter the HID interface at all, so there is nothing for any
client to read. Options+ receives them over a private channel — not HID++, since
zero `reportID=17` notifications appeared while the buttons were pressed. The
buttons *do* work: they navigate, so Options+ is actively converting them.

Two corollaries worth keeping:

- **No framework changes this.** A native Swift app calls the same APIs and gets
  the same nothing. The wall is below the API layer.
- The one untried lever is unbinding back/forward inside Options+, which might
  stop the diversion. **Deliberately not pursued** — changing a user's Options+
  defaults is not something this plugin should require.

### 7. `CGGetEventTapList` settles the tap-ordering question

22 active taps. **Ours is index 0** — first in the entire system, HID level,
listen-only, enabled. More conclusive than the ordering argument: *no tap
anywhere, in any process, carries `OtherMouseDown` in an **active** (swallowing)
mask*. Options+'s only active HID tap covers left/right/keys/scroll, excludes
OtherMouse, and is disabled. The "Options+ consumes them with its own tap"
theory is dead by direct evidence, not by elimination.

### 8. LogiPluginService does NOT hold Input Monitoring

From the system TCC database, which is where `kTCCServiceListenEvent` lives:

```
kTCCServiceListenEvent | com.logi.cp-dev-mgr    | 2 (allowed)   <- Options+
kTCCServiceListenEvent | com.logi.pluginservice | ABSENT        <- us
kTCCServiceAccessibility | com.logi.pluginservice | 2 (allowed) <- confirms the method
```

Absent, not denied — never prompted, so System Settings shows no row for it.
Options+ holding it is a *different bundle* and does not carry to us. Our plugin
runs in pid `com.logi.pluginservice` (`/Applications/Utilities/LogiPluginService.app`),
confirmed by `lsof` holding the plugin directory and the settings pipe.

Measured consequence: `IOHIDDeviceOpen` returns `kIOReturnNotPermitted` without
it, and element enumeration is blocked too. With it granted, the same call
returns `kIOReturnSuccess` **alongside a running Options+** — so the device is
*not* held exclusively, and non-exclusive HID access genuinely works.

One later measurement sits awkwardly beside this and is recorded rather than
explained away: called from INSIDE the Plugin Service, `IOHIDCheckAccess` reports
listen access as **granted**. The figures above came from a terminal tool, not
from the service, and `IOHIDDeviceOpen` was never attempted from inside it. It no
longer needs to be — device filtering went another way — but "the service cannot
open HID devices" is an inference from this section, not something it measured.

---

## The mission

The two verifications this document used to open with are **answered** — see
established facts 6, 7 and 8. What follows is what remains.

### 1. Back / forward — SOLVED, via the substitute rather than the press

The presses are unreachable exactly as fact 6 measured. What that table missed is
that it only ever asked "is there a button?", and the answer to "what arrives
instead?" is different.

Options+ posts an **NSEvent gesture (CGEventType 29)** in their place. Ten
presses produced ten isolated pairs of them, at the right times, with no scroll
anywhere near. A session-level tap sees them; the HID-level tap does not, because
it sits upstream of where Options+ injects.

What separates them from everything else that emits type 29:

- **Trackpad swipes** carry no source PID — they come from hardware. The thumb
  buttons carry Options+'s PID, because Options+ posted them. Clean and reliable.
- **The thumb wheel** carries the same PID and cannot be separated that way. Only
  timing distinguishes it: a wheel roll has scroll events around its gestures, a
  button press had none within eighteen seconds.

Back and forward are **indistinguishable from each other** — same PID, same
fields, every time — so macOS gets one combined `mouse.thumb` event rather than
two. It ships off by default, because the second filter is a heuristic.

The lesson worth keeping: "unreachable" was true of the mechanism and false of
the outcome, and the table's authority made the overreach hard to see.

### 2. Device filtering — SOLVED, and without IOHIDManager

`CGEventTap` delivers input merged across every pointing device, so the
MX Master used to buzz when you used the trackpad. This section used to say the
way out was `IOHIDManager`, blocked on Input Monitoring. It was not needed.

**Every hardware event already carries the registry id of the service that sent
it, in integer field 87 of the CGEvent.** Measured on device, inside the Plugin
Service, with Options+ running:

| Input | What the event carries |
|---|---|
| MX Master 4: move, click, drag | sender → "MX Master 4", vendor `0x046D` |
| MX Master 4: both wheels, thumb-button gesture | no sender; posted by `logioptionsplus_agent` (field 41) |
| Kensington SlimBlade Pro: movement | sender → the trackball, vendor `0x047D` |
| Kensington SlimBlade Pro: clicks, scroll | sender → `tbwDKPointer`, its driver's virtual pointer, vendor `0x047D` |
| Built-in trackpad | sender → "Apple Internal Keyboard / Trackpad", vendor `0x05AC` |

The id resolves through IOKit's registry calls, which are public and need no
permission. Field 87 agreed with the private `IOHIDEventGetSenderID` on every
kind of event and was present on a drag where the IOHIDEvent was not, so the
private calls were dropped. Reading it costs nothing measurable.

Field 87 is **not documented**. `MacDeviceFilter` therefore engages only once it
has identified a Logitech pointer as a sender on that machine, and remembers
that across restarts. If the field ever changes, the plugin behaves as 1.2.0 did.

Two things the trackpad taught, both now in the code:

- **Coasting is not use.** After a two-finger swipe the trackpad sends scroll
  events for a second or more with nobody touching it. In one ten-second window
  it sent 2,083 events, of which 788 were momentum scrolling and 708 gestures.
  Treating all of that as "the trackpad is in use" silenced the first thumb press
  after every swipe.
- **Options+ input has to wait for the other device.** The wheels carry no
  sender, so on their own they play whatever else is going on — including when
  the thumb wheel reports a point or two of travel without being rolled, which
  it was measured doing while the trackball was in use. They count once the
  other device has been
  untouched for a quarter second, the same rule and number as on Windows.

Through a **Bolt receiver** the sender resolves to "USB Receiver", still vendor
`0x046D` — which is why the vendor is matched and not the product. Clicks through
it were verified on device; switching back to Bluetooth gives the mouse a new
sender id, which is identified afresh.

Not yet tried: a machine with two Logitech pointers.

### 3. Unaccelerated scroll detents — untested, possibly free

`reportID=2` carries the wheel as raw HID usage `GenericDesktop 0x38`, not the
accelerated line delta `CGEventTap` reports. If that is a true detent count it
would properly solve the scroll-pacing problem that `b0c0286` had to defer as
unmeasurable — the reason pacing is a 50 ms cooldown rather than a real detent
boundary.

Not measured: the capture session only pressed buttons. One 15-second capture
with wheel rolls would settle it. Cheap, and it would retire a known compromise.

### 4. We are leaking event taps

`CGGetEventTapList` shows **six** taps owned by LogiPluginService — one enabled,
five stale and disabled, spanning older builds (one carries the very first
build's mask: `LMouseDown,RMouseDown,ScrollWheel,OtherMouseDown`, no drag). Each
plugin reload creates a tap without destroying the last.

`MacMouseInputSource.Stop()` disables the tap, stops the run loop and CFReleases
both the tap and its run loop source — but never calls `CFMachPortInvalidate` on
the tap, and never `CFRunLoopRemoveSource`. A CFRelease alone does not remove the
port from the system tap list while another reference survives.

Harmless today (they are disabled), but it accumulates across reloads and it
means teardown is not doing what its comments claim.

---

## Constraints — these are not up for renegotiation

- **Never install a keyboard hook or keyboard event tap.** Not on any platform,
  not temporarily for diagnosis. It is the API keyloggers use, it sees every
  password typed, and it is the single biggest reason security software flags
  software like this. The README makes this promise publicly. Two buttons do not
  justify breaking it.
- **No companion app, no local server, no browser configuration, no network
  access.** One install, working immediately, with sensible defaults. This is the
  entire reason the project exists — every competing plugin fails here.
- **Do not touch `MouseInputSource.cs` or the Windows build.** Windows ships
  today, is on GitHub Releases, and is pending Marketplace submission. macOS work
  must not put it at risk.
- **Keep Logitech's official waveform names.** Renaming them would mean
  maintaining a translation layer against the SDK docs and every other plugin,
  for no functional gain.
- Haptic **strength is not controllable** — verified, `PluginApi.dll` exposes
  `get_WaveformIndex` and nothing else. Do not add a strength setting; Options+
  has a global Haptic intensity control and duplicating it would give users two
  settings that fight.

---

## Decisions taken on the Mac, 2026-08-10

Recorded because each reverses or resolves something this document previously
left open. Nothing here has been implemented yet.

### Do not touch the user's Options+ configuration

Unbinding back/forward inside Options+ might stop the diversion and hand the
buttons back. **Rejected.** Requiring a user to change their Options+ defaults
to make our plugin work is a configuration step by another name, and the whole
premise is one install with sensible defaults.

Consequence: `mouse.back` and `mouse.forward` come out of the macOS catalogue.

### The settings window stays Avalonia — the SwiftUI proposal was reversed

**Superseded 2026-08-10, after the Mac session.** SwiftUI was proposed here on
size grounds and then rejected by the project owner. Recorded rather than deleted
because the size figures are real and someone will reasonably raise it again.

| | unpacked | packed |
|---|---|---|
| Windows | 1.03 MB | 0.39 MB |
| macOS, Avalonia | 25.1 MB | 10.4 MB |
| macOS, SwiftUI (estimated) | ~1–2 MB | ~0.5 MB |

`libSkiaSharp.dylib` is 14.8 MB of it. What settled it:

- **No size limit exists.** Neither the SDK docs nor the Marketplace submission
  guidance specifies one. For scale, LogiPluginService itself ships a **8.98 MB**
  `libSkiaSharp.dll` — the same library. 10.4 MB packed is not out of family.
- **Cross-building from Windows is worth more than the megabytes.** `swiftc` runs
  only on macOS, so SwiftUI would mean every macOS change and every release must
  be cut on the Mac. Avalonia keeps the whole project buildable from one machine,
  which is the practical reality of how it is developed.
- The Avalonia window is **already working** on device. SwiftUI would have been a
  rewrite of working code sitting on the critical path to a macOS release.

One piece of that proposal was genuinely good and is worth keeping in mind
independently: `CATALOG` and `WAVEFORMS` pipe commands, so the settings UI renders
what the plugin sends at **runtime** rather than holding a compiled-in copy.
Strictly stronger than compile-time sharing, which relies on both sides being
rebuilt together — and **framework-independent**, so it was never an argument for
SwiftUI specifically. Not implemented: with Avalonia staying, both sides compile
the same `HapticEvents.cs` and cannot drift, so it solves a problem we do not
currently have.

---

## Diagnostics

`tools/macos-diagnostics/` holds the two throwaway probes that established facts
6, 7 and 8. They are not part of the plugin and nothing links against them —
kept because re-deriving these answers cost a session, and because they are the
fastest way to check whether a macOS update has changed any of it.

```bash
swiftc -O tools/macos-diagnostics/hidprobe.swift -o /tmp/hidprobe
/tmp/hidprobe taps      # every active event tap, in delivery order
/tmp/hidprobe hid 30    # device open result, then raw input values

swiftc -O tools/macos-diagnostics/descdump.swift -o /tmp/descdump
/tmp/descdump 30        # full element dump, then RAW input reports
```

**Both need Input Monitoring for the HID half**, granted to whichever terminal
or app runs them — which is itself the finding. `taps` needs nothing.

---

## Building and testing on the Mac

Needs the .NET 10 SDK and `dotnet tool install --global LogiPluginTool`.

```bash
dotnet build ThrumHapticsPlugin/src -c Release -r osx-arm64
```

`ThrumHapticsPlugin.csproj` already carries the macOS paths. On a Mac,
`IsDevLoopBuild` becomes true, so the build writes a `.link` file into the
Plugin Service's plugin directory and sends a reload — the same fast loop
Windows has. **This path has never been executed**, having only ever been
cross-compiled. Expect to fix it. In particular `PluginDir` contains an escaped
space (`~/Library/Application\ Support/...`) that may not survive the `Exec`
task.

Packaging, if needed:

```bash
logiplugintool pack ./ThrumHapticsPlugin/bin-mac/Release ./dist/ThrumHaptics_mac.lplug4
logiplugintool verify ./dist/ThrumHaptics_mac.lplug4
```

Note `bin-mac/`, not `bin/` — macOS builds go to a separate tree so they never
clobber the Windows output.

Plugin log:

```
~/Library/Application Support/Logi/LogiPluginService/Logs/plugin_logs/ThrumHaptics.log
```

The settings application's stdout and stderr are captured into that same log, so
an Avalonia failure or a missing dylib appears there.

### Size, and why packaging is still undecided

| | unpacked | packed |
|---|---|---|
| Windows | 1.03 MB | 0.39 MB |
| macOS | 25.1 MB | 10.37 MB |

Almost all of it is `libSkiaSharp.dylib` (14.8 MB), Avalonia's renderer. Windows
and Linux backends are already stripped from the macOS build. A single
dual-platform `.lplug4` would make every Windows user download a Skia renderer
they will never run, at 26× the current size — so **two separate packages** looks
right, but that has not been settled and the Marketplace may only accept one per
listing.

---

## Git

Small commits, freely. Commit messages explain **why**, including what was tried
and rejected — the whole handoff works because past decisions are recoverable
from history. Follow the existing style on this branch.

Do not merge to `main` until macOS is genuinely tested end to end; `main` carries
the shipping Windows release.
