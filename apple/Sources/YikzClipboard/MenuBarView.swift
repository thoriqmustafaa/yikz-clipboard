import SwiftUI
import ClipCore

struct MenuBarView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 0) {
            header
            if let notice = model.notice {
                NoticeBanner(text: notice, symbol: "info.circle.fill", tint: .blue) { model.notice = nil }
                    .padding(.horizontal, 12)
                    .padding(.bottom, 8)
            }
            if let w = model.storageWarning {
                NoticeBanner(text: "Server disk is low (\(Format.bytes(w.freeDiskBytes)) free). Large uploads are paused.", symbol: "externaldrive.badge.exclamationmark", tint: .orange, onClose: nil)
                    .padding(.horizontal, 12)
                    .padding(.bottom, 8)
            }
            if !model.transfers.isEmpty {
                VStack(spacing: 6) {
                    ForEach(model.transfers) { TransferRow(transfer: $0) }
                }
                .padding(.horizontal, 14)
                .padding(.bottom, 10)
            }
            Divider().padding(.horizontal, 12)
            switch model.account {
            case .signedIn:
                DevicesStrip()
                Divider().padding(.horizontal, 12)
                RecentSection(close: close)
            case .locked:
                OnboardingCard(symbol: "lock.fill", title: "Encryption password needed", message: "Enter your encryption password to decrypt your synced history.", action: "Unlock") {
                    close()
                    WindowManager.shared.showSettings(tab: .account)
                }
            case .signedOut:
                OnboardingCard(symbol: "doc.on.clipboard", title: "Sync your clipboard", message: "Sign in to your server to share text, images and files across your devices, end-to-end encrypted.", action: "Sign In") {
                    close()
                    WindowManager.shared.showSettings(tab: .account)
                }
            }
            Divider().padding(.horizontal, 12)
            actions
        }
        .frame(width: 360)
        .onAppear {
            Task { await model.connection.probe(reason: "menu opened") }
        }
    }

    private func close() {
        dismiss()
    }

    private var header: some View {
        HStack(spacing: 11) {
            AppGlyph(size: 32)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text("Yikz Clipboard")
                        .font(.system(size: 13, weight: .semibold))
                    StatusDot(color: model.statusColor, pulsing: model.status.isConnected && !model.settings.paused)
                        .id(model.statusColor)
                    Text(model.statusTitle)
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(model.statusColor == .gray ? .secondary : model.statusColor)
                }
                TimelineView(.periodic(from: .now, by: 1)) { ctx in
                    Text(model.statusDetail(now: ctx.date))
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                        .lineLimit(1)
                        .truncationMode(.tail)
                }
            }
            Spacer(minLength: 4)
            if model.account.isSignedIn {
                Button {
                    model.reconnect()
                } label: {
                    Image(systemName: "arrow.clockwise")
                        .font(.system(size: 12, weight: .semibold))
                        .frame(width: 26, height: 26)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .hoverHighlight(radius: 6)
                .help("Reconnect now")
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 14)
        .padding(.bottom, 12)
    }

    private var actions: some View {
        VStack(spacing: 1) {
            if let version = model.updates.readyVersion {
                MenuActionRow(symbol: "arrow.down.circle.fill", title: "Restart to Update (\(version))", hint: nil) {
                    close()
                    model.updates.installNow()
                }
                Divider().padding(.vertical, 4).padding(.horizontal, 6)
            }
            MenuActionRow(symbol: "clock.arrow.circlepath", title: "Open History", hint: model.settings.historyShortcut.display) {
                close()
                WindowManager.shared.showHistory()
            }
            MenuActionRow(symbol: "paperplane", title: "Send Clipboard Now", hint: nil) {
                model.sendClipboardNow()
            }
            .disabled(!model.account.isSignedIn)
            MenuActionRow(symbol: model.settings.paused ? "play.circle" : "pause.circle", title: model.settings.paused ? "Resume Sync" : "Pause Sync", hint: nil) {
                model.togglePause()
            }
            .disabled(!model.account.isSignedIn)
            Divider().padding(.vertical, 4).padding(.horizontal, 6)
            MenuActionRow(symbol: "gearshape", title: "Settings", hint: "\u{2318},") {
                close()
                WindowManager.shared.showSettings()
            }
            MenuActionRow(symbol: "arrow.triangle.2.circlepath.circle", title: "Check for Updates\u{2026}", hint: nil) {
                close()
                model.updates.checkNow()
                WindowManager.shared.showSettings(tab: .updates)
            }
            MenuActionRow(symbol: "list.bullet.rectangle", title: "Logs", hint: nil) {
                close()
                WindowManager.shared.showLogs()
            }
            MenuActionRow(symbol: "power", title: "Quit Yikz Clipboard", hint: "\u{2318}Q") {
                NSApp.terminate(nil)
            }
            .keyboardShortcut("q")
        }
        .padding(6)
    }
}

struct MenuActionRow: View {
    let symbol: String
    let title: String
    let hint: String?
    let action: () -> Void
    @Environment(\.isEnabled) private var enabled

    var body: some View {
        Button(action: action) {
            HStack(spacing: 9) {
                Image(systemName: symbol)
                    .font(.system(size: 13))
                    .frame(width: 18)
                    .foregroundStyle(.secondary)
                Text(title)
                    .font(.system(size: 13))
                Spacer()
                if let hint {
                    Text(hint)
                        .font(.system(size: 12))
                        .foregroundStyle(.tertiary)
                }
            }
            .padding(.horizontal, 8)
            .frame(height: 26)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .opacity(enabled ? 1 : 0.45)
        .hoverHighlight(radius: 6)
    }
}

struct NoticeBanner: View {
    let text: String
    let symbol: String
    let tint: Color
    let onClose: (() -> Void)?

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: symbol)
                .foregroundStyle(tint)
                .font(.system(size: 12))
                .padding(.top, 1)
            Text(text)
                .font(.system(size: 11.5))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            if let onClose {
                Button(action: onClose) {
                    Image(systemName: "xmark")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundStyle(.secondary)
                        .frame(width: 16, height: 16)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(9)
        .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(tint.opacity(0.1)))
        .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous).strokeBorder(tint.opacity(0.18), lineWidth: 0.5))
    }
}

struct TransferRow: View {
    let transfer: TransferProgress

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Image(systemName: transfer.direction == .upload ? "arrow.up.circle.fill" : "arrow.down.circle.fill")
                    .foregroundStyle(transfer.failure == nil ? Color.accentColor : .red)
                    .font(.system(size: 12))
                Text(transfer.title)
                    .font(.system(size: 12, weight: .medium))
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer()
                Text(transfer.failure != nil ? "Failed" : "\(Format.bytes(transfer.completed)) of \(Format.bytes(transfer.total))")
                    .font(.system(size: 11))
                    .foregroundStyle(transfer.failure != nil ? .red : .secondary)
                    .monospacedDigit()
            }
            ProgressView(value: transfer.fraction)
                .progressViewStyle(.linear)
                .controlSize(.small)
                .tint(transfer.failure == nil ? .accentColor : .red)
                .animation(.easeOut(duration: 0.2), value: transfer.completed)
        }
    }
}

struct DevicesStrip: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionLabel(title: "Devices", trailing: model.otherOnlineDevices.isEmpty ? nil : "\(model.otherOnlineDevices.count) online")
            if model.otherOnlineDevices.isEmpty {
                Text(model.status.isConnected ? "No other devices online" : "Device list appears when connected")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
            } else {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 6) {
                        ForEach(model.otherOnlineDevices) { d in
                            HStack(spacing: 5) {
                                Image(systemName: platformSymbol(d.platform))
                                    .font(.system(size: 11))
                                    .foregroundStyle(.secondary)
                                Text(d.name)
                                    .font(.system(size: 12))
                                    .lineLimit(1)
                                Circle().fill(.green).frame(width: 6, height: 6)
                            }
                            .padding(.horizontal, 9)
                            .padding(.vertical, 4)
                            .background(Capsule().fill(.primary.opacity(0.06)))
                        }
                    }
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct SectionLabel: View {
    let title: String
    var trailing: String?

    var body: some View {
        HStack {
            Text(title.uppercased())
                .font(.system(size: 10.5, weight: .semibold))
                .tracking(0.4)
                .foregroundStyle(.secondary)
            Spacer()
            if let trailing {
                Text(trailing)
                    .font(.system(size: 11))
                    .foregroundStyle(.tertiary)
            }
        }
    }
}

struct RecentSection: View {
    @Environment(AppModel.self) private var model
    let close: () -> Void
    @ViewState private var copiedId: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            SectionLabel(title: "Recent", trailing: model.items.isEmpty ? nil : "\(model.items.count) in history")
                .padding(.horizontal, 8)
            if model.items.isEmpty {
                Text(model.syncing ? "Loading history" : "Copy something to get started")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 6)
            } else {
                VStack(spacing: 1) {
                    ForEach(model.items.prefix(8)) { entry in
                        RecentRow(entry: entry, copied: copiedId == entry.id) {
                            Task {
                                if await model.copy(entry) {
                                    withAnimation(.easeOut(duration: 0.15)) { copiedId = entry.id }
                                    try? await Task.sleep(for: .milliseconds(900))
                                    withAnimation(.easeOut(duration: 0.2)) { copiedId = nil }
                                }
                            }
                        }
                    }
                }
            }
        }
        .padding(.horizontal, 6)
        .padding(.vertical, 10)
    }
}

struct RecentRow: View {
    @Environment(AppModel.self) private var model
    let entry: HistoryEntry
    let copied: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                KindTile(entry: entry, size: 28)
                VStack(alignment: .leading, spacing: 1) {
                    Text(entry.displayTitle)
                        .font(.system(size: 12.5))
                        .lineLimit(1)
                        .truncationMode(.tail)
                    HStack(spacing: 4) {
                        Text(model.deviceName(for: entry.deviceId))
                        Text("\u{00B7}")
                        TimelineView(.periodic(from: .now, by: 30)) { ctx in
                            Text(Format.relative(entry.createdAt, now: ctx.date))
                        }
                    }
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                    .lineLimit(1)
                }
                Spacer(minLength: 4)
                if copied {
                    Label("Copied", systemImage: "checkmark")
                        .labelStyle(.titleAndIcon)
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(.green)
                        .transition(.opacity.combined(with: .scale(scale: 0.9)))
                } else if entry.pinned {
                    Image(systemName: "pin.fill")
                        .font(.system(size: 10))
                        .foregroundStyle(.tertiary)
                }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .hoverHighlight(radius: 7)
        .help("Click to copy")
    }
}

struct OnboardingCard: View {
    let symbol: String
    let title: String
    let message: String
    let action: String
    let perform: () -> Void

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 26, weight: .regular))
                .foregroundStyle(.secondary)
                .padding(.top, 6)
            Text(title)
                .font(.system(size: 14, weight: .semibold))
            Text(message)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            Button(action: perform) {
                Text(action).frame(minWidth: 90)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.regular)
            .padding(.top, 2)
        }
        .padding(.horizontal, 24)
        .padding(.vertical, 18)
        .frame(maxWidth: .infinity)
    }
}
