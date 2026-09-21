import AppKit
import Carbon.HIToolbox
import ClipCore

@MainActor
final class HotKeyCenter {
    static let shared = HotKeyCenter()

    private var ref: EventHotKeyRef?
    private var handler: EventHandlerRef?
    var action: (() -> Void)?

    @discardableResult
    func register(_ shortcut: Shortcut) -> Bool {
        unregister()
        installHandlerIfNeeded()
        let id = EventHotKeyID(signature: OSType(0x59494B5A), id: 1)
        var newRef: EventHotKeyRef?
        let status = RegisterEventHotKey(shortcut.keyCode, shortcut.modifiers, id, GetApplicationEventTarget(), 0, &newRef)
        if status == noErr {
            ref = newRef
            Log.info("Registered history shortcut \(shortcut.display)", "hotkey")
            return true
        }
        Log.error("Cannot register shortcut \(shortcut.display) (status \(status)); it may be used by another app", "hotkey")
        return false
    }

    func unregister() {
        if let ref {
            UnregisterEventHotKey(ref)
        }
        ref = nil
    }

    private func installHandlerIfNeeded() {
        guard handler == nil else { return }
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ -> OSStatus in
            MainActor.assumeIsolated {
                HotKeyCenter.shared.action?()
            }
            return noErr
        }, 1, &spec, nil, &handler)
    }

    static func carbonModifiers(_ flags: NSEvent.ModifierFlags) -> UInt32 {
        var m: UInt32 = 0
        if flags.contains(.command) { m |= Shortcut.cmd }
        if flags.contains(.option) { m |= Shortcut.option }
        if flags.contains(.shift) { m |= Shortcut.shift }
        if flags.contains(.control) { m |= Shortcut.control }
        return m
    }

    static func keyName(for event: NSEvent) -> String {
        let special: [UInt16: String] = [
            UInt16(kVK_Space): "Space", UInt16(kVK_Return): "\u{21A9}", UInt16(kVK_Tab): "\u{21E5}",
            UInt16(kVK_Delete): "\u{232B}", UInt16(kVK_ForwardDelete): "\u{2326}", UInt16(kVK_Escape): "\u{238B}",
            UInt16(kVK_LeftArrow): "\u{2190}", UInt16(kVK_RightArrow): "\u{2192}", UInt16(kVK_UpArrow): "\u{2191}",
            UInt16(kVK_DownArrow): "\u{2193}", UInt16(kVK_Home): "\u{2196}", UInt16(kVK_End): "\u{2198}",
            UInt16(kVK_PageUp): "\u{21DE}", UInt16(kVK_PageDown): "\u{21DF}",
            UInt16(kVK_F1): "F1", UInt16(kVK_F2): "F2", UInt16(kVK_F3): "F3", UInt16(kVK_F4): "F4",
            UInt16(kVK_F5): "F5", UInt16(kVK_F6): "F6", UInt16(kVK_F7): "F7", UInt16(kVK_F8): "F8",
            UInt16(kVK_F9): "F9", UInt16(kVK_F10): "F10", UInt16(kVK_F11): "F11", UInt16(kVK_F12): "F12"
        ]
        if let s = special[event.keyCode] { return s }
        let chars = event.charactersIgnoringModifiers ?? ""
        return chars.isEmpty ? "Key \(event.keyCode)" : chars.uppercased()
    }
}
