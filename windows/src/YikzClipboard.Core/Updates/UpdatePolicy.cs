using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace YikzClipboard.Core.Updates;

public enum UpdateAction
{
    NoRelease,
    UpToDate,
    Update,
    Invalid,
}

public sealed record UpdateDecision(UpdateAction Action, LatestRelease? Release, SemVer? Version, string Reason);

public static partial class UpdatePolicy
{
    public static readonly TimeSpan LaunchDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex FileNamePattern();

    public static string? PlatformKey(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "windows-x64",
        Architecture.Arm64 => "windows-arm64",
        _ => null,
    };

    public static string? CurrentPlatformKey() => PlatformKey(RuntimeInformation.ProcessArchitecture);

    public static bool IsSafeFileName(string? name) => name != null && name != "." && name != ".." && FileNamePattern().IsMatch(name);

    public static UpdateDecision Decide(SemVer current, LatestRelease? latest, string? platformKey)
    {
        if (latest == null)
        {
            return new UpdateDecision(UpdateAction.NoRelease, null, null, "no release is available for this platform");
        }
        if (!SemVer.TryParse(latest.Version, out var version))
        {
            return new UpdateDecision(UpdateAction.Invalid, latest, null, "the release version is invalid");
        }
        if (version <= current)
        {
            return new UpdateDecision(UpdateAction.UpToDate, latest, version, "up to date");
        }
        if (string.IsNullOrEmpty(platformKey))
        {
            return new UpdateDecision(UpdateAction.Invalid, latest, version, "this architecture is not supported");
        }
        var asset = latest.Asset;
        if (asset == null)
        {
            return new UpdateDecision(UpdateAction.Invalid, latest, version, "the release has no asset");
        }
        if (!string.Equals(asset.Platform, platformKey, StringComparison.Ordinal))
        {
            return new UpdateDecision(UpdateAction.Invalid, latest, version, "the asset is for " + asset.Platform + ", not " + platformKey);
        }
        if (!IsSafeFileName(asset.File))
        {
            return new UpdateDecision(UpdateAction.Invalid, latest, version, "the asset file name is invalid");
        }
        if (asset.Size <= 0)
        {
            return new UpdateDecision(UpdateAction.Invalid, latest, version, "the asset size is invalid");
        }
        if (!ReleaseSignature.IsValidSha256Hex(asset.Sha256))
        {
            return new UpdateDecision(UpdateAction.Invalid, latest, version, "the asset hash is invalid");
        }
        if (string.IsNullOrEmpty(asset.Signature))
        {
            return new UpdateDecision(UpdateAction.Invalid, latest, version, "the asset is not signed");
        }
        return new UpdateDecision(UpdateAction.Update, latest, version, "version " + version + " is available");
    }
}
