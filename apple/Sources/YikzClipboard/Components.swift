import SwiftUI
import AppKit
import ClipCore

enum Format {
    static func bytes(_ n: Int64) -> String {
        let f = ByteCountFormatter()
        f.countStyle = .file
        f.allowedUnits = [.useBytes, .useKB, .useMB, .useGB]
        return f.string(fromByteCount: n)
    }

    static func relative(_ date: Date, now: Date = Date()) -> String {
        let s = now.timeIntervalSince(date)
        if s < 45 { return "Just now" }
        if s < 3600 { return "\(Int(s / 60)) min ago" }
        if s < 86400 { return "\(Int(s / 3600)) h ago" }
        let f = DateFormatter()
        f.dateFormat = Calendar.current.isDate(date, equalTo: now, toGranularity: .year) ? "d MMM" : "d MMM yyyy"
        return f.string(from: date)
    }

    static func full(_ date: Date) -> String {
        let cal = Calendar.current
        let time = date.formatted(date: .omitted, time: .shortened)
        if cal.isDateInToday(date) { return "Today at \(time)" }
        if cal.isDateInYesterday(date) { return "Yesterday at \(time)" }
        return date.formatted(date: .abbreviated, time: .shortened)
    }

    static func dayTitle(_ date: Date, now: Date = Date()) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(date) { return "Today" }
        if cal.isDateInYesterday(date) { return "Yesterday" }
        if let days = cal.dateComponents([.day], from: cal.startOfDay(for: date), to: cal.startOfDay(for: now)).day, days < 7 {
            return date.formatted(.dateTime.weekday(.wide))
        }
        return date.formatted(.dateTime.day().month(.wide).year())
    }
}

enum KindFilter: String, CaseIterable, Identifiable {
    case all = "All Types"
    case text = "Text"
    case links = "Links"
    case images = "Images"
    case files = "Files"

    var id: String { rawValue }

    var symbol: String {
        switch self {
        case .all: return "square.stack"
        case .text: return "text.alignleft"
        case .links: return "link"
        case .images: return "photo"
        case .files: return "doc"
        }
    }

    func matches(_ e: HistoryEntry) -> Bool {
        switch self {
        case .all: return true
        case .text: return e.kind == .text && !e.isLink
        case .links: return e.isLink
        case .images: return e.kind == .image
        case .files: return e.kind == .files
        }
    }
}

extension HistoryEntry {
    var symbol: String {
        switch kind {
        case .text: return isLink ? "link" : "text.alignleft"
        case .image: return "photo"
        case .files: return (meta?.files?.count ?? 1) > 1 ? "doc.on.doc" : "doc"
        }
    }

    var typeName: String {
        switch kind {
        case .text: return isLink ? "Link" : "Text"
        case .image: return "Image"
        case .files: return (meta?.files?.count ?? 1) > 1 ? "Files" : "File"
        }
    }

    var tint: Color {
        switch kind {
        case .text: return isLink ? .blue : .secondary
        case .image: return .purple
        case .files: return .orange
        }
    }

    var displayTitle: String {
        switch metaState {
        case .ok: return title
        case .unsupported: return "Unsupported item"
        case .corrupt: return "Unreadable item"
        }
    }
}

func platformSymbol(_ platform: String?) -> String {
    switch platform {
    case "macos": return "laptopcomputer"
    case "android": return "smartphone"
    case "windows": return "pc"
    case "web": return "globe"
    default: return "desktopcomputer"
    }
}

struct AppGlyph: View {
    var size: CGFloat = 28

    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.26, style: .continuous)
            .fill(LinearGradient(colors: [Color(red: 0.36, green: 0.42, blue: 1.0), Color(red: 0.55, green: 0.27, blue: 0.95)], startPoint: .topLeading, endPoint: .bottomTrailing))
            .overlay(
                Image(systemName: "doc.on.clipboard.fill")
                    .font(.system(size: size * 0.5, weight: .semibold))
                    .foregroundStyle(.white)
            )
            .frame(width: size, height: size)
            .shadow(color: .black.opacity(0.12), radius: 1, y: 0.5)
    }
}

struct KindTile: View {
    let entry: HistoryEntry
    var size: CGFloat = 26
    @Environment(AppModel.self) private var model
    @ViewState private var thumb: NSImage?

    var body: some View {
        ZStack {
            if let thumb {
                Image(nsImage: thumb)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
                    .frame(width: size, height: size)
                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                    .overlay(RoundedRectangle(cornerRadius: 6, style: .continuous).strokeBorder(.primary.opacity(0.1), lineWidth: 0.5))
            } else {
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .fill(entry.tint.opacity(0.14))
                    .frame(width: size, height: size)
                    .overlay(
                        Image(systemName: entry.symbol)
                            .font(.system(size: size * 0.46, weight: .medium))
                            .foregroundStyle(entry.tint)
                    )
            }
        }
        .task(id: entry.id) {
            guard entry.kind == .image else { return }
            if let cached = model.thumbnails.get(entry.id) {
                thumb = cached
                return
            }
            let img = await model.thumbnail(for: entry)
            withAnimation(.easeOut(duration: 0.15)) { thumb = img }
        }
    }
}

struct StatusDot: View {
    let color: Color
    var pulsing = false
    @ViewState private var pulse = false

    var body: some View {
        Circle()
            .fill(color)
            .frame(width: 8, height: 8)
            .overlay(
                Circle()
                    .stroke(color.opacity(0.5), lineWidth: 1.5)
                    .scaleEffect(pulse ? 2.2 : 1)
                    .opacity(pulse ? 0 : 0.8)
                    .opacity(pulsing ? 1 : 0)
            )
            .onAppear {
                guard pulsing else { return }
                withAnimation(.easeOut(duration: 1.4).repeatForever(autoreverses: false)) { pulse = true }
            }
    }
}

struct KeyHint: View {
    let keys: String

    var body: some View {
        Text(keys)
            .font(.system(size: 11, weight: .medium, design: .rounded))
            .foregroundStyle(.secondary)
            .padding(.horizontal, 5)
            .padding(.vertical, 1.5)
            .background(RoundedRectangle(cornerRadius: 4, style: .continuous).fill(.primary.opacity(0.07)))
    }
}

struct HoverHighlight: ViewModifier {
    var radius: CGFloat = 7
    var active = false
    @ViewState private var hovering = false

    func body(content: Content) -> some View {
        content
            .background(
                RoundedRectangle(cornerRadius: radius, style: .continuous)
                    .fill(Color.primary.opacity(active ? 0.1 : (hovering ? 0.06 : 0)))
            )
            .onHover { h in
                withAnimation(.easeOut(duration: 0.12)) { hovering = h }
            }
    }
}

extension View {
    func hoverHighlight(radius: CGFloat = 7, active: Bool = false) -> some View {
        modifier(HoverHighlight(radius: radius, active: active))
    }
}

struct VisualEffectBackground: NSViewRepresentable {
    var material: NSVisualEffectView.Material = .popover
    var blending: NSVisualEffectView.BlendingMode = .behindWindow

    func makeNSView(context: Context) -> NSVisualEffectView {
        let v = NSVisualEffectView()
        v.material = material
        v.blendingMode = blending
        v.state = .active
        return v
    }

    func updateNSView(_ v: NSVisualEffectView, context: Context) {
        v.material = material
        v.blendingMode = blending
    }
}

extension ConnectionStatus {
    var color: Color {
        switch self {
        case .connected: return .green
        case .connecting, .waiting: return .orange
        case .offline, .sleeping, .idle: return .gray
        case .stopped: return .red
        }
    }
}

extension AppModel {
    var statusTitle: String {
        switch account {
        case .signedOut: return "Not signed in"
        case .locked: return "Locked"
        case .signedIn: break
        }
        if settings.paused, status.isConnected { return "Paused" }
        switch status {
        case .idle: return "Starting"
        case .connecting(let attempt): return attempt == 0 ? "Connecting" : "Reconnecting"
        case .connected: return syncing ? "Syncing" : "Connected"
        case .waiting: return "Reconnecting"
        case .offline: return "Offline"
        case .sleeping: return "Asleep"
        case .stopped(let r):
            switch r {
            case .unauthorized: return "Signed out"
            case .protocolUnsupported: return "Update required"
            case .replaced: return "Disconnected"
            case .keyRejected: return "Locked"
            }
        }
    }

    func statusDetail(now: Date) -> String {
        switch account {
        case .signedOut: return "Sign in to start syncing"
        case .locked: return "Enter your encryption password"
        case .signedIn: break
        }
        switch status {
        case .connected(let since):
            if settings.paused { return "Clipboard is not sent or received" }
            let host = URL(string: settings.serverURL)?.host ?? settings.serverURL
            return "\(host) since \(since.formatted(date: .omitted, time: .shortened))"
        case .connecting: return "Opening a secure connection"
        case .waiting(let at, _, let reason):
            let s = max(0, Int(at.timeIntervalSince(now).rounded(.up)))
            return s > 0 ? "Retrying in \(s) s. \(reason.prefix(60))" : "Retrying now"
        case .offline: return "Waiting for the network"
        case .sleeping: return "Reconnects when the Mac wakes"
        case .idle: return "Not connected"
        case .stopped(let r):
            switch r {
            case .unauthorized: return "This Mac was signed out by the server"
            case .protocolUnsupported: return "The server needs a newer app version"
            case .replaced: return "Too many connections from this device"
            case .keyRejected: return "Encryption password needed"
            }
        }
    }

    var statusColor: Color {
        if !account.isSignedIn { return account == .signedOut ? .gray : .orange }
        if settings.paused { return .gray }
        return status.color
    }

    var menuBarSymbol: String {
        if !account.isSignedIn { return account == .signedOut ? "clipboard" : "lock" }
        if settings.paused { return "pause.circle" }
        switch status {
        case .connected: return transfers.contains { !$0.finished } ? "arrow.up.arrow.down.circle" : "doc.on.clipboard"
        case .connecting, .waiting, .idle: return "arrow.triangle.2.circlepath"
        case .offline, .sleeping: return "wifi.slash"
        case .stopped: return "exclamationmark.triangle"
        }
    }
}

typealias ViewState<Value> = SwiftUI.State<Value>
