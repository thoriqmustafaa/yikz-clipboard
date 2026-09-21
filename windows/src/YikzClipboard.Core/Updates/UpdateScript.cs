using System.Globalization;
using System.Text;

namespace YikzClipboard.Core.Updates;

public sealed record UpdateScriptOptions(
    int ProcessId,
    string SourceDir,
    string InstallDir,
    string ExePath,
    string BackupDir,
    string LogPath,
    string Version);

public static class UpdateScript
{
    private static readonly char[] SingleQuotes = ['\'', '‘', '’', '‚', '‛'];

    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('\'');
        foreach (var c in value)
        {
            if (Array.IndexOf(SingleQuotes, c) >= 0)
            {
                sb.Append(c);
            }
            sb.Append(c);
        }
        sb.Append('\'');
        return sb.ToString();
    }

    public static Encoding FileEncoding => new UTF8Encoding(true);

    public static string Build(UpdateScriptOptions o)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append("\r\n");
        Line("$ErrorActionPreference = 'Stop'");
        Line("$ProgressPreference = 'SilentlyContinue'");
        Line("$waitPid = " + o.ProcessId.ToString(CultureInfo.InvariantCulture));
        Line("$source = " + Quote(TrimSeparators(o.SourceDir)));
        Line("$target = " + Quote(TrimSeparators(o.InstallDir)));
        Line("$exe = " + Quote(o.ExePath));
        Line("$backup = " + Quote(TrimSeparators(o.BackupDir)));
        Line("$log = " + Quote(o.LogPath));
        Line("$version = " + Quote(o.Version));
        Line("function Write-Log([string]$message) {");
        Line("  try { Add-Content -LiteralPath $log -Value ((Get-Date).ToString('o') + ' ' + $message) -Encoding UTF8 } catch { }");
        Line("}");
        Line("function Copy-WithRetry([string]$from, [string]$to) {");
        Line("  $dir = [System.IO.Path]::GetDirectoryName($to)");
        Line("  if ($dir) { [System.IO.Directory]::CreateDirectory($dir) | Out-Null }");
        Line("  for ($i = 1; $i -le 40; $i++) {");
        Line("    try { [System.IO.File]::Copy($from, $to, $true); return } catch { if ($i -eq 40) { throw }; Start-Sleep -Milliseconds 250 }");
        Line("  }");
        Line("}");
        Line("Write-Log ('installing version ' + $version)");
        Line("$deadline = (Get-Date).AddSeconds(60)");
        Line("while (Get-Process -Id $waitPid -ErrorAction SilentlyContinue) {");
        Line("  if ((Get-Date) -gt $deadline) {");
        Line("    Write-Log 'the app did not exit, stopping it'");
        Line("    try { Stop-Process -Id $waitPid -Force -ErrorAction Stop } catch { }");
        Line("    Start-Sleep -Seconds 2");
        Line("    break");
        Line("  }");
        Line("  Start-Sleep -Milliseconds 200");
        Line("}");
        Line("Start-Sleep -Milliseconds 500");
        Line("$sourceRoot = [System.IO.Path]::GetFullPath($source).TrimEnd('\\')");
        Line("$files = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force)");
        Line("$saved = New-Object 'System.Collections.Generic.List[string]'");
        Line("$created = New-Object 'System.Collections.Generic.List[string]'");
        Line("$ok = $false");
        Line("try {");
        Line("  if ($files.Count -eq 0) { throw 'the update package is empty' }");
        Line("  if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }");
        Line("  foreach ($f in $files) {");
        Line("    $rel = $f.FullName.Substring($sourceRoot.Length).TrimStart('\\')");
        Line("    $dst = [System.IO.Path]::Combine($target, $rel)");
        Line("    if ([System.IO.File]::Exists($dst)) {");
        Line("      Copy-WithRetry $dst ([System.IO.Path]::Combine($backup, $rel))");
        Line("      $saved.Add($rel)");
        Line("    } else {");
        Line("      $created.Add($dst)");
        Line("    }");
        Line("  }");
        Line("  Write-Log ('backed up ' + $saved.Count + ' files')");
        Line("  foreach ($f in $files) {");
        Line("    $rel = $f.FullName.Substring($sourceRoot.Length).TrimStart('\\')");
        Line("    Copy-WithRetry $f.FullName ([System.IO.Path]::Combine($target, $rel))");
        Line("  }");
        Line("  $ok = $true");
        Line("  Write-Log ('installed ' + $files.Count + ' files')");
        Line("} catch {");
        Line("  Write-Log ('install failed: ' + $_.Exception.Message + ', rolling back')");
        Line("  foreach ($rel in $saved) {");
        Line("    try { Copy-WithRetry ([System.IO.Path]::Combine($backup, $rel)) ([System.IO.Path]::Combine($target, $rel)) } catch { Write-Log ('rollback failed for ' + $rel + ': ' + $_.Exception.Message) }");
        Line("  }");
        Line("  foreach ($path in $created) {");
        Line("    try { if ([System.IO.File]::Exists($path)) { [System.IO.File]::Delete($path) } } catch { }");
        Line("  }");
        Line("  Write-Log 'rollback finished'");
        Line("}");
        Line("try {");
        Line("  if ($ok) { Start-Process -FilePath $exe -ArgumentList '--background', '--updated' -WorkingDirectory $target }");
        Line("  else { Start-Process -FilePath $exe -ArgumentList '--background', '--update-failed' -WorkingDirectory $target }");
        Line("  Write-Log 'restarted the app'");
        Line("} catch {");
        Line("  Write-Log ('restart failed: ' + $_.Exception.Message)");
        Line("}");
        return sb.ToString();
    }

    private static string TrimSeparators(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? path : trimmed;
    }
}
