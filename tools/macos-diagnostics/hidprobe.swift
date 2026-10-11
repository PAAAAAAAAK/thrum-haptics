// Diagnostic probe for the macOS port. Throwaway - not part of the plugin.
//
// Answers two questions about why the thumb buttons never arrive on macOS,
// without building or changing the plugin:
//
//   A. CGGetEventTapList() - every active tap, its owner, location, and whether
//      it filters. Settles whether Options+ holds a tap ahead of ours, which is
//      the one hole left in the "nothing consumes them above IOKit" argument.
//
//   B. IOHIDManager - can the device be opened at all, and do the thumb buttons
//      appear in the raw HID reports? The open's IOReturn discriminates the two
//      failure modes that matter: exclusive access (Options+ holds it) versus
//      not permitted (TCC Input Monitoring).
//
// Run from a terminal, so TCC attributes to the terminal app rather than to
// LogiPluginService. That is deliberate: this measures whether the prize EXISTS
// before anyone spends effort on the permission problem.

import Foundation
import CoreGraphics
import IOKit
import IOKit.hid
import Darwin

// MARK: - helpers

func procPath(_ pid: pid_t) -> String {
    var buf = [CChar](repeating: 0, count: 4096)
    let r = proc_pidpath(pid, &buf, UInt32(buf.count))
    return r > 0 ? String(cString: buf) : "(unknown)"
}

func ioReturnName(_ r: IOReturn) -> String {
    switch UInt32(bitPattern: r) {
    case 0:          return "kIOReturnSuccess"
    case 0xe00002c5: return "kIOReturnExclusiveAccess  <- another process holds it exclusively"
    case 0xe00002e2: return "kIOReturnNotPermitted     <- TCC (Input Monitoring) refused"
    case 0xe00002e1: return "kIOReturnNotPrivileged"
    case 0xe00002bc: return "kIOReturnError"
    case 0xe00002c7: return "kIOReturnBadArgument"
    case 0xe00002d8: return "kIOReturnUnsupported"
    case 0xe00002cd: return "kIOReturnNotOpen"
    default:         return String(format: "0x%08x", UInt32(bitPattern: r))
    }
}

func usagePageName(_ page: Int) -> String {
    switch page {
    case 0x01: return "GenericDesktop"
    case 0x07: return "Keyboard"
    case 0x08: return "LED"
    case 0x09: return "Button"
    case 0x0C: return "Consumer"
    case 0x0D: return "Digitizer"
    default:   return String(format: "page:0x%02X", page)
    }
}

func tapLocationName(_ loc: UInt32) -> String {
    switch loc {
    case 0:  return "HID       (kCGHIDEventTap)"
    case 1:  return "Session   (kCGSessionEventTap)"
    case 2:  return "AnnotSess (kCGAnnotatedSessionEventTap)"
    default: return "loc:\(loc)"
    }
}

// Mouse-relevant CGEvent types, for decoding a tap's mask.
let mouseEventTypes: [(UInt32, String)] = [
    (1, "LMouseDown"), (2, "LMouseUp"), (3, "RMouseDown"), (4, "RMouseUp"),
    (5, "MouseMoved"), (6, "LMouseDragged"), (7, "RMouseDragged"),
    (10, "KeyDown"), (11, "KeyUp"), (12, "FlagsChanged"),
    (22, "ScrollWheel"), (23, "TabletPointer"), (24, "TabletProximity"),
    (25, "OtherMouseDown"), (26, "OtherMouseUp"), (27, "OtherMouseDragged"),
]

func decodeMask(_ mask: UInt64) -> String {
    var names: [String] = []
    for (t, n) in mouseEventTypes where mask & (UInt64(1) << UInt64(t)) != 0 {
        names.append(n)
    }
    if mask == ~UInt64(0) { return "ALL" }
    return names.isEmpty ? String(format: "0x%llx", mask) : names.joined(separator: ",")
}

// MARK: - Part A: event tap list

func dumpEventTaps() {
    print("========================================================")
    print(" A. CGGetEventTapList - every active event tap")
    print("========================================================")

    var count: UInt32 = 0
    var err = CGGetEventTapList(0, nil, &count)
    guard err == .success else {
        print("  CGGetEventTapList failed to count: \(err.rawValue)")
        return
    }
    if count == 0 {
        print("  No active taps reported.")
        return
    }

    var taps = [CGEventTapInformation](repeating: CGEventTapInformation(), count: Int(count))
    err = CGGetEventTapList(count, &taps, &count)
    guard err == .success else {
        print("  CGGetEventTapList failed to fill: \(err.rawValue)")
        return
    }

    print("  \(count) tap(s). Listed in delivery order (index 0 sees events first).\n")

    for (i, t) in taps.prefix(Int(count)).enumerated() {
        let owner = procPath(t.tappingProcess)
        let short = (owner as NSString).lastPathComponent
        // options: 0 = default (CAN modify/swallow), 1 = listen only
        let filters = t.options == .defaultTap ? "ACTIVE (can swallow)" : "listen-only"
        let target = t.processBeingTapped == 0
            ? "global"
            : "pid \(t.processBeingTapped) (\((procPath(t.processBeingTapped) as NSString).lastPathComponent))"

        print("  [\(i)] \(short)  pid=\(t.tappingProcess)")
        print("       location : \(tapLocationName(t.tapPoint.rawValue))")
        print("       options  : \(filters)")
        print("       enabled  : \(t.enabled)")
        print("       target   : \(target)")
        print("       mask     : \(decodeMask(t.eventsOfInterest))")
        print("       path     : \(owner)")
        print("")
    }
}

// MARK: - Part B: IOHIDManager

func prop(_ device: IOHIDDevice, _ key: String) -> Any? {
    return IOHIDDeviceGetProperty(device, key as CFString)
}

func intProp(_ device: IOHIDDevice, _ key: String) -> Int? {
    return prop(device, key) as? Int
}

func describe(_ device: IOHIDDevice) -> String {
    let vid = intProp(device, kIOHIDVendorIDKey) ?? 0
    let pid = intProp(device, kIOHIDProductIDKey) ?? 0
    let name = prop(device, kIOHIDProductKey) as? String ?? "(no name)"
    let transport = prop(device, kIOHIDTransportKey) as? String ?? "?"
    let up = intProp(device, kIOHIDPrimaryUsagePageKey) ?? 0
    let usage = intProp(device, kIOHIDPrimaryUsageKey) ?? 0
    return String(format: "VID 0x%04X PID 0x%04X  %@  [%@]  primary=%@/%d",
                  vid, pid, name, transport, usagePageName(up), usage)
}

let valueCallback: IOHIDValueCallback = { _, _, _, value in
    let element = IOHIDValueGetElement(value)
    let page = Int(IOHIDElementGetUsagePage(element))
    let usage = Int(IOHIDElementGetUsage(element))
    let v = IOHIDValueGetIntegerValue(value)

    // Relative pointer movement and scroll would drown everything else out.
    if page == 0x01 && (usage == 0x30 || usage == 0x31 || usage == 0x38) { return }
    if page == 0x0C && usage == 0x238 { return }

    let dev = IOHIDElementGetDevice(element)
    let pid = (IOHIDDeviceGetProperty(dev, kIOHIDProductIDKey as CFString) as? Int) ?? 0

    let stamp = String(format: "%.3f", Date().timeIntervalSince1970.truncatingRemainder(dividingBy: 1000))
    print(String(format: "  %@  PID 0x%04X  %@ usage=0x%02X (%d)  value=%d",
                 stamp, pid, usagePageName(page), usage, usage, v))
    fflush(stdout)
}

func probeHid(seconds: Double) {
    print("========================================================")
    print(" B. IOHIDManager - device access and raw reports")
    print("========================================================")

    let mgr = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))
    IOHIDManagerSetDeviceMatching(mgr, nil)

    guard let all = IOHIDManagerCopyDevices(mgr) as? Set<IOHIDDevice> else {
        print("  IOHIDManagerCopyDevices returned nothing.")
        return
    }

    // Enumeration needs no permission and no open, so this always tells us
    // something even if the open below is refused.
    let logitech = all.filter { (intProp($0, kIOHIDVendorIDKey) ?? 0) == 0x046D }

    print("  \(all.count) HID device(s) present, \(logitech.count) from Logitech (VID 0x046D).\n")
    print("  --- Logitech devices ---")
    for d in logitech.sorted(by: { (intProp($0, kIOHIDProductIDKey) ?? 0) < (intProp($1, kIOHIDProductIDKey) ?? 0) }) {
        print("   * \(describe(d))")

        if let elements = IOHIDDeviceCopyMatchingElements(d, nil, IOOptionBits(kIOHIDOptionsTypeNone)) as? [IOHIDElement] {
            var buttons: [Int] = []
            var consumer: [Int] = []
            for e in elements {
                let page = Int(IOHIDElementGetUsagePage(e))
                let usage = Int(IOHIDElementGetUsage(e))
                if page == 0x09 { buttons.append(usage) }
                if page == 0x0C { consumer.append(usage) }
            }
            let b = Set(buttons).sorted()
            let c = Set(consumer).sorted()
            print("       \(elements.count) elements; Button usages: \(b.isEmpty ? "none" : b.map(String.init).joined(separator: ","))")
            if !c.isEmpty {
                print("       Consumer usages: \(c.prefix(24).map { String(format: "0x%X", $0) }.joined(separator: ","))\(c.count > 24 ? " ..." : "")")
            }
        } else {
            print("       (could not read elements)")
        }
    }

    if logitech.isEmpty {
        print("  No Logitech device found - is the mouse connected?")
        return
    }

    print("\n  --- open attempts ---")
    var opened: [IOHIDDevice] = []
    for d in logitech {
        let r = IOHIDDeviceOpen(d, IOOptionBits(kIOHIDOptionsTypeNone))
        let pid = intProp(d, kIOHIDProductIDKey) ?? 0
        print(String(format: "   PID 0x%04X -> %@", pid, ioReturnName(r)))
        if r == kIOReturnSuccess { opened.append(d) }
    }

    if opened.isEmpty {
        print("\n  No device could be opened - see the IOReturn above for why.")
        return
    }

    for d in opened {
        IOHIDDeviceRegisterInputValueCallback(d, valueCallback, nil)
        IOHIDDeviceScheduleWithRunLoop(d, CFRunLoopGetCurrent(), CFRunLoopMode.defaultMode.rawValue)
    }

    print("""

      Listening for \(Int(seconds))s on \(opened.count) device(s).
      Pointer movement and the high-res scroll usage are filtered out.

      PRESS, slowly, with a pause between each:
        left, right, middle, BACK, FORWARD, then roll the wheel one notch.

    """)
    fflush(stdout)

    CFRunLoopRunInMode(.defaultMode, seconds, false)

    print("\n  Done listening.")
    for d in opened {
        IOHIDDeviceClose(d, IOOptionBits(kIOHIDOptionsTypeNone))
    }
}

// MARK: - main

let args = CommandLine.arguments
let mode = args.count > 1 ? args[1] : "all"
let listenSeconds = args.count > 2 ? (Double(args[2]) ?? 25) : 25

if mode == "taps" || mode == "all" {
    dumpEventTaps()
    print("")
}
if mode == "hid" || mode == "all" {
    probeHid(seconds: listenSeconds)
}
