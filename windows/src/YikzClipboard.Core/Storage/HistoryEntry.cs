using System.Globalization;
using System.Text;
using YikzClipboard.Core.Content;
using YikzClipboard.Core.Protocol;

namespace YikzClipboard.Core.Storage;

public enum MetaState
{
    Ok = 0,
    Unsupported = 1,
    Corrupt = 2,
}

public enum EntryKind
{
    Text,
    Link,
    Image,
    Files,
    Unknown,
}

public sealed class HistoryEntry
{
    public HistoryEntry(ItemHeader header, ItemMeta? meta, MetaState state)
    {
        Header = header;
        Meta = meta;
        MetaState = state;
    }

    public ItemHeader Header { get; }

    public ItemMeta? Meta { get; }

    public MetaState MetaState { get; }

    public string Id => Header.Id;

    public long Seq => Header.Seq;

    public bool Pinned => Header.Pinned;

    public bool IsInline => Header.ChunkCount == 0;

    public EntryKind Kind
    {
        get
        {
            if (MetaState != MetaState.Ok)
            {
                return EntryKind.Unknown;
            }
            return Header.Kind switch
            {
                ProtocolConstants.KindText => TextPreview.IsLink(Meta?.Preview) ? EntryKind.Link : EntryKind.Text,
                ProtocolConstants.KindImage => EntryKind.Image,
                ProtocolConstants.KindFiles => EntryKind.Files,
                _ => EntryKind.Unknown,
            };
        }
    }

    public string Title
    {
        get
        {
            if (MetaState == MetaState.Corrupt)
            {
                return "Unreadable item";
            }
            if (MetaState == MetaState.Unsupported || Meta == null)
            {
                return "Unsupported item";
            }
            switch (Header.Kind)
            {
                case ProtocolConstants.KindText:
                    {
                        var line = TextPreview.FirstLine(Meta.Preview);
                        return line.Length == 0 ? "Blank text" : line;
                    }
                case ProtocolConstants.KindImage:
                    return Meta.Image != null
                        ? string.Create(CultureInfo.InvariantCulture, $"Image ({Meta.Image.Width} x {Meta.Image.Height})")
                        : "Image";
                case ProtocolConstants.KindFiles:
                    {
                        var files = Meta.Files ?? new List<FileInfoEntry>();
                        if (files.Count == 0)
                        {
                            return "Files";
                        }
                        if (files.Count == 1)
                        {
                            return files[0].Name;
                        }
                        return files[0].Name + " and " + (files.Count - 1).ToString(CultureInfo.InvariantCulture) + " more";
                    }
                default:
                    return "Unknown item";
            }
        }
    }

    public string KindLabel => Kind switch
    {
        EntryKind.Text => "Text",
        EntryKind.Link => "Link",
        EntryKind.Image => "Image",
        EntryKind.Files => (Meta?.Files?.Count ?? 0) == 1 ? "File" : "Files",
        _ => "Unknown",
    };

    public string SearchText
    {
        get
        {
            var sb = new StringBuilder();
            if (Meta != null)
            {
                sb.Append(Meta.Preview);
                if (Meta.Files != null)
                {
                    foreach (var f in Meta.Files)
                    {
                        sb.Append('\n').Append(f.Name);
                    }
                }
                if (!string.IsNullOrEmpty(Meta.SourceApp))
                {
                    sb.Append('\n').Append(Meta.SourceApp);
                }
            }
            sb.Append('\n').Append(KindLabel);
            return sb.ToString().ToLowerInvariant();
        }
    }

    public HistoryEntry WithPinned(bool pinned)
    {
        var header = Header.Clone();
        header.Pinned = pinned;
        return new HistoryEntry(header, Meta, MetaState);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + (bytes == 1 ? " byte" : " bytes");
        }
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
