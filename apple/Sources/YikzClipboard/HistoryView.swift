import SwiftUI
import AppKit
import UniformTypeIdentifiers
import ClipCore

struct HistoryGroup: Identifiable {
    let id: String
    let title: String
    let entries: [HistoryEntry]
}

@MainActor
@Observable
final class HistoryViewModel {
    var query = ""
    var filter: KindFilter = .all
    var selectedId: String?
    var focusToken = 0
    var scrollToken = 0
    var busy = false

    @ObservationIgnored weak var panel: NSPanel?

    func visible(_ model: AppModel) -> [HistoryEntry] {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return model.items.filter { e in
            guard filter.matches(e) else { return false }
            guard !q.isEmpty else { return true }
            if e.preview.localizedStandardContains(q) { return true }
            if let files = e.meta?.files, files.contains(where: { $0.name.localizedStandardContains(q) }) { return true }
            if let app = e.meta?.sourceApp, app.localizedStandardContains(q) { return true }
            if model.deviceName(for: e.deviceId).localizedStandardContains(q) { return true }
            if e.typeName.localizedStandardContains(q) { return true }
            return false
        }
    }

    func groups(_ entries: [HistoryEntry]) -> [HistoryGroup] {
        var out: [HistoryGroup] = []
        let pinned = entries.filter(\.pinned)
        if !pinned.isEmpty {
            out.append(HistoryGroup(id: "pinned", title: "Pinned", entries: pinned))
        }
        var current: (key: String, title: String, items: [HistoryEntry])?
        let cal = Calendar.current
        for e in entries where !e.pinned {
            let day = cal.startOfDay(for: e.createdAt)
            let key = "d\(Int(day.timeIntervalSince1970))"
            if current?.key != key {
                if let c = current { out.append(HistoryGroup(id: c.key, title: c.title, entries: c.items)) }
                current = (key, Format.dayTitle(e.createdAt), [])
            }
            current?.items.append(e)
        }
        if let c = current { out.append(HistoryGroup(id: c.key, title: c.title, entries: c.items)) }
        return out
    }

    func ordered(_ model: AppModel) -> [HistoryEntry] {
        groups(visible(model)).flatMap(\.entries)
    }

    func selected(_ model: AppModel) -> HistoryEntry? {
        guard let id = selectedId else { return nil }
        return model.items.first { $0.id == id }
    }

    func ensureSelection(_ model: AppModel) {
        let list = ordered(model)
        if let id = selectedId, list.contains(where: { $0.id == id }) { return }
        selectedId = list.first?.id
    }

    func move(_ delta: Int, _ model: AppModel) {
        let list = ordered(model)
        guard !list.isEmpty else { return }
        let idx = list.firstIndex { $0.id == selectedId } ?? -1
        let next = min(max(idx + delta, 0), list.count - 1)
        selectedId = list[next].id
        scrollToken += 1
    }

    func prepareForShow(_ model: AppModel) {
        query = ""
        filter = .all
        selectedId = nil
        ensureSelection(model)
        focusToken += 1
        scrollToken += 1
    }

    func handleKey(_ event: NSEvent, _ model: AppModel) -> Bool {
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        let cmd = flags.contains(.command)
        switch Int(event.keyCode) {
        case 53:
            WindowManager.shared.hideHistory()
            return true
        case 125:
            move(1, model)
            return true
        case 126:
            move(-1, model)
            return true
        case 121:
            move(8, model)
            return true
        case 116:
            move(-8, model)
            return true
        case 36, 76:
            guard let e = selected(model) else { return true }
            if cmd { paste(e, model) } else { copy(e, model) }
            return true
        case 35 where cmd:
            if let e = selected(model) { Task { await model.togglePin(e) } }
            return true
        case 51 where cmd:
            if let e = selected(model) { delete(e, model) }
            return true
        case 1 where cmd:
            if let e = selected(model) { saveAs(e, model) }
            return true
        case 3 where cmd:
            focusToken += 1
            return true
        case 13 where cmd:
            WindowManager.shared.hideHistory()
            return true
        default:
            return false
        }
    }

    func copy(_ e: HistoryEntry, _ model: AppModel) {
        guard !busy else { return }
        busy = true
        Task {
            let ok = await model.copy(e)
            busy = false
            if ok { WindowManager.shared.hideHistory() }
        }
    }

    func paste(_ e: HistoryEntry, _ model: AppModel) {
        guard !busy else { return }
        if !Accessibility.isTrusted {
            Accessibility.requestIfNeeded()
            model.notice = "Allow Yikz Clipboard in Accessibility settings to paste directly. The item was copied instead."
            copy(e, model)
            return
        }
        busy = true
        Task {
            let ok = await model.copy(e)
            busy = false
            guard ok else { return }
            WindowManager.shared.hideHistory(restoreFocus: true)
            try? await Task.sleep(for: .milliseconds(120))
            Accessibility.pasteToFrontmostApp()
        }
    }

    func delete(_ e: HistoryEntry, _ model: AppModel) {
        let list = ordered(model)
        if let idx = list.firstIndex(where: { $0.id == e.id }) {
            let next = idx + 1 < list.count ? list[idx + 1] : (idx > 0 ? list[idx - 1] : nil)
            selectedId = next?.id
        }
        Task { await model.delete(e) }
    }

    func saveAs(_ e: HistoryEntry, _ model: AppModel) {
        WindowManager.shared.hideHistory()
        Task { await model.saveAs(e) }
    }
}

struct HistoryView: View {
    @Environment(AppModel.self) private var model
    @Bindable var vm: HistoryViewModel
    @FocusState private var searchFocused: Bool

    var body: some View {
        let visible = vm.visible(model)
        let groups = vm.groups(visible)
        VStack(spacing: 0) {
            searchBar(count: visible.count)
            Divider()
            HStack(spacing: 0) {
                listColumn(groups)
                    .frame(width: 320)
                Divider()
                detailColumn
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            Divider()
            actionBar
        }
        .onChange(of: vm.focusToken) { searchFocused = true }
        .onChange(of: vm.query) { vm.ensureSelection(model) }
        .onChange(of: vm.filter) { vm.ensureSelection(model) }
        .onChange(of: model.items) { vm.ensureSelection(model) }
        .onAppear {
            vm.ensureSelection(model)
            searchFocused = true
        }
    }

    private func searchBar(count: Int) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 15, weight: .medium))
                .foregroundStyle(.secondary)
            TextField("Search clipboard history", text: $vm.query)
                .textFieldStyle(.plain)
                .font(.system(size: 17))
                .focused($searchFocused)
            if !vm.query.isEmpty {
                Button {
                    vm.query = ""
                } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary)
                }
                .buttonStyle(.plain)
            }
            Menu {
                Picker("Type", selection: $vm.filter) {
                    ForEach(KindFilter.allCases) { f in
                        Label(f.rawValue, systemImage: f.symbol).tag(f)
                    }
                }
                .pickerStyle(.inline)
                .labelsHidden()
            } label: {
                HStack(spacing: 5) {
                    Image(systemName: vm.filter.symbol)
                    Text(vm.filter.rawValue)
                }
                .font(.system(size: 12, weight: .medium))
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .padding(.horizontal, 9)
            .padding(.vertical, 5)
            .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(.primary.opacity(0.06)))
        }
        .padding(.horizontal, 16)
        .frame(height: 54)
    }

    private func listColumn(_ groups: [HistoryGroup]) -> some View {
        ScrollViewReader { proxy in
            ScrollView {
                if groups.isEmpty {
                    emptyState
                        .padding(.top, 80)
                } else {
                    LazyVStack(alignment: .leading, spacing: 1, pinnedViews: []) {
                        ForEach(groups) { g in
                            Text(g.title)
                                .font(.system(size: 11, weight: .semibold))
                                .foregroundStyle(.secondary)
                                .padding(.horizontal, 10)
                                .padding(.top, g.id == groups.first?.id ? 4 : 12)
                                .padding(.bottom, 4)
                            ForEach(g.entries) { e in
                                HistoryRow(entry: e, selected: e.id == vm.selectedId)
                                    .id(e.id)
                                    .onTapGesture(count: 2) { vm.copy(e, model) }
                                    .simultaneousGesture(TapGesture().onEnded { vm.selectedId = e.id })
                                    .contextMenu { rowMenu(e) }
                            }
                        }
                    }
                    .padding(8)
                }
            }
            .scrollIndicators(.automatic)
            .onChange(of: vm.scrollToken) {
                if let id = vm.selectedId {
                    withAnimation(.easeOut(duration: 0.12)) { proxy.scrollTo(id, anchor: nil) }
                }
            }
        }
    }

    @ViewBuilder
    private func rowMenu(_ e: HistoryEntry) -> some View {
        Button("Copy") { vm.copy(e, model) }
        Button("Paste to Frontmost App") { vm.paste(e, model) }
        Divider()
        Button(e.pinned ? "Unpin" : "Pin") { Task { await model.togglePin(e) } }
        Button("Save As\u{2026}") { vm.saveAs(e, model) }
        Divider()
        Button("Delete", role: .destructive) { vm.delete(e, model) }
    }

    private var emptyState: some View {
        VStack(spacing: 8) {
            Image(systemName: vm.query.isEmpty && vm.filter == .all ? "clipboard" : "magnifyingglass")
                .font(.system(size: 28))
                .foregroundStyle(.tertiary)
            Text(vm.query.isEmpty && vm.filter == .all ? "No items yet" : "No matching items")
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(.secondary)
            if !model.account.isSignedIn {
                Button("Sign In") {
                    WindowManager.shared.hideHistory()
                    WindowManager.shared.showSettings(tab: .account)
                }
                .controlSize(.small)
            }
        }
        .frame(maxWidth: .infinity)
    }

    @ViewBuilder
    private var detailColumn: some View {
        if let e = vm.selected(model) {
            DetailView(entry: e)
                .id(e.id)
        } else {
            VStack(spacing: 6) {
                Image(systemName: "doc.on.clipboard")
                    .font(.system(size: 30))
                    .foregroundStyle(.quaternary)
                Text("Select an item")
                    .font(.system(size: 12))
                    .foregroundStyle(.tertiary)
            }
        }
    }

    private var actionBar: some View {
        let e = vm.selected(model)
        return HStack(spacing: 4) {
            AppGlyph(size: 18)
            Text("Clipboard History")
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(.secondary)
                .padding(.leading, 4)
            if model.syncing || vm.busy {
                ProgressView().controlSize(.mini).padding(.leading, 6)
            }
            if let notice = model.notice {
                Text(notice)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .padding(.leading, 8)
            }
            Spacer(minLength: 8)
            ActionBarButton(title: "Save", keys: "\u{2318}S") { if let e { vm.saveAs(e, model) } }
            ActionBarButton(title: "Delete", keys: "\u{2318}\u{232B}") { if let e { vm.delete(e, model) } }
            ActionBarButton(title: e?.pinned == true ? "Unpin" : "Pin", keys: "\u{2318}P") {
                if let e { Task { await model.togglePin(e) } }
            }
            Divider().frame(height: 16).padding(.horizontal, 4)
            ActionBarButton(title: "Paste", keys: "\u{2318}\u{21A9}") { if let e { vm.paste(e, model) } }
            ActionBarButton(title: "Copy", keys: "\u{21A9}", prominent: true) { if let e { vm.copy(e, model) } }
        }
        .disabled(false)
        .padding(.horizontal, 12)
        .frame(height: 40)
        .background(.primary.opacity(0.025))
    }
}

struct ActionBarButton: View {
    let title: String
    let keys: String
    var prominent = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) {
                Text(title)
                    .font(.system(size: 12, weight: prominent ? .semibold : .regular))
                    .foregroundStyle(prominent ? .primary : .secondary)
                KeyHint(keys: keys)
            }
            .padding(.horizontal, 7)
            .padding(.vertical, 4)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .hoverHighlight(radius: 6)
    }
}

struct HistoryRow: View {
    @Environment(AppModel.self) private var model
    let entry: HistoryEntry
    let selected: Bool
    @ViewState private var hovering = false

    var body: some View {
        HStack(spacing: 10) {
            KindTile(entry: entry, size: 26)
            Text(entry.displayTitle)
                .font(.system(size: 13))
                .lineLimit(1)
                .truncationMode(.tail)
                .foregroundStyle(entry.metaState == .ok ? .primary : .secondary)
            Spacer(minLength: 4)
            if entry.pinned {
                Image(systemName: "pin.fill")
                    .font(.system(size: 9.5))
                    .foregroundStyle(.tertiary)
            }
            if entry.deviceId != model.ownDeviceId {
                Image(systemName: platformSymbol(model.platform(for: entry.deviceId)))
                    .font(.system(size: 10))
                    .foregroundStyle(.tertiary)
                    .help("From \(model.deviceName(for: entry.deviceId))")
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(selected ? Color.accentColor.opacity(0.22) : Color.primary.opacity(hovering ? 0.05 : 0))
        )
        .contentShape(Rectangle())
        .onHover { h in hovering = h }
        .animation(.easeOut(duration: 0.1), value: selected)
    }
}

struct DetailView: View {
    @Environment(AppModel.self) private var model
    let entry: HistoryEntry
    @ViewState private var content: ApplyContent?
    @ViewState private var loading = false
    @ViewState private var failure: String?

    var body: some View {
        VStack(spacing: 0) {
            preview
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            Divider()
            InfoSection(entry: entry)
        }
        .task(id: entry.id) { await load(force: false) }
    }

    private var needsManualDownload: Bool {
        !entry.isInline && entry.size > model.settings.engineSettings.autoDownloadLimit
    }

    private func load(force: Bool) async {
        failure = nil
        if let cached = await model.cachedContent(for: entry) {
            content = cached
            return
        }
        guard entry.metaState == .ok else { return }
        if needsManualDownload && !force { return }
        loading = true
        defer { loading = false }
        do {
            let c = try await model.content(for: entry)
            withAnimation(.easeOut(duration: 0.15)) { content = c }
        } catch {
            failure = (error as? APIError)?.userMessage ?? "\(error)"
        }
    }

    @ViewBuilder
    private var preview: some View {
        switch entry.metaState {
        case .unsupported:
            placeholder(symbol: "questionmark.square.dashed", title: "Unsupported item", message: "This item was created by a newer app version.")
        case .corrupt:
            placeholder(symbol: "exclamationmark.lock", title: "Cannot decrypt", message: "The item could not be decrypted with this Mac's key.")
        case .ok:
            switch entry.kind {
            case .text: textPreview
            case .image: imagePreview
            case .files: filesPreview
            }
        }
    }

    private func placeholder(symbol: String, title: String, message: String) -> some View {
        VStack(spacing: 8) {
            Image(systemName: symbol)
                .font(.system(size: 30))
                .foregroundStyle(.tertiary)
            Text(title).font(.system(size: 13, weight: .semibold))
            Text(message)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
        .padding(24)
    }

    private var fullText: String {
        if case .text(let s) = content { return s }
        return entry.preview
    }

    @ViewBuilder
    private var textPreview: some View {
        if let url = HistoryEntry.linkURL(in: fullText) {
            LinkCard(url: url)
        } else {
            ScrollView {
                let text = fullText
                let shown = text.count > 60_000 ? String(text.prefix(60_000)) : text
                VStack(alignment: .leading, spacing: 8) {
                    Text(shown)
                        .font(.system(size: 13, design: looksLikeCode(shown) ? .monospaced : .default))
                        .lineSpacing(2)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .topLeading)
                    if shown.count < text.count {
                        Text("Preview truncated. Copy to get the full text.")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                    }
                    if content == nil && loading {
                        ProgressView().controlSize(.small)
                    }
                }
                .padding(18)
            }
        }
    }

    private func looksLikeCode(_ s: String) -> Bool {
        let sample = s.prefix(2000)
        let markers = ["{", "}", ";", "=>", "func ", "def ", "import ", "</", "  "]
        return markers.filter { sample.contains($0) }.count >= 3
    }

    @ViewBuilder
    private var imagePreview: some View {
        if case .image(let data) = content, let img = NSImage(data: data) {
            Image(nsImage: img)
                .resizable()
                .interpolation(.high)
                .aspectRatio(contentMode: .fit)
                .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(.primary.opacity(0.08), lineWidth: 0.5))
                .shadow(color: .black.opacity(0.08), radius: 6, y: 2)
                .frame(maxWidth: CGFloat(entry.meta?.image?.width ?? 2000), maxHeight: CGFloat(entry.meta?.image?.height ?? 2000))
                .padding(20)
        } else {
            downloadState(symbol: "photo")
        }
    }

    @ViewBuilder
    private func downloadState(symbol: String) -> some View {
        VStack(spacing: 10) {
            ZStack {
                KindTile(entry: entry, size: 64)
            }
            if loading {
                ProgressView().controlSize(.small)
                if let t = model.transfers.first(where: { $0.id == entry.id }) {
                    Text("\(Format.bytes(t.completed)) of \(Format.bytes(t.total))")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                }
            } else if let failure {
                Text(failure)
                    .font(.system(size: 12))
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
                Button("Try Again") { Task { await load(force: true) } }
                    .controlSize(.small)
            } else if needsManualDownload {
                Text("\(Format.bytes(entry.size)) is above your auto-download limit.")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                Button("Download") { Task { await load(force: true) } }
                    .controlSize(.small)
            }
        }
        .padding(24)
    }

    @ViewBuilder
    private var filesPreview: some View {
        let files = entry.meta?.files ?? []
        VStack(spacing: 0) {
            ScrollView {
                VStack(spacing: 0) {
                    ForEach(Array(files.enumerated()), id: \.offset) { i, f in
                        HStack(spacing: 10) {
                            Image(nsImage: fileIcon(f.name))
                                .resizable()
                                .frame(width: 32, height: 32)
                            VStack(alignment: .leading, spacing: 1) {
                                Text(f.name)
                                    .font(.system(size: 13))
                                    .lineLimit(1)
                                    .truncationMode(.middle)
                                Text(Format.bytes(f.size))
                                    .font(.system(size: 11))
                                    .foregroundStyle(.secondary)
                                    .monospacedDigit()
                            }
                            Spacer()
                        }
                        .padding(.horizontal, 14)
                        .padding(.vertical, 7)
                        if i < files.count - 1 {
                            Divider().padding(.leading, 56)
                        }
                    }
                }
                .padding(.vertical, 10)
            }
            if case .files(let urls) = content {
                HStack {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                    Text("Downloaded")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting(urls) }
                        .controlSize(.small)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
            } else if loading || failure != nil || needsManualDownload {
                downloadState(symbol: "doc")
                    .frame(maxHeight: 150)
            }
        }
    }

    private func fileIcon(_ name: String) -> NSImage {
        let ext = (name as NSString).pathExtension
        let type = UTType(filenameExtension: ext) ?? .data
        return NSWorkspace.shared.icon(for: type)
    }
}

struct LinkCard: View {
    let url: URL

    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "globe")
                .font(.system(size: 30, weight: .light))
                .foregroundStyle(.blue)
                .frame(width: 64, height: 64)
                .background(Circle().fill(Color.blue.opacity(0.1)))
            Text(url.host ?? url.absoluteString)
                .font(.system(size: 15, weight: .semibold))
            Text(url.absoluteString)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .lineLimit(4)
                .textSelection(.enabled)
            Button {
                NSWorkspace.shared.open(url)
            } label: {
                Label("Open Link", systemImage: "arrow.up.right.square")
            }
            .controlSize(.small)
        }
        .padding(24)
    }
}

struct InfoSection: View {
    @Environment(AppModel.self) private var model
    let entry: HistoryEntry

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("Information")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.secondary)
                .padding(.bottom, 6)
            row("Source", source)
            row("Type", typeText)
            row("Size", Format.bytes(entry.size))
            row("Created", Format.full(entry.createdAt))
            row("Pinned", entry.pinned ? "Yes" : "No", last: true)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    private var source: String {
        let device = model.deviceName(for: entry.deviceId)
        if let app = entry.meta?.sourceApp, !app.isEmpty { return "\(device), \(app)" }
        return device
    }

    private var typeText: String {
        switch entry.kind {
        case .image:
            if let d = entry.meta?.image { return "Image, \(d.width) \u{00D7} \(d.height)" }
            return "Image"
        case .files:
            let n = entry.meta?.files?.count ?? 0
            return n == 1 ? "File" : "\(n) files"
        case .text:
            return entry.typeName
        }
    }

    private func row(_ label: String, _ value: String, last: Bool = false) -> some View {
        VStack(spacing: 0) {
            HStack {
                Text(label)
                    .foregroundStyle(.secondary)
                Spacer(minLength: 12)
                Text(value)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .textSelection(.enabled)
            }
            .font(.system(size: 12))
            .monospacedDigit()
            .padding(.vertical, 5)
            if !last { Divider().opacity(0.6) }
        }
    }
}
