import SwiftUI
import AppKit
import os
import ClipCore

final class LogInbox: Sendable {
    private let pending = OSAllocatedUnfairLock<(items: [LogEntry], scheduled: Bool)>(initialState: ([], false))

    func push(_ e: LogEntry) -> Bool {
        pending.withLock { s in
            s.items.append(e)
            if s.scheduled { return false }
            s.scheduled = true
            return true
        }
    }

    func drain() -> [LogEntry] {
        pending.withLock { s in
            let out = s.items
            s.items = []
            s.scheduled = false
            return out
        }
    }
}

@MainActor
@Observable
final class LogsViewModel {
    var entries: [LogEntry] = []
    var minLevel: LogLevel = .debug
    var query = ""
    var autoScroll = true
    var selection = Set<UInt64>()
    var appendToken = 0
    @ObservationIgnored private var observer: UUID?
    @ObservationIgnored private let inbox = LogInbox()

    func attach() {
        guard observer == nil else { return }
        entries = Log.shared.entries()
        let inbox = self.inbox
        observer = Log.shared.addObserver { [weak self] e in
            if inbox.push(e) {
                Task { @MainActor in
                    try? await Task.sleep(for: .milliseconds(150))
                    self?.flush()
                }
            }
        }
    }

    func detach() {
        if let observer { Log.shared.removeObserver(observer) }
        observer = nil
    }

    private func flush() {
        let new = inbox.drain()
        guard !new.isEmpty else { return }
        entries.append(contentsOf: new)
        if entries.count > Log.shared.capacity {
            entries.removeFirst(entries.count - Log.shared.capacity)
        }
        appendToken += 1
    }

    var filtered: [LogEntry] {
        let q = query.trimmingCharacters(in: .whitespaces)
        return entries.filter { e in
            e.level >= minLevel && (q.isEmpty || e.message.localizedCaseInsensitiveContains(q) || e.category.localizedCaseInsensitiveContains(q))
        }
    }

    func text(for ids: Set<UInt64>?) -> String {
        let list = filtered.filter { ids?.contains($0.id) ?? true }
        return list.map(\.line).joined(separator: "\n")
    }

    func copySelection() {
        let text = self.text(for: selection.isEmpty ? nil : selection)
        guard !text.isEmpty else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }

    func export() {
        let panel = NSSavePanel()
        let stamp = Date().formatted(.iso8601.year().month().day())
        panel.nameFieldStringValue = "YikzClipboard-logs-\(stamp).txt"
        panel.allowedContentTypes = [.plainText]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try (text(for: nil) + "\n").write(to: url, atomically: true, encoding: .utf8)
        } catch {
            Log.error("Log export failed: \(error.localizedDescription)", "logs")
        }
    }
}

struct LogsView: View {
    @ViewState private var vm = LogsViewModel()

    var body: some View {
        let rows = vm.filtered
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Picker("Level", selection: $vm.minLevel) {
                    Text("All").tag(LogLevel.debug)
                    Text("Info").tag(LogLevel.info)
                    Text("Warnings").tag(LogLevel.warning)
                    Text("Errors").tag(LogLevel.error)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                HStack(spacing: 6) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Filter", text: $vm.query)
                        .textFieldStyle(.plain)
                }
                .padding(.horizontal, 8)
                .padding(.vertical, 5)
                .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(.primary.opacity(0.06)))
                .frame(maxWidth: 260)
                Toggle("Auto-scroll", isOn: $vm.autoScroll)
                    .toggleStyle(.checkbox)
                Spacer()
                Text("\(rows.count) lines")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                Button {
                    vm.copySelection()
                } label: {
                    Label(vm.selection.isEmpty ? "Copy All" : "Copy", systemImage: "doc.on.doc")
                }
                Button {
                    vm.export()
                } label: {
                    Label("Export", systemImage: "square.and.arrow.up")
                }
                Button {
                    NSWorkspace.shared.open(Log.defaultDirectory)
                } label: {
                    Label("Open Folder", systemImage: "folder")
                }
            }
            .controlSize(.small)
            .padding(.horizontal, 12)
            .padding(.vertical, 9)
            Divider()
            ScrollViewReader { proxy in
                List(rows, selection: $vm.selection) { e in
                    LogRow(entry: e)
                        .id(e.id)
                        .listRowInsets(EdgeInsets(top: 1, leading: 6, bottom: 1, trailing: 6))
                }
                .listStyle(.plain)
                .environment(\.defaultMinListRowHeight, 18)
                .onCopyCommand {
                    [NSItemProvider(object: vm.text(for: vm.selection.isEmpty ? nil : vm.selection) as NSString)]
                }
                .onChange(of: vm.appendToken) {
                    if vm.autoScroll, let last = vm.filtered.last {
                        proxy.scrollTo(last.id, anchor: .bottom)
                    }
                }
                .onAppear {
                    if let last = rows.last { proxy.scrollTo(last.id, anchor: .bottom) }
                }
            }
        }
        .frame(minWidth: 640, minHeight: 360)
        .onAppear { vm.attach() }
        .onDisappear { vm.detach() }
    }
}

struct LogRow: View {
    let entry: LogEntry

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(entry.date.formatted(.dateTime.hour(.twoDigits(amPM: .omitted)).minute(.twoDigits).second(.twoDigits).secondFraction(.fractional(3))))
                .foregroundStyle(.secondary)
                .frame(width: 92, alignment: .leading)
            Text(entry.level.label)
                .font(.system(size: 10, weight: .semibold, design: .rounded))
                .foregroundStyle(levelColor)
                .frame(width: 44, alignment: .leading)
            Text(entry.category)
                .foregroundStyle(.secondary)
                .frame(width: 70, alignment: .leading)
                .lineLimit(1)
            Text(entry.message)
                .foregroundStyle(entry.level >= .warning ? levelColor : .primary)
                .lineLimit(3)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .font(.system(size: 11.5, design: .monospaced))
        .monospacedDigit()
    }

    private var levelColor: Color {
        switch entry.level {
        case .debug: return .secondary
        case .info: return .blue
        case .warning: return .orange
        case .error: return .red
        }
    }
}
