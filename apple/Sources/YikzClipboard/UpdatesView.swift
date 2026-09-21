import SwiftUI
import ClipCore

struct UpdatesPane: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let updates = model.updates
        Form {
            Section {
                LabeledContent("Current version") {
                    Text(model.appVersion).monospacedDigit().foregroundStyle(.secondary)
                }
                Toggle("Automatically install updates", isOn: Binding(
                    get: { model.settings.autoInstallUpdates },
                    set: {
                        model.settings.autoInstallUpdates = $0
                        updates.autoInstallChanged()
                    }
                ))
                LabeledContent("Status") {
                    UpdateStatusLabel(status: updates.status)
                }
                LabeledContent("Last checked") {
                    Text(lastChecked).foregroundStyle(.secondary)
                }
                if case .failed(let message) = updates.status {
                    Text(message)
                        .font(.footnote)
                        .foregroundStyle(.red)
                        .fixedSize(horizontal: false, vertical: true)
                        .textSelection(.enabled)
                }
                HStack {
                    Spacer()
                    if let v = updates.readyVersion {
                        Button("Restart to Update (\(v))") { updates.installNow() }
                            .buttonStyle(.borderedProminent)
                    }
                    Button("Check for Updates") { updates.checkNow() }
                        .disabled(updates.isBusy)
                }
            } footer: {
                Text("Updates come from your server and are verified with the release signature and the app's code signature before they are installed. Automatic installs happen while Yikz Clipboard is idle.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            if let latest = updates.latest {
                Section("What's New in \(latest.version)") {
                    if latest.notesMd.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        Text("No release notes.").foregroundStyle(.secondary)
                    } else {
                        ReleaseNotesView(markdown: latest.notesMd)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 540, height: 520)
    }

    private var lastChecked: String {
        guard let d = model.settings.lastUpdateCheck else { return "Never" }
        return d.formatted(date: .abbreviated, time: .shortened)
    }
}

struct UpdateStatusLabel: View {
    let status: UpdateStatus

    var body: some View {
        HStack(spacing: 8) {
            switch status {
            case .idle:
                Text("Not checked yet").foregroundStyle(.secondary)
            case .checking:
                ProgressView().controlSize(.small)
                Text("Checking\u{2026}").foregroundStyle(.secondary)
            case .upToDate:
                StatusDot(color: .green)
                Text("Up to date").foregroundStyle(.secondary)
            case .downloading(let f):
                ProgressView(value: f).frame(width: 90)
                Text("Downloading \(Int(f * 100))%").monospacedDigit().foregroundStyle(.secondary)
            case .ready(let v):
                StatusDot(color: .blue)
                Text("Ready to install \(v)").foregroundStyle(.secondary)
            case .installing:
                ProgressView().controlSize(.small)
                Text("Installing\u{2026}").foregroundStyle(.secondary)
            case .failed:
                StatusDot(color: .red)
                Text("Error").foregroundStyle(.secondary)
            }
        }
    }
}

struct ReleaseNotesView: View {
    let markdown: String

    var body: some View {
        let blocks = ReleaseNotes.parse(markdown)
        VStack(alignment: .leading, spacing: 5) {
            ForEach(Array(blocks.enumerated()), id: \.offset) { index, block in
                switch block {
                case .heading(let t):
                    Text(ReleaseNotes.inline(t))
                        .font(.system(size: 12.5, weight: .semibold))
                        .padding(.top, index == 0 ? 0 : 6)
                case .bullet(let t):
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text("\u{2022}").foregroundStyle(.secondary)
                        Text(ReleaseNotes.inline(t))
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .font(.system(size: 12))
                case .paragraph(let t):
                    Text(ReleaseNotes.inline(t))
                        .font(.system(size: 12))
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .textSelection(.enabled)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
