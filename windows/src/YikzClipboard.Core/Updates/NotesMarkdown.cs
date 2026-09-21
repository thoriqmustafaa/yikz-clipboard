namespace YikzClipboard.Core.Updates;

public enum NotesBlockKind
{
    Heading,
    Bullet,
    Paragraph,
}

public enum NotesSpanKind
{
    Text,
    Bold,
    Code,
    Link,
}

public sealed record NotesSpan(NotesSpanKind Kind, string Text, string? Url = null);

public sealed record NotesBlock(NotesBlockKind Kind, IReadOnlyList<NotesSpan> Spans);

public static class NotesMarkdown
{
    public static IReadOnlyList<NotesBlock> Parse(string? markdown)
    {
        var blocks = new List<NotesBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return blocks;
        }
        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (trimmed.StartsWith('#'))
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#')
                {
                    level++;
                }
                if (level < trimmed.Length && trimmed[level] == ' ')
                {
                    blocks.Add(new NotesBlock(NotesBlockKind.Heading, ParseInline(trimmed[(level + 1)..].Trim())));
                    continue;
                }
            }
            if ((trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) && trimmed.Length > 2)
            {
                blocks.Add(new NotesBlock(NotesBlockKind.Bullet, ParseInline(trimmed[2..].Trim())));
                continue;
            }
            blocks.Add(new NotesBlock(NotesBlockKind.Paragraph, ParseInline(trimmed)));
        }
        return blocks;
    }

    public static IReadOnlyList<NotesSpan> ParseInline(string text)
    {
        var spans = new List<NotesSpan>();
        var plain = new System.Text.StringBuilder();
        void Flush()
        {
            if (plain.Length > 0)
            {
                spans.Add(new NotesSpan(NotesSpanKind.Text, plain.ToString()));
                plain.Clear();
            }
        }
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    spans.Add(new NotesSpan(NotesSpanKind.Bold, text[(i + 2)..end]));
                    i = end + 2;
                    continue;
                }
            }
            else if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    spans.Add(new NotesSpan(NotesSpanKind.Code, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '[')
            {
                var close = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                if (close > i + 1)
                {
                    var end = text.IndexOf(')', close + 2);
                    if (end > close + 2)
                    {
                        var label = text[(i + 1)..close];
                        var url = text[(close + 2)..end].Trim();
                        Flush();
                        if (IsSafeLink(url))
                        {
                            spans.Add(new NotesSpan(NotesSpanKind.Link, label, url));
                        }
                        else
                        {
                            spans.Add(new NotesSpan(NotesSpanKind.Text, label));
                        }
                        i = end + 1;
                        continue;
                    }
                }
            }
            plain.Append(c);
            i++;
        }
        Flush();
        return spans;
    }

    public static bool IsSafeLink(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }
}
