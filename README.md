# Thrum Haptics

Universal haptic feedback for the **Logitech MX Master 4** — clicks, scroll and
gestures, in every application.

The MX Master 4 has a haptic motor. This plugin puts it to work on the things you
actually do with a mouse, everywhere you do them.

- **Universal.** Responds to real mouse input, so it works in every application —
  your editor, your browser, your file manager, a game. Nothing to configure
  per-app.
- **Windows and macOS.** One package, both platforms — the same waveforms, the
  same settings, whichever machine you are on.
- **One install.** Download, double-click, done. The settings window ships inside
  the plugin — no companion app, nothing to sign into.
- **Yours to tune.** Every input can be switched off or given a different
  waveform, with a live preview so you choose by feel rather than by name, and
  the wheels let you set how closely their ticks are spaced.

### ⬇ [Download Thrum Haptics](https://github.com/PAAAAAAAAK/thrum-haptics/releases)

Grab the latest `.lplug4`, double-click it, then **confirm the prompt in Logi
Options+**. Needs an MX Master 4, Logi Options+, and Windows x64 or Apple
Silicon macOS.

<p align="center">
  <img src="docs/settings-window.png" alt="Thrum Haptics settings window on Windows" width="420">
  <img src="docs/settings-window-macos.png" alt="Thrum Haptics settings window on macOS" width="420">
</p>
<p align="center"><em>Windows and macOS — the same settings, native on each.</em></p>

## What it does

| Input | Default | On by default | Platform |
|---|---|---|---|
| Left click | `subtle_collision` | ✅ | both |
| Right click | `damp_collision` | ✅ | both |
| Middle click | `sharp_collision` | ✅ | both |
| Back (thumb) | `sharp_collision` | ✅ | Windows |
| Forward (thumb) | `sharp_collision` | ✅ | Windows |
| Thumb button (back / forward) | `sharp_collision` | ✗ | macOS |
| Scroll wheel | `subtle_collision` | ✅ | both |
| Thumb wheel | `sharp_collision` | ✅ | both |
| Drag start / end — left | `sharp_state_change` / `damp_state_change` | ✅ | both |
| Drag start / end — middle | as above | ✗ | both |
| Drag start / end — right | as above | ✗ | both |
| Screen edge | `subtle_collision` | ✗ | both |

Every entry can be switched off or given a different waveform. The settings
window only lists what the machine you are on can actually deliver, so you never
see a switch wired to nothing.

Middle and right drag are off by default because in CAD and 3D tools those are
the orbit and pan gestures — held down and moved constantly — so haptics there
would be relentless for exactly the people who use them most. Screen edge is off
because it fires during ordinary cursor movement rather than a deliberate action.

### Synthesized detents on the thumb wheel

The vertical wheel has physical detents, so in ratchet mode the hardware paces
the haptics for us. The thumb wheel has none — it rolls smoothly — so pacing it
by time gives a constant buzz no matter how fast you roll it.

It's paced by **distance** instead: travel accumulates and a tick fires once per
notch-equivalent. Roll slowly, ticks come slowly; roll fast, they come fast. The
haptic supplies the detents the hardware doesn't have.

### Smooth scrolling changes what the ticks follow

Logi Options+ has a **Smooth scrolling** option, applied per application, that
makes pages glide. With it on, Options+ consumes the wheel's physical detents and
emits an eased 60 Hz animation stream in their place — so what reaches this
plugin describes where the *page* is going, not where the wheel is.

Haptics are counted back out of that stream one tick per detent's worth of
rotation, so slow scrolling still ticks slowly and fast still ticks fast. But
they follow the eased motion, which is deliberately a little behind your finger.

- **Smooth scrolling off** — ticks lock to the physical detent.
- **Smooth scrolling on** — ticks follow the page, a frame or so late.

Neither is wrong; they are feedback for different things. If you want the haptic
to match the click under your finger exactly, turn smooth scrolling off for that
application.

### Density

Both wheels have a **Density** setting — Sparse, Light, Standard, Dense, Very
dense — controlling how closely their ticks are spaced.

It's a setting rather than a constant because there is no right answer to find.
The spacing was tuned by feel four times across two platforms and landed
somewhere different each time; how dense a wheel should feel is a preference, not
a fact. Standard is a starting point, not a verdict.

At the dense end the limit is the motor: asked to start a waveform before the
last one has finished, it stops producing distinct taps and becomes one
continuous buzz. *Very dense* sits deliberately at that edge.

### Thumb buttons on macOS

On Windows, back and forward arrive as ordinary mouse buttons and get a row each.

macOS never delivers them. Options+ converts the presses before they reach the
input system — measured at four observation points, down to raw HID reports, all
absent — and sends a navigation gesture instead. That gesture *is* detectable, so
macOS gets a single combined **Thumb button** row: it can tell that a thumb button
was pressed, but not which one.

It ships **off by default**. Telling that gesture apart from a thumb-wheel roll
relies on timing rather than on anything the event carries, so starting a roll
after a pause can occasionally produce one extra tick. That's a fair trade if you
want the feedback, and not one to impose if you don't.

## Installing

1. Download the latest `.lplug4` from
   [Releases](https://github.com/PAAAAAAAAK/thrum-haptics/releases).
2. Double-click it.
3. **Confirm the install prompt that appears in Logi Options+.**

Step 3 is easy to miss — the prompt opens in Options+, not next to the file, so
it can look as though nothing happened.

That's it: clicks and scroll start responding immediately, with no setup.

**If it feels too strong or too weak**, that's Logitech's own setting rather than
ours: Options+ → your mouse → **Haptic feedback** → **Haptic intensity**
(Subtle / Low / Medium / High). It scales everything, including this plugin. Use
the plugin's own settings to change *which* waveform each input plays; use
Options+ to change how hard the motor hits.

To uninstall, right-click the same `.lplug4` and choose **Uninstall Plugin**.

### Requirements

- Logitech **MX Master 4** — the only Logitech mouse with a controllable haptic motor
- **Logi Options+** (installs the Logi Plugin Service)
- **Windows x64**, or **macOS on Apple Silicon**
- **.NET 10 or newer**, for the settings window only — haptics work without it.
  If the settings window doesn't open, see
  [Settings window won't open](#settings-window-wont-open).

On macOS the haptics need Accessibility permission — but it attaches to
LogiPluginService, which Options+ already holds, so there is normally nothing for
you to grant. If nothing buzzes, check System Settings → Privacy & Security →
**Accessibility** and confirm LogiPluginService is enabled there.

## Troubleshooting

### Settings window won't open

If haptics work but pressing **Haptic Settings** does nothing, the settings
window is missing the .NET runtime it runs on. The haptics themselves run inside
Logi's own service, which is why they carry on regardless.

Install it from <https://dotnet.microsoft.com/download/dotnet/10.0>:

- **macOS** — in the **.NET Runtime** column, choose **macOS → Arm64 →
  Installer**, and run the `.pkg`. Not the SDK, and not the ASP.NET Core Runtime.
- **Windows** — Windows normally offers this itself the first time, with a
  download button. Otherwise choose **.NET Desktop Runtime → Windows → x64**.

Then press Haptic Settings again; nothing needs restarting. Any version from 10
upwards works.

To confirm that this is the cause on macOS, the plugin's log says so in plain
words:

```
grep -i "settings" ~/Library/Application\ Support/Logi/LogiPluginService/Logs/plugin_logs/ThrumHaptics.log | tail -10
```

On Windows the same log is at
`%LOCALAPPDATA%\Logi\LogiPluginService\Logs\plugin_logs\ThrumHaptics.log`. If it
shows something else, please
[open an issue](https://github.com/PAAAAAAAAK/thrum-haptics/issues) with those
lines.

## Settings

Bind the **Haptic Settings** action (Options+ → *All Actions → Thrum Haptics →
Haptics*) to a key or an Actions Ring slot. Pressing it opens the settings
window.

Each row is one input: switch it off, give it a different waveform, and for the
two wheels set how densely its ticks are spaced. Waveforms are labelled by
character — `short`, `medium`, `long` — and selecting one plays it immediately,
so you tune by feel rather than by name. Changes save as you make them; there's
no Save button.

Density changes don't preview: one waveform can't demonstrate spacing, so
playing it would say nothing about what just changed. You feel that by scrolling.

### Opening it from a mouse button instead

Logi Options+ only offers plugin actions on the Actions Ring, not in the
button-assignment list. If you would rather a button opened the settings
directly, point a **Smart Action → Application** at the bundled executable — it
defaults to the right connection when launched on its own, so it needs no
arguments:

**Windows**

```
%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\ThrumHaptics\bin\ThrumHapticsSettings.exe
```

**macOS**

```
~/Library/Application Support/Logi/LogiPluginService/Plugins/ThrumHaptics/bin/mac/ThrumHapticsSettingsMac
```

Assign that Smart Action to any button. Logi Options+ must be running, since the
window reads its settings from the plugin.

Clicks and scroll offer only the short waveforms. A long waveform there outlasts
the action that triggered it and the motor is still running when the next click
arrives, so those are left out rather than offered as a trap. Gestures, which
happen once per deliberate movement, get the full set.

There is deliberately no strength control here. Logitech already provides one —
Options+ → *Haptic intensity* — and it scales every haptic on the device,
including this plugin's. Duplicating it would give you two settings that fight
each other. Choose *which* waveform an input plays here; choose *how hard* the
motor hits in Options+.

## Privacy and security

- **Mouse input only.** On Windows the hook is created with
  `GlobalHookType.Mouse`, so only `WH_MOUSE_LL` is installed. On macOS the event
  taps subscribe to mouse, scroll and gesture events, and the keyboard event
  types are excluded explicitly. A keyboard hook is the same mechanism a
  keylogger uses and sees every keystroke including passwords; nothing here needs
  keystrokes, so it is never requested on either platform.
- **No network connections.** Nothing is sent anywhere. The settings window talks
  to the plugin over a local named pipe, which never touches the network stack.
- **No telemetry, no analytics, no data collection.** Preferences are stored
  locally.

## Building

```
dotnet tool install --global LogiPluginTool
dotnet build ThrumHapticsPlugin/src
```

`dotnet build` also builds the settings application, copies it into the plugin
output, writes a `.link` file into the Plugin Service's plugin directory and asks
it to reload — so the plugin runs straight from your build output. Use
`dotnet watch build` from `src/` while iterating.

To package a release, **use the script** rather than calling `pack` by hand:

```
./tools/pack-release.ps1 -Version 1.2.0
```

It builds each platform from a completely clean tree, nests the macOS output
under `bin/mac`, rewrites `pluginFolderMac` to match, and refuses to pack if the
version, the folder keys or either platform's files are missing.

The clean tree is not tidiness. Two things go wrong without it:

1. **Shared intermediates poison the second build.** Because
   `AppendRuntimeIdentifierToOutputPath` is false, the RID is stripped from the
   *intermediate* path too, so building Windows and then macOS shares
   `src/obj/Release`. The macOS package that comes out has an identical file
   list, an identical `deps.json` and an assembly of exactly the same size — and
   Options+ refuses to install it, every time, with a generic error. Eight
   consecutive packages failed that way while five built standalone installed
   first time. Nothing in the package content reveals it; only build provenance
   correlates.

2. **A build only ever *adds* to its output directory.** Rename or delete a
   project and the previous build's assemblies stay behind and get packaged
   alongside the new ones. Invisible in the build log, and `verify` passes
   happily, because the package is structurally fine.

### Project layout

- **`ThrumHapticsPlugin`** — the plugin. Targets plain `net10.0` with no desktop
  framework references (see below). `MouseInputSource` handles Windows via
  SharpHook; `MacMouseInputSource` handles macOS via CoreGraphics event taps.
- **`ThrumHapticsSettings`** — the Windows settings window, a WinForms executable.
- **`ThrumHapticsSettingsMac`** — the macOS settings window, an Avalonia
  executable. Separate because WinForms cannot run on macOS, and deliberately not
  unified: Windows has nothing to gain from the rewrite.

`HapticEvents.cs` and `Waveforms.cs` are compiled into **all three** projects
rather than sent over the pipe, so they share one definition of what events exist
and only values cross the process boundary.

### Why macOS looks nothing like Windows internally

LogiPluginService runs with the **hardened runtime** and without
`com.apple.security.cs.disable-library-validation`, so it will only load code
signed by Apple or by Logitech. SharpHook's `libuiohook.dylib` is neither, and
signing it ourselves cannot help — it would carry our team ID, not theirs.

So macOS ships no native binary of ours at all. `MacMouseInputSource` P/Invokes
CoreGraphics directly: `CGEventTapCreate` at HID level for mouse and scroll, plus
a second session-level tap for the navigation gestures Options+ posts in place of
the thumb buttons. Both are listen-only.

### Three things that will bite you

All are already handled, but each cost real time to diagnose:

1. **`LogiPluginTool` scaffolds `net8.0`, which will not compile.** The installed
   `PluginApi.dll` (6.4.x) is built against .NET 10, so referencing it from a
   `net8.0` project fails with `CS1705`. The target framework must match the
   Plugin Service's runtime.

2. **SharpHook ships native binaries for every platform**, and the Plugin
   Service's assembly resolver picks the first `runtimes/*/native/` match it
   finds — observed loading `win-arm64` on an x64 machine. The resulting
   `DllNotFoundException` is **fatal and kills LogiPluginService outright**.
   Fixed by pinning `RuntimeIdentifier=win-x64` together with
   `AppendRuntimeIdentifierToOutputPath=false` (the latter because setting a RID
   otherwise shifts the output path and breaks `pluginFolderWin: bin`).

3. **A WinForms reference in the plugin assembly fails `logiplugintool verify`.**
   The verifier inspects the assembly with a metadata resolver that can't load
   desktop framework assemblies, which blocks Marketplace submission. That's why
   the settings window is a separate executable rather than an in-process form.

Also note `Assembly.Location` is **empty** for a loaded plugin — the service uses
a collectible load context. Use the SDK's `AssemblyFilePath` instead.

## Roadmap

- [x] Click haptics (5 buttons)
- [x] Scroll wheel and thumb wheel
- [x] Drag gestures and screen edge
- [x] Settings window
- [x] macOS support
- [ ] Per-device filtering — a trackpad or second mouse currently drives the
      motor too, because both `WH_MOUSE_LL` and `CGEventTap` deliver input
      already merged across devices. Fixing it needs device-level input: Raw
      Input on Windows, `IOHIDManager` on macOS.

## Licence

MIT — see [LICENSE](LICENSE). End user terms: [EULA](EULA.md).

Free, and staying free. Nothing is gated behind sponsorship — but if you like the
plugin and want to support me, a coffee is very welcome:
[GitHub Sponsors](https://github.com/sponsors/PAAAAAAAAK) ·
[Buy Me a Coffee](https://buymeacoffee.com/paaaaaaaak).
