using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using YikzClipboard.App.Interop;

namespace YikzClipboard.App.Platform;

internal enum ClipboardSnapshotKind
{
    None,
    Sensitive,
    OwnWrite,
    Text,
    Image,
    Files,
}

internal sealed class ClipboardSnapshot
{
    public ClipboardSnapshotKind Kind { get; init; }
    public uint Sequence { get; init; }
    public string? Text { get; init; }
    public byte[]? Png { get; init; }
    public byte[]? Dib { get; init; }
    public IReadOnlyList<string>? Files { get; init; }
    public IntPtr OwnerWindow { get; init; }
    public string? Reason { get; init; }
}

internal static class ClipboardAccess
{
    public static readonly uint FormatPng = Win32.RegisterClipboardFormat("PNG");
    public static readonly uint FormatExclude = Win32.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    public static readonly uint FormatCanIncludeInHistory = Win32.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    public static readonly uint FormatCanUploadToCloud = Win32.RegisterClipboardFormat("CanUploadToCloudClipboard");
    public static readonly uint FormatRtf = Win32.RegisterClipboardFormat("Rich Text Format");
    public static readonly uint FormatOrigin = Win32.RegisterClipboardFormat("YikzClipboardOrigin");
    public static readonly uint FormatPreferredDropEffect = Win32.RegisterClipboardFormat("Preferred DropEffect");

    public static bool TryOpen(IntPtr owner, int attempts = 12)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (Win32.OpenClipboard(owner))
            {
                return true;
            }
            Thread.Sleep(15 + i * 10);
        }
        return false;
    }

    public static ClipboardSnapshot Read(IntPtr owner, bool honorOriginMarker = true)
    {
        var sequence = Win32.GetClipboardSequenceNumber();
        if (!TryOpen(owner))
        {
            return new ClipboardSnapshot { Kind = ClipboardSnapshotKind.None, Sequence = sequence, Reason = "clipboard busy" };
        }
        try
        {
            var ownerWindow = Win32.GetClipboardOwner();
            if (honorOriginMarker && Win32.IsClipboardFormatAvailable(FormatOrigin))
            {
                return new ClipboardSnapshot { Kind = ClipboardSnapshotKind.OwnWrite, Sequence = sequence };
            }
            if (Win32.IsClipboardFormatAvailable(FormatExclude))
            {
                return new ClipboardSnapshot { Kind = ClipboardSnapshotKind.Sensitive, Sequence = sequence, Reason = "ExcludeClipboardContentFromMonitorProcessing" };
            }
            if (IsDwordZero(FormatCanIncludeInHistory))
            {
                return new ClipboardSnapshot { Kind = ClipboardSnapshotKind.Sensitive, Sequence = sequence, Reason = "CanIncludeInClipboardHistory = 0" };
            }
            if (IsDwordZero(FormatCanUploadToCloud))
            {
                return new ClipboardSnapshot { Kind = ClipboardSnapshotKind.Sensitive, Sequence = sequence, Reason = "CanUploadToCloudClipboard = 0" };
            }
            if (Win32.IsClipboardFormatAvailable(Win32.CF_HDROP))
            {
                var files = ReadFileDrop();
                if (files.Count > 0)
                {
                    return new ClipboardSnapshot { Kind = ClipboardSnapshotKind.Files, Sequence = sequence, Files = files, OwnerWindow = ownerWindow };
                }
            }
            var hasText = Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT);
            var hasRtf = Win32.IsClipboardFormatAvailable(FormatRtf);
            if (!(hasText && hasRtf))
            {
                if (Win32.IsClipboardFormatAvailable(FormatPng))
                {
                    var png = Win32.ReadGlobal(Win32.GetClipboardData(FormatPng));
                    if (png != null && png.Length > 8)
                    {
                        return new ClipboardSnapshot { Kind = ClipboardSnapshotKind.Image, Sequence = sequence, Png = png, OwnerWindow = ownerWindow };
                    }
                }
                foreach (var format in new[] { Win32.CF_DIBV5, Win32.CF_DIB })
                {
                    if (Win32.IsClipboardFormatAvailable(format))
                    {
                        var dib = Win32.ReadGlobal(Win32.GetClipboardData(format));
                        if (dib != null && dib.Length > 40)
                        {
                            return new ClipboardSnapshot { Kind = ClipboardSnapshotKind.Image, Sequence = sequence, Dib = dib, OwnerWindow = ownerWindow };
                        }
                    }
                }
            }
            if (hasText)
            {
                var text = ReadUnicodeText();
                if (!string.IsNullOrEmpty(text))
                {
                    return new ClipboardSnapshot { Kind = ClipboardSnapshotKind.Text, Sequence = sequence, Text = text, OwnerWindow = ownerWindow };
                }
            }
            return new ClipboardSnapshot { Kind = ClipboardSnapshotKind.None, Sequence = sequence };
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    private static bool IsDwordZero(uint format)
    {
        if (!Win32.IsClipboardFormatAvailable(format))
        {
            return false;
        }
        var data = Win32.ReadGlobal(Win32.GetClipboardData(format));
        return data != null && data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 0;
    }

    private static string? ReadUnicodeText()
    {
        var bytes = Win32.ReadGlobal(Win32.GetClipboardData(Win32.CF_UNICODETEXT));
        if (bytes == null)
        {
            return null;
        }
        var chars = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, bytes.Length & ~1));
        var end = chars.IndexOf('\0');
        return new string(end >= 0 ? chars[..end] : chars);
    }

    private static List<string> ReadFileDrop()
    {
        var list = new List<string>();
        var handle = Win32.GetClipboardData(Win32.CF_HDROP);
        if (handle == IntPtr.Zero)
        {
            return list;
        }
        var count = Win32.DragQueryFile(handle, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < count && i < 5000; i++)
        {
            var length = Win32.DragQueryFile(handle, i, null, 0);
            if (length == 0)
            {
                continue;
            }
            var buffer = new char[length + 1];
            var copied = Win32.DragQueryFile(handle, i, buffer, (uint)buffer.Length);
            if (copied > 0)
            {
                list.Add(new string(buffer, 0, (int)copied));
            }
        }
        return list;
    }

    public static uint WriteText(IntPtr owner, string text, string itemId)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return Write(owner, itemId, new List<(uint, byte[])> { (Win32.CF_UNICODETEXT, bytes) });
    }

    public static uint WriteImage(IntPtr owner, byte[] png, byte[]? dib, string itemId)
    {
        var formats = new List<(uint, byte[])> { (FormatPng, png) };
        if (dib != null)
        {
            formats.Add((Win32.CF_DIB, dib));
        }
        return Write(owner, itemId, formats);
    }

    public static uint WriteFiles(IntPtr owner, IReadOnlyList<string> paths, string itemId)
    {
        using var ms = new MemoryStream();
        var header = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 1);
        ms.Write(header);
        foreach (var p in paths)
        {
            ms.Write(Encoding.Unicode.GetBytes(p));
            ms.Write(new byte[2]);
        }
        ms.Write(new byte[2]);
        var effect = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(effect, 1);
        return Write(owner, itemId, new List<(uint, byte[])> { (Win32.CF_HDROP, ms.ToArray()), (FormatPreferredDropEffect, effect) });
    }

    private static uint Write(IntPtr owner, string itemId, List<(uint Format, byte[] Data)> formats)
    {
        var marker = Encoding.Unicode.GetBytes(itemId + "\0");
        formats.Add((FormatOrigin, marker));
        if (!TryOpen(owner, 20))
        {
            throw new IOException("the clipboard is in use by another app");
        }
        try
        {
            if (!Win32.EmptyClipboard())
            {
                throw new IOException("could not empty the clipboard, error " + Marshal.GetLastWin32Error());
            }
            foreach (var (format, data) in formats)
            {
                var handle = Win32.AllocGlobal(data);
                if (handle == IntPtr.Zero)
                {
                    throw new OutOfMemoryException("GlobalAlloc failed");
                }
                if (Win32.SetClipboardData(format, handle) == IntPtr.Zero)
                {
                    Win32.GlobalFree(handle);
                    Debug.WriteLine("SetClipboardData failed for format " + format);
                }
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
        return Win32.GetClipboardSequenceNumber();
    }
}
