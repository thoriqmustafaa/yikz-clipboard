import SwiftUI
import Combine
import AppKit
import ClipCore

struct GeneralPane: View {
    @Environment(AppModel.self) private var model
    @ViewState private var timer = Timer.publish(every: 2, on: .main, in: .common).autoconnect()

    var body: some View {
        Form {
            Section {
                Toggle("Launch at login", isOn: Binding(
                    get: { model.launchAtLogin },
                    set: { model.setLaunchAtLogin($0) }
                ))
                LabeledContent("History shortcut") {
                    ShortcutRecorder()
                }
            } footer: {
                Text("The shortcut opens Clipboard History from any app.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            Section("Permissions") {
                LabeledContent("Accessibility") {
                    HStack(spacing: 8) {
                        StatusDot(color: model.accessibilityTrusted ? .green : .orange)
                        Text(model.accessibilityTrusted ? "Allowed" : "Not allowed")
                            .foregroundStyle(.secondary)
                        if !model.accessibilityTrusted {
                            Button("Allow\u{2026}") {
                                Accessibility.requestIfNeeded()
                                Accessibility.openSettings()
                            }
                            .controlSize(.small)
                        }
                    }
                }
                Text("Needed only for Paste (\u{2318}\u{21A9}) in Clipboard History, which types \u{2318}V into the frontmost app.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            Section("Folders") {
                LabeledContent("Logs") {
                    Button("Show in Finder") { NSWorkspace.shared.open(Log.defaultDirectory) }
                        .controlSize(.small)
                }
                LabeledContent("Received files") {
                    Button("Show in Finder") {
                        try? FileManager.default.createDirectory(at: model.paths.received, withIntermediateDirectories: true)
                        NSWorkspace.shared.open(model.paths.received)
                    }
                    .controlSize(.small)
                }
            }
            Section {
                LabeledContent("Version") {
                    Text(model.appVersion).monospacedDigit().foregroundStyle(.secondary)
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 540, height: 470)
        .onAppear { model.refreshSystemState() }
        .onReceive(timer) { _ in model.refreshSystemState() }
    }
}

struct ShortcutRecorder: View {
    @Environment(AppModel.self) private var model
    @ViewState private var recording = false
    @ViewState private var monitor: Any?

    var body: some View {
        HStack(spacing: 6) {
            Button {
                recording ? stop() : start()
            } label: {
                Text(recording ? "Type shortcut\u{2026}" : model.settings.historyShortcut.display)
                    .font(.system(size: 12, weight: .medium, design: .rounded))
                    .frame(minWidth: 110)
                    .foregroundStyle(recording ? Color.accentColor : .primary)
            }
            .controlSize(.regular)
            if model.settings.historyShortcut != .defaultHistory && !recording {
                Button {
                    model.updateShortcut(.defaultHistory)
                } label: {
                    Image(systemName: "arrow.uturn.backward")
                }
                .buttonStyle(.borderless)
                .help("Reset to \(Shortcut.defaultHistory.display)")
            }
        }
        .onDisappear { stop() }
    }

    private func start() {
        recording = true
        HotKeyCenter.shared.unregister()
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            MainActor.assumeIsolated {
                handle(event)
            }
            return nil
        }
    }

    private func handle(_ event: NSEvent) {
        if event.keyCode == 53 {
            stop()
            return
        }
        let mods = HotKeyCenter.carbonModifiers(event.modifierFlags)
        guard mods & (Shortcut.cmd | Shortcut.option | Shortcut.control) != 0 else {
            NSSound.beep()
            return
        }
        let s = Shortcut(keyCode: UInt32(event.keyCode), modifiers: mods, key: HotKeyCenter.keyName(for: event))
        if let m = monitor { NSEvent.removeMonitor(m) }
        monitor = nil
        recording = false
        model.updateShortcut(s)
    }

    private func stop() {
        if let m = monitor { NSEvent.removeMonitor(m) }
        monitor = nil
        if recording {
            recording = false
            HotKeyCenter.shared.register(model.settings.historyShortcut)
        }
    }
}

struct AccountPane: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            switch model.account {
            case .signedOut: SignInForm()
            case .locked(let user): UnlockForm(username: user)
            case .signedIn: SignedInView()
            }
        }
        .frame(width: 540, height: 500)
    }
}

struct SignInForm: View {
    @Environment(AppModel.self) private var model
    @ViewState private var server = ""
    @ViewState private var username = ""
    @ViewState private var password = ""
    @ViewState private var encryption = ""
    @ViewState private var deviceName = ""
    @ViewState private var working = false
    @ViewState private var error: String?

    var body: some View {
        Form {
            Section {
                HStack(spacing: 12) {
                    AppGlyph(size: 40)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Sign in to your clipboard server")
                            .font(.system(size: 13, weight: .semibold))
                        Text("History is end-to-end encrypted with your encryption password.")
                            .font(.system(size: 11.5))
                            .foregroundStyle(.secondary)
                    }
                }
                .padding(.vertical, 4)
            }
            Section("Server") {
                TextField("Server URL", text: $server, prompt: Text(AppSettings.defaultServer))
                TextField("Username", text: $username)
                SecureField("Password", text: $password)
            }
            Section {
                SecureField("Encryption password", text: $encryption)
                TextField("Device name", text: $deviceName)
            } header: {
                Text("This Mac")
            } footer: {
                Text("The encryption password never leaves this Mac and must be the same on all your devices.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            Section {
                HStack {
                    if let error {
                        Label(error, systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.red)
                            .font(.system(size: 12))
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer()
                    if working { ProgressView().controlSize(.small) }
                    Button("Sign In") { submit() }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.borderedProminent)
                        .disabled(working || username.isEmpty || password.isEmpty || encryption.isEmpty)
                }
            }
        }
        .formStyle(.grouped)
        .onAppear {
            server = model.settings.serverURL
            username = model.settings.username
            deviceName = model.settings.deviceName
        }
    }

    private func submit() {
        working = true
        error = nil
        Task {
            do {
                try await model.signIn(server: server, username: username, password: password, encryptionPassword: encryption, deviceName: deviceName)
                password = ""
                encryption = ""
            } catch {
                self.error = error.localizedDescription
            }
            working = false
        }
    }
}

struct UnlockForm: View {
    @Environment(AppModel.self) private var model
    let username: String
    @ViewState private var encryption = ""
    @ViewState private var working = false
    @ViewState private var error: String?

    var body: some View {
        Form {
            Section {
                HStack(spacing: 12) {
                    Image(systemName: "lock.fill")
                        .font(.system(size: 22))
                        .foregroundStyle(.orange)
                        .frame(width: 40, height: 40)
                        .background(Circle().fill(.orange.opacity(0.12)))
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Enter your encryption password")
                            .font(.system(size: 13, weight: .semibold))
                        Text("Signed in as \(username) on \(URL(string: model.settings.serverURL)?.host ?? model.settings.serverURL).")
                            .font(.system(size: 11.5))
                            .foregroundStyle(.secondary)
                    }
                }
                .padding(.vertical, 4)
            }
            Section {
                SecureField("Encryption password", text: $encryption)
                    .onSubmit { submit() }
            } footer: {
                if let error {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.red)
                        .font(.footnote)
                }
            }
            Section {
                HStack {
                    Button("Sign Out", role: .destructive) {
                        Task { await model.signOut() }
                    }
                    Spacer()
                    if working { ProgressView().controlSize(.small) }
                    Button("Unlock") { submit() }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.borderedProminent)
                        .disabled(working || encryption.isEmpty)
                }
            }
        }
        .formStyle(.grouped)
    }

    private func submit() {
        guard !encryption.isEmpty else { return }
        working = true
        error = nil
        Task {
            do {
                try await model.unlock(encryptionPassword: encryption)
                encryption = ""
            } catch {
                self.error = error.localizedDescription
            }
            working = false
        }
    }
}

struct SignedInView: View {
    @Environment(AppModel.self) private var model
    @ViewState private var name = ""
    @ViewState private var renaming = false
    @ViewState private var error: String?
    @ViewState private var storage: StorageInfo?
    @ViewState private var confirmSignOut = false

    var body: some View {
        Form {
            Section {
                HStack(spacing: 12) {
                    AppGlyph(size: 40)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(model.settings.username)
                            .font(.system(size: 13, weight: .semibold))
                        HStack(spacing: 5) {
                            StatusDot(color: model.statusColor)
                            Text("\(model.statusTitle) to \(URL(string: model.settings.serverURL)?.host ?? model.settings.serverURL)")
                                .font(.system(size: 11.5))
                                .foregroundStyle(.secondary)
                        }
                    }
                    Spacer()
                    Button("Sign Out\u{2026}") { confirmSignOut = true }
                }
                .padding(.vertical, 4)
            }
            Section("This Mac") {
                LabeledContent("Device name") {
                    HStack(spacing: 6) {
                        TextField("", text: $name)
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 200)
                            .onSubmit { rename() }
                        if name != model.settings.deviceName {
                            Button("Save") { rename() }
                                .controlSize(.small)
                                .disabled(renaming)
                        }
                    }
                }
                LabeledContent("Device ID") {
                    Text(model.settings.deviceId ?? "")
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                }
                if let error {
                    Text(error).foregroundStyle(.red).font(.footnote)
                }
            }
            Section("Devices") {
                if model.deviceList.isEmpty {
                    Text("Device list loads when connected.")
                        .foregroundStyle(.secondary)
                } else {
                    ForEach(model.deviceList.filter { !$0.revoked }) { d in
                        HStack(spacing: 10) {
                            Image(systemName: platformSymbol(d.platform))
                                .frame(width: 20)
                                .foregroundStyle(.secondary)
                            Text(d.name)
                            if d.id == model.settings.deviceId {
                                Text("This Mac")
                                    .font(.system(size: 10, weight: .medium))
                                    .padding(.horizontal, 6)
                                    .padding(.vertical, 1)
                                    .background(Capsule().fill(.primary.opacity(0.07)))
                            }
                            Spacer()
                            let isOnline = model.online.contains { $0.deviceId == d.id }
                            StatusDot(color: isOnline ? .green : .gray.opacity(0.5))
                            Text(isOnline ? "Online" : Format.relative(d.lastSeenAt))
                                .font(.system(size: 11.5))
                                .foregroundStyle(.secondary)
                                .monospacedDigit()
                        }
                    }
                }
            }
            if let s = storage {
                Section("Server Storage") {
                    VStack(alignment: .leading, spacing: 6) {
                        ProgressView(value: Double(s.usedBytes), total: Double(max(1, s.limitBytes)))
                            .tint(s.diskLow ? .orange : .accentColor)
                        HStack {
                            Text("\(Format.bytes(s.usedBytes)) of \(Format.bytes(s.limitBytes))")
                            Spacer()
                            Text("\(s.itemCount) items, kept \(s.retentionDays) days")
                                .foregroundStyle(.secondary)
                        }
                        .font(.system(size: 11.5))
                        .monospacedDigit()
                    }
                }
            }
        }
        .formStyle(.grouped)
        .onAppear { name = model.settings.deviceName }
        .task {
            storage = await model.storageInfo()
            await model.engine.refreshDevices()
        }
        .confirmationDialog("Sign out of \(URL(string: model.settings.serverURL)?.host ?? "the server")?", isPresented: $confirmSignOut) {
            Button("Sign Out", role: .destructive) { Task { await model.signOut() } }
        } message: {
            Text("This Mac stops syncing and its local history cache is removed. Your items stay on the server.")
        }
    }

    private func rename() {
        guard name != model.settings.deviceName else { return }
        renaming = true
        error = nil
        Task {
            do {
                try await model.renameDevice(name)
            } catch {
                self.error = error.localizedDescription
            }
            renaming = false
        }
    }
}

struct SyncPane: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var settings = model.settings
        Form {
            Section {
                Toggle("Pause sync", isOn: Binding(get: { settings.paused }, set: { _ in model.togglePause() }))
            } footer: {
                Text("While paused, clipboard changes are not sent and received items are not placed on the clipboard. History still updates.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            Section("Sync These Types") {
                Toggle(isOn: $settings.syncText) { Label("Text and links", systemImage: "text.alignleft") }
                Toggle(isOn: $settings.syncImages) { Label("Images", systemImage: "photo") }
                Toggle(isOn: $settings.syncFiles) { Label("Files", systemImage: "doc") }
            }
            Section {
                LabeledContent("Auto-download up to") {
                    HStack(spacing: 6) {
                        TextField("", value: $settings.autoDownloadMB, format: .number)
                            .textFieldStyle(.roundedBorder)
                            .multilineTextAlignment(.trailing)
                            .frame(width: 70)
                        Stepper("", value: $settings.autoDownloadMB, in: 1...10_000, step: 10)
                            .labelsHidden()
                        Text("MB").foregroundStyle(.secondary)
                    }
                }
            } header: {
                Text("Large Items")
            } footer: {
                Text("Larger items from other devices show a notification and stay available in History.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            Section("Privacy") {
                Text("Items marked as concealed or transient by password managers are never sent or stored. Everything else is encrypted on this Mac before upload.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 540, height: 470)
        .onChange(of: settings.syncText) { model.pushSettings() }
        .onChange(of: settings.syncImages) { model.pushSettings() }
        .onChange(of: settings.syncFiles) { model.pushSettings() }
        .onChange(of: settings.autoDownloadMB) {
            if settings.autoDownloadMB < 1 { settings.autoDownloadMB = 1 }
            model.pushSettings()
        }
    }
}
