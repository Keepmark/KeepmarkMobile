using System.Globalization;
using System.Net;
using System.Text;
using Keepmark.Core.Crypto;
using Keepmark.Core.I18n;
using Keepmark.Core.Models;
using Keepmark.Core.Sync;

namespace Keepmark.Core.Html;

/// <summary>Result of reading a bookmarks.html file: the tree without ids
/// (the store assigns them on insert) and what was found.</summary>
public sealed record NetscapeParseResult(
    List<BookmarkNode> Roots, int Folders, int Bookmarks, int Separators, int Skipped);

/// <summary>What an import would add once duplicates are handled.</summary>
public sealed record NetscapeImportPlan(
    List<BookmarkNode> Roots, int Folders, int Bookmarks, int Separators, int Duplicates);

/// <summary>
/// Netscape bookmark file (bookmarks.html), the format every browser imports
/// and exports. Plain text on purpose: the export is NOT encrypted, the UI
/// says so before writing it. Mapping:
/// folder = DT/H3 + DL, bookmark = DT/A (HREF, ADD_DATE in seconds, TAGS
/// comma-separated) + optional DD description, separator = HR. The synced
/// [xbs] Toolbar / Other containers are written as the H3 folders flagged
/// PERSONAL_TOOLBAR_FOLDER / UNFILED_BOOKMARKS_FOLDER; [xbs] Menu children
/// sit at the top level (Firefox layout). Icons, keywords, visit dates and
/// feeds are ignored on import. The parser is a tolerant tokenizer (the
/// format is not well-formed HTML), bounded like encrypted backups.
/// </summary>
public static class NetscapeBookmarks
{
    /// <summary>Largest HTML file read (same bound as encrypted backups).</summary>
    public const long MaxBytes = BackupGuard.MaxBytes;

    /// <summary>Deepest folder nesting accepted.</summary>
    public const int MaxDepth = 1000;

    public const string FileSuffix = ".html";

    private const string Doctype = "NETSCAPE-Bookmark-file-1";

    public static bool HasSupportedExtension(string fileName) =>
        fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    /// <summary>`keepmark-&lt;sync&gt;-YYYYMMDD-HHmmss.html` (device local time),
    /// slugged like the encrypted export.</summary>
    public static string ExportFileName(string? syncName, DateTime localNow)
    {
        var slug = BackupGuard.Slug(syncName);
        return slug.Length == 0
            ? $"keepmark-{localNow:yyyyMMdd-HHmmss}{FileSuffix}"
            : $"keepmark-{slug}-{localNow:yyyyMMdd-HHmmss}{FileSuffix}";
    }

    // ---- export -------------------------------------------------------------

    /// <summary>Writes the tree as a Netscape bookmark file. Every text and
    /// attribute is HTML-escaped: the file may be opened in a browser.</summary>
    public static string Write(IReadOnlyList<BookmarkNode> roots)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE ").Append(Doctype).Append(">\n")
          .Append("<!-- This is an automatically generated file.\n     It will be read and overwritten.\n     DO NOT EDIT! -->\n")
          .Append("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n")
          .Append("<TITLE>Bookmarks</TITLE>\n<H1>Bookmarks</H1>\n<DL><p>\n");

        var menu = roots.FirstOrDefault(r => r.Title == ContainerNames.Menu && r.IsFolder);
        var toolbar = roots.FirstOrDefault(r => r.Title == ContainerNames.Toolbar && r.IsFolder);
        var other = roots.FirstOrDefault(r => r.Title == ContainerNames.Other && r.IsFolder);

        if (menu?.Children is not null)
        {
            WriteChildren(sb, menu.Children, 1);
        }

        if (toolbar is not null)
        {
            WriteFolder(sb, toolbar, ContainerNames.Display(toolbar.Title, true), "PERSONAL_TOOLBAR_FOLDER", 1);
        }

        if (other is not null)
        {
            WriteFolder(sb, other, ContainerNames.Display(other.Title, true), "UNFILED_BOOKMARKS_FOLDER", 1);
        }

        foreach (var root in roots)
        {
            if (ReferenceEquals(root, menu) || ReferenceEquals(root, toolbar) || ReferenceEquals(root, other))
            {
                continue;
            }

            WriteNode(sb, root, 1);
        }

        sb.Append("</DL><p>\n");
        return sb.ToString();
    }

    private static void WriteChildren(StringBuilder sb, List<BookmarkNode> children, int depth)
    {
        foreach (var child in children)
        {
            WriteNode(sb, child, depth);
        }
    }

    private static void WriteNode(StringBuilder sb, BookmarkNode node, int depth)
    {
        if (node.IsFolder)
        {
            WriteFolder(sb, node, node.Title, null, depth);
            return;
        }

        Indent(sb, depth);
        if (node.IsSeparator)
        {
            sb.Append("<HR>\n");
            return;
        }

        sb.Append("<DT><A HREF=\"").Append(Escape(node.Url ?? string.Empty)).Append('"');
        AppendDate(sb, node.DateAdded);
        var tags = node.Tags?
            .Select(t => t.Replace(',', ' ').Trim())
            .Where(t => t.Length > 0)
            .ToList();
        if (tags is { Count: > 0 })
        {
            sb.Append(" TAGS=\"").Append(Escape(string.Join(",", tags))).Append('"');
        }

        sb.Append('>').Append(Escape(node.Title ?? node.Url ?? string.Empty)).Append("</A>\n");
        if (!string.IsNullOrWhiteSpace(node.Description))
        {
            Indent(sb, depth);
            sb.Append("<DD>").Append(Escape(OneLine(node.Description))).Append('\n');
        }
    }

    private static void WriteFolder(StringBuilder sb, BookmarkNode folder, string? title, string? flag, int depth)
    {
        Indent(sb, depth);
        sb.Append("<DT><H3");
        AppendDate(sb, folder.DateAdded);
        if (flag is not null)
        {
            sb.Append(' ').Append(flag).Append("=\"true\"");
        }

        sb.Append('>').Append(Escape(title ?? string.Empty)).Append("</H3>\n");
        Indent(sb, depth);
        sb.Append("<DL><p>\n");
        WriteChildren(sb, folder.Children ?? new List<BookmarkNode>(), depth + 1);
        Indent(sb, depth);
        sb.Append("</DL><p>\n");
    }

    private static void AppendDate(StringBuilder sb, long? dateAddedMs)
    {
        if (dateAddedMs is > 0)
        {
            sb.Append(" ADD_DATE=\"").Append((dateAddedMs.Value / 1000).ToString(CultureInfo.InvariantCulture)).Append('"');
        }
    }

    private static void Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 4);

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    private static string OneLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ---- import -------------------------------------------------------------

    /// <summary>Reads a bookmarks.html text. Throws (localized) when the text is
    /// not a bookmarks file, nests too deeply or holds too many items.</summary>
    public static NetscapeParseResult Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var roots = new List<BookmarkNode>();
        var stack = new List<List<BookmarkNode>> { roots };
        BookmarkNode? pendingFolder = null;
        BookmarkNode? lastLink = null;
        BookmarkNode? descriptionTarget = null;
        int folders = 0, bookmarks = 0, separators = 0, skipped = 0, nodes = 0;
        var sawStructure = false;
        int i = 0, n = html.Length;

        void Count()
        {
            if (++nodes > BackupGuard.MaxNodes)
            {
                throw new InvalidOperationException(
                    Loc.Get(Str.ErrBackupTooManyItems, BackupGuard.MaxNodes.ToString("N0")));
            }
        }

        while (i < n)
        {
            int lt = html.IndexOf('<', i);
            if (descriptionTarget is not null)
            {
                var text = Clean(WebUtility.HtmlDecode(html[i..(lt < 0 ? n : lt)]));
                if (text.Length > 0)
                {
                    descriptionTarget.Description = text.Length > XbsConstants.DescriptionMaxLength
                        ? text[..XbsConstants.DescriptionMaxLength].TrimEnd()
                        : text;
                }

                descriptionTarget = null;
            }

            if (lt < 0)
            {
                break;
            }

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                int endComment = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = endComment < 0 ? n : endComment + 3;
                continue;
            }

            int gt = TagEnd(html, lt + 1);
            if (gt < 0)
            {
                break;
            }

            var (name, closing, attrs) = ReadTag(html, lt + 1, gt);
            i = gt + 1;
            switch (name)
            {
                case "DL":
                    sawStructure = true;
                    lastLink = null;
                    if (closing)
                    {
                        if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                    }
                    else
                    {
                        if (stack.Count >= MaxDepth)
                        {
                            throw new InvalidOperationException(Loc.Get(Str.ErrHtmlTooDeep));
                        }

                        stack.Add(pendingFolder?.Children ?? stack[^1]);
                        pendingFolder = null;
                    }

                    break;
                case "H3" when !closing:
                {
                    var (text, next) = InnerText(html, i, "</h3");
                    i = next;
                    Count();
                    var folder = new BookmarkNode
                    {
                        Title = Clean(text),
                        DateAdded = ParseDate(attrs.GetValueOrDefault("ADD_DATE")),
                        Children = new List<BookmarkNode>(),
                    };
                    stack[^1].Add(folder);
                    folders++;
                    pendingFolder = folder;
                    lastLink = null;
                    break;
                }

                case "A" when !closing:
                {
                    var (text, next) = InnerText(html, i, "</a");
                    i = next;
                    lastLink = null;
                    var url = Clean(WebUtility.HtmlDecode(attrs.GetValueOrDefault("HREF") ?? string.Empty));
                    if (!IsWebUrl(url))
                    {
                        skipped++;
                        break;
                    }

                    Count();
                    var title = Clean(text);
                    var link = new BookmarkNode
                    {
                        Title = title.Length > 0 ? title : url,
                        Url = url,
                        DateAdded = ParseDate(attrs.GetValueOrDefault("ADD_DATE")),
                        Tags = ParseTags(attrs.GetValueOrDefault("TAGS")),
                    };
                    stack[^1].Add(link);
                    bookmarks++;
                    lastLink = link;
                    break;
                }

                case "DD" when !closing:
                    descriptionTarget = lastLink;
                    lastLink = null;
                    break;
                case "HR" when !closing:
                    Count();
                    stack[^1].Add(new BookmarkNode { Url = XbsConstants.SeparatorUrl });
                    separators++;
                    lastLink = null;
                    break;
                case "DT":
                    lastLink = null;
                    break;
            }
        }

        if (!sawStructure && nodes == 0 && html.IndexOf(Doctype, StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(Loc.Get(Str.ErrHtmlNotBookmarks));
        }

        return new NetscapeParseResult(roots, folders, bookmarks, separators, skipped);
    }

    /// <summary>What an import adds. Duplicates (normalized URL already in
    /// <paramref name="existing"/>, or earlier in the same file) are always
    /// counted and dropped only when <paramref name="skipDuplicates"/> is set;
    /// a folder left empty by that drop is dropped too. The input is not
    /// changed: the plan holds copies.</summary>
    public static NetscapeImportPlan Plan(NetscapeParseResult parsed, IEnumerable<BookmarkNode> existing, bool skipDuplicates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<BookmarkNode>(existing);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.Children is not null)
            {
                foreach (var c in node.Children) queue.Enqueue(c);
            }
            else if (!string.IsNullOrEmpty(node.Url) && !node.IsSeparator)
            {
                seen.Add(BookmarkMerge.NormalizeUrl(node.Url));
            }
        }

        int folders = 0, bookmarks = 0, separators = 0, duplicates = 0;

        List<BookmarkNode> CopyList(List<BookmarkNode> source)
        {
            var output = new List<BookmarkNode>(source.Count);
            foreach (var node in source)
            {
                if (node.IsFolder)
                {
                    var children = CopyList(node.Children!);
                    if (children.Count == 0 && node.Children!.Count > 0)
                    {
                        continue; // emptied by skipped duplicates
                    }

                    output.Add(new BookmarkNode { Title = node.Title, DateAdded = node.DateAdded, Children = children });
                    folders++;
                }
                else if (node.IsSeparator)
                {
                    output.Add(new BookmarkNode { Url = node.Url });
                    separators++;
                }
                else
                {
                    var duplicate = !seen.Add(BookmarkMerge.NormalizeUrl(node.Url!));
                    if (duplicate)
                    {
                        duplicates++;
                        if (skipDuplicates) continue;
                    }

                    output.Add(new BookmarkNode
                    {
                        Title = node.Title,
                        Url = node.Url,
                        Description = node.Description,
                        Tags = node.Tags is null ? null : new List<string>(node.Tags),
                        DateAdded = node.DateAdded,
                    });
                    bookmarks++;
                }
            }

            return output;
        }

        var roots = CopyList(parsed.Roots);
        return new NetscapeImportPlan(roots, folders, bookmarks, separators, duplicates);
    }

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>ADD_DATE is Unix seconds in the format; some exporters write
    /// milliseconds or microseconds. Anything unusable is null.</summary>
    internal static long? ParseDate(string? value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var raw) || raw <= 0)
        {
            return null;
        }

        return raw switch
        {
            > 100_000_000_000_000 => raw / 1000,  // microseconds
            > 100_000_000_000 => raw,             // milliseconds
            _ => raw * 1000,                      // seconds
        };
    }

    private static List<string>? ParseTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var tags = WebUtility.HtmlDecode(value)
            .Split(',')
            .Select(t => Clean(t))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return tags.Count == 0 ? null : tags;
    }

    /// <summary>Drops control characters (U+001F joins tags in the store) and
    /// collapses whitespace runs into one space.</summary>
    private static string Clean(string value)
    {
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>Text up to the closing tag (case-insensitive), decoded and with
    /// any markup inside dropped; returns the index after the closing '>'.</summary>
    private static (string Text, int Next) InnerText(string html, int start, string closeTag)
    {
        int end = html.IndexOf(closeTag, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            int lt = html.IndexOf('<', start);
            end = lt < 0 ? html.Length : lt;
            return (WebUtility.HtmlDecode(html[start..end]), end);
        }

        var raw = html[start..end];
        if (raw.Contains('<'))
        {
            var sb = new StringBuilder(raw.Length);
            var inTag = false;
            foreach (var ch in raw)
            {
                if (ch == '<') inTag = true;
                else if (ch == '>') inTag = false;
                else if (!inTag) sb.Append(ch);
            }

            raw = sb.ToString();
        }

        int gt = html.IndexOf('>', end);
        return (WebUtility.HtmlDecode(raw), gt < 0 ? html.Length : gt + 1);
    }

    /// <summary>Index of the '&gt;' that ends a tag, skipping quoted values.</summary>
    private static int TagEnd(string html, int start)
    {
        char quote = '\0';
        for (int i = start; i < html.Length; i++)
        {
            var ch = html[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static (string Name, bool Closing, Dictionary<string, string> Attrs) ReadTag(string html, int start, int end)
    {
        int i = start;
        var closing = false;
        if (i < end && html[i] == '/')
        {
            closing = true;
            i++;
        }

        int nameStart = i;
        while (i < end && char.IsAsciiLetterOrDigit(html[i])) i++;
        var name = html[nameStart..i].ToUpperInvariant();
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (closing || name is not ("A" or "H3"))
        {
            return (name, closing, attrs);
        }

        while (i < end)
        {
            while (i < end && (char.IsWhiteSpace(html[i]) || html[i] == '/')) i++;
            int keyStart = i;
            while (i < end && !char.IsWhiteSpace(html[i]) && html[i] is not '=' and not '/') i++;
            var key = html[keyStart..i];
            while (i < end && char.IsWhiteSpace(html[i])) i++;
            var value = string.Empty;
            if (i < end && html[i] == '=')
            {
                i++;
                while (i < end && char.IsWhiteSpace(html[i])) i++;
                if (i < end && html[i] is '"' or '\'')
                {
                    var q = html[i++];
                    int valueStart = i;
                    while (i < end && html[i] != q) i++;
                    value = html[valueStart..i];
                    if (i < end) i++;
                }
                else
                {
                    int valueStart = i;
                    while (i < end && !char.IsWhiteSpace(html[i])) i++;
                    value = html[valueStart..i];
                }
            }

            if (key.Length > 0)
            {
                attrs.TryAdd(key, value);
            }
            else if (i == keyStart)
            {
                i++;
            }
        }

        return (name, closing, attrs);
    }
}
