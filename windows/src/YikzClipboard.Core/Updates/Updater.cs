using System.Diagnostics;
using YikzClipboard.Core.Logging;

namespace YikzClipboard.Core.Updates;

public enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    Downloading,
    Verifying,
    Ready,
    Installing,
    Failed,
}

public sealed record PreparedUpdate(SemVer Version, string VersionDir, string AppDir, LatestRelease Release);

public sealed record UpdateStatus(
    UpdateStage Stage,
    string Message,
    double Progress,
    LatestRelease? Latest,
    PreparedUpdate? Prepared,
    DateTimeOffset? LastChecked);

public sealed class Updater
{
    private readonly UpdateClient _client;
    private readonly string _updatesRoot;
    private readonly string? _platformKey;
    private readonly ILog _log;
    private readonly byte[] _publicKey;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private Task? _running;
    private UpdateStatus _status;

    public Updater(UpdateClient client, SemVer current, string? platformKey, string updatesRoot, ILog log, DateTimeOffset? lastChecked = null, byte[]? publicKey = null, Func<DateTimeOffset>? clock = null)
    {
        _client = client;
        Current = current;
        _platformKey = platformKey;
        _updatesRoot = updatesRoot;
        _log = log;
        _publicKey = publicKey ?? ReleaseSignature.ReleasePublicKey;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _status = new UpdateStatus(UpdateStage.Idle, "", 0, null, null, lastChecked);
    }

    public SemVer Current { get; }

    public string? PlatformKey => _platformKey;

    public string UpdatesRoot => _updatesRoot;

    public UpdateStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public event Action<UpdateStatus>? Changed;

    public event Action<DateTimeOffset>? Checked;

    public Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_running != null && !_running.IsCompleted)
            {
                return _running;
            }
            if (_status.Stage == UpdateStage.Installing)
            {
                return Task.CompletedTask;
            }
            _running = Task.Run(() => RunAsync(manual, ct), CancellationToken.None);
            return _running;
        }
    }

    private void Set(Func<UpdateStatus, UpdateStatus> change)
    {
        UpdateStatus snapshot;
        lock (_gate)
        {
            _status = change(_status);
            snapshot = _status;
        }
        try
        {
            Changed?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _log.Warn("update", "status listener failed", ex);
        }
    }

    private async Task RunAsync(bool manual, CancellationToken ct)
    {
        Set(s => s with { Stage = UpdateStage.Checking, Message = "Checking for updates...", Progress = 0 });
        try
        {
            if (_platformKey == null)
            {
                Set(s => s with { Stage = UpdateStage.Failed, Message = "Updates are not available for this architecture." });
                return;
            }
            var latest = await _client.GetLatestAsync(_platformKey, ct).ConfigureAwait(false);
            var now = _clock();
            try
            {
                Checked?.Invoke(now);
            }
            catch (Exception)
            {
            }
            var decision = UpdatePolicy.Decide(Current, latest, _platformKey);
            _log.Info("update", "check " + (manual ? "(manual) " : "") + "result: " + decision.Action + ", " + decision.Reason);
            switch (decision.Action)
            {
                case UpdateAction.NoRelease:
                case UpdateAction.UpToDate:
                    Set(s => s with { Stage = s.Prepared != null ? UpdateStage.Ready : UpdateStage.UpToDate, Message = s.Prepared != null ? ReadyMessage(s.Prepared) : "Yikz Clipboard is up to date.", Latest = latest ?? s.Latest, LastChecked = now, Progress = 0 });
                    return;
                case UpdateAction.Invalid:
                    _log.Warn("update", "ignoring release: " + decision.Reason);
                    Set(s => s with { Stage = UpdateStage.Failed, Message = "The available update is not valid for this PC: " + decision.Reason + ".", Latest = latest, LastChecked = now });
                    return;
            }
            var version = decision.Version!.Value;
            var existing = Status.Prepared;
            if (existing != null && existing.Version >= version && Directory.Exists(existing.AppDir))
            {
                Set(s => s with { Stage = UpdateStage.Ready, Message = ReadyMessage(existing), Latest = latest, LastChecked = now });
                return;
            }
            var prepared = await PrepareAsync(latest!, version, now, ct).ConfigureAwait(false);
            if (prepared != null)
            {
                Set(s => s with { Stage = UpdateStage.Ready, Message = ReadyMessage(prepared), Prepared = prepared, Latest = latest, LastChecked = now, Progress = 1 });
            }
        }
        catch (OperationCanceledException)
        {
            Set(s => s with { Stage = s.Prepared != null ? UpdateStage.Ready : UpdateStage.Idle, Message = "", Progress = 0 });
        }
        catch (Exception ex)
        {
            _log.Warn("update", "update check failed", ex);
            Set(s => s with { Stage = UpdateStage.Failed, Message = ex is UpdateException ? Sentence(ex.Message) : "The update check failed: " + ex.Message, Progress = 0 });
        }
    }

    private async Task<PreparedUpdate?> PrepareAsync(LatestRelease latest, SemVer version, DateTimeOffset now, CancellationToken ct)
    {
        var asset = latest.Asset!;
        var versionDir = Path.Combine(_updatesRoot, version.ToString());
        Directory.CreateDirectory(versionDir);
        var zipPath = Path.Combine(versionDir, asset.File);
        Set(s => s with { Stage = UpdateStage.Downloading, Message = "Downloading version " + version + "...", Progress = 0, Latest = latest, LastChecked = now });
        await _client.DownloadAsync(asset, version.ToString(), zipPath, new SyncProgress(p => Set(s => s.Stage == UpdateStage.Downloading ? s with { Progress = p } : s)), ct).ConfigureAwait(false);
        Set(s => s with { Stage = UpdateStage.Verifying, Message = "Verifying version " + version + "...", Progress = 1 });
        var result = ReleaseSignature.VerifyFile(zipPath, asset.Size, asset.Sha256, asset.Signature, _publicKey);
        if (result != VerificationResult.Valid)
        {
            TryDeleteFile(zipPath);
            _log.Error("update", "verification of " + asset.File + " failed: " + result);
            Set(s => s with { Stage = UpdateStage.Failed, Message = "The downloaded update failed verification (" + result + ") and was deleted.", Progress = 0 });
            return null;
        }
        _log.Info("update", "verified " + asset.File + " (" + asset.Size + " bytes)");
        var appDir = await Task.Run(() => UpdatePackage.Extract(zipPath, Path.Combine(versionDir, "app")), ct).ConfigureAwait(false);
        TryDeleteFile(zipPath);
        _log.Info("update", "version " + version + " is ready to install");
        return new PreparedUpdate(version, versionDir, appDir, latest);
    }

    public bool StartInstall(int processId, string exePath, out string? error)
    {
        error = null;
        var prepared = Status.Prepared;
        if (prepared == null)
        {
            error = "No update is ready.";
            return false;
        }
        var installDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(installDir))
        {
            error = "The install folder is unknown.";
            return false;
        }
        if (!UpdatePackage.IsDirectoryWritable(installDir))
        {
            error = "The install folder " + installDir + " is not writable. Move Yikz Clipboard to a folder you own, or install the update manually.";
            _log.Warn("update", "install folder is not writable: " + installDir);
            return false;
        }
        try
        {
            var scriptPath = Path.Combine(prepared.VersionDir, "install.ps1");
            var script = UpdateScript.Build(new UpdateScriptOptions(
                processId,
                prepared.AppDir,
                installDir,
                exePath,
                Path.Combine(prepared.VersionDir, "backup"),
                Path.Combine(prepared.VersionDir, "install.log"),
                prepared.Version.ToString()));
            File.WriteAllText(scriptPath, script, UpdateScript.FileEncoding);
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell))
            {
                powershell = "powershell.exe";
            }
            var psi = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = prepared.VersionDir,
            };
            using var process = Process.Start(psi);
            if (process == null)
            {
                error = "The update helper could not be started.";
                return false;
            }
            _log.Info("update", "installing version " + prepared.Version + " with helper process " + process.Id);
            Set(s => s with { Stage = UpdateStage.Installing, Message = "Installing version " + prepared.Version + "..." });
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("update", "could not start the update helper", ex);
            error = "The update helper could not be started: " + ex.Message;
            return false;
        }
    }

    private static string ReadyMessage(PreparedUpdate prepared) => "Version " + prepared.Version + " is ready to install.";

    private static string Sentence(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }
        var text = char.ToUpperInvariant(message[0]) + message[1..];
        return text.EndsWith('.') ? text : text + ".";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _report;

        public SyncProgress(Action<double> report)
        {
            _report = report;
        }

        public void Report(double value) => _report(value);
    }
}
