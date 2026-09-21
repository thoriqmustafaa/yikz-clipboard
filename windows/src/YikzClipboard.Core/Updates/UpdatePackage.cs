using System.IO.Compression;

namespace YikzClipboard.Core.Updates;

public static class UpdatePackage
{
    public const string ExeName = "YikzClipboard.exe";

    public static string Extract(string zipPath, string destination)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, true);
        }
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.Length == 0)
                {
                    continue;
                }
                var full = Path.GetFullPath(Path.Combine(root, name));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateException("the update package contains an unsafe path");
                }
                if (name.EndsWith('/'))
                {
                    Directory.CreateDirectory(full);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                entry.ExtractToFile(full, true);
            }
        }
        return LocateAppRoot(destination) ?? throw new UpdateException("the update package does not contain " + ExeName);
    }

    public static string? LocateAppRoot(string directory)
    {
        if (File.Exists(Path.Combine(directory, ExeName)))
        {
            return directory;
        }
        var subdirs = Directory.GetDirectories(directory);
        if (subdirs.Length == 1 && Directory.GetFiles(directory).Length == 0 && File.Exists(Path.Combine(subdirs[0], ExeName)))
        {
            return subdirs[0];
        }
        return null;
    }

    public static bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".yikz-update-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void CleanupOldVersions(string updatesRoot, SemVer current)
    {
        try
        {
            if (!Directory.Exists(updatesRoot))
            {
                return;
            }
            foreach (var dir in Directory.GetDirectories(updatesRoot))
            {
                var name = Path.GetFileName(dir);
                if (!SemVer.TryParse(name, out var v) || v < current)
                {
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
        catch (Exception)
        {
        }
    }
}
