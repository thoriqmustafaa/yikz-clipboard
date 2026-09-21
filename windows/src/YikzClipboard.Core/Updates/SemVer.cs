using System.Globalization;
using System.Reflection;

namespace YikzClipboard.Core.Updates;

public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    public static SemVer Zero => new(0, 0, 0);

    public static bool TryParse(string? text, out SemVer version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }
        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            s = s[..cut];
        }
        var parts = s.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            var p = parts[i];
            if (p.Length == 0 || p.Length > 9 || !p.All(char.IsAsciiDigit))
            {
                return false;
            }
            numbers[i] = int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        version = new SemVer(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public static SemVer Parse(string text) => TryParse(text, out var v) ? v : throw new FormatException("invalid version: " + text);

    public static SemVer FromVersion(Version? version)
    {
        if (version == null)
        {
            return Zero;
        }
        return new SemVer(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build));
    }

    public static SemVer FromAssembly(Assembly assembly) => FromVersion(assembly.GetName().Version);

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;

    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
