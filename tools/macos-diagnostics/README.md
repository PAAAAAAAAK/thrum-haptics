# macOS diagnostics

Throwaway probes. **Not part of the plugin** — nothing links against them, and
they ship in no package. They live here because the answers they produced cost a
full session to establish, and because they are the fastest way to find out
whether a macOS update has changed any of it.

Both are single-file Swift programs with no dependencies beyond the system
frameworks.

## `hidprobe.swift`

```bash
swiftc -O hidprobe.swift -o /tmp/hidprobe
/tmp/hidprobe taps      # every active event tap, in delivery order
/tmp/hidprobe hid 30    # Logitech device enumeration, open result, input values
/tmp/hidprobe all
```

`taps` calls `CGGetEventTapList` and prints every tap in the system with its
owning process, location (HID vs session), whether it is **active or listen-only**,
whether it is enabled, and a decoded event mask. Index 0 sees events first.

`hid` enumerates HID devices, filters to Logitech (VID `0x046D`), attempts
`IOHIDDeviceOpen`, and decodes the `IOReturn` — importantly distinguishing
`kIOReturnExclusiveAccess` (another process holds the device) from
`kIOReturnNotPermitted` (TCC Input Monitoring refused), which look identical
from the outside but mean entirely different things.

## `descdump.swift`

```bash
swiftc -O descdump.swift -o /tmp/descdump
/tmp/descdump 30
```

Full element dump grouped by usage page, then a **raw input report** listener.

The raw listener matters: `IOHIDDeviceRegisterInputValueCallback` only surfaces
*parsed* elements, so Logitech's HID++ notifications — which ride on vendor
report IDs — would never appear there. This registers
`IOHIDDeviceRegisterInputReportCallback` instead and prints every report byte,
flagging report IDs `0x10` and `0x11` as HID++ short and long.

## What they established

In short:

- Our event tap is **index 0** — first in the system. No tap anywhere carries
  `OtherMouseDown` in an active mask, so nothing is swallowing the thumb buttons.
- `com.logi.pluginservice` does **not** hold Input Monitoring;
  `com.logi.cp-dev-mgr` (Options+) does.
- The device opens **non-exclusively** alongside Options+ — but only with Input
  Monitoring granted.
- Back and forward appear in **neither** the parsed values nor the raw reports.
  534 reports captured, all `reportID=2`, button byte only ever `00/01/02/04`,
  zero HID++ notifications. The presses never reach the HID interface.

## Permissions

The `hid` half needs **Input Monitoring**, granted to whichever terminal or app
runs the binary — that requirement is itself one of the findings. `taps` needs
nothing. Reading the TCC database directly (`kTCCServiceListenEvent` lives in
`/Library/Application Support/com.apple.TCC/TCC.db`) needs Full Disk Access.
