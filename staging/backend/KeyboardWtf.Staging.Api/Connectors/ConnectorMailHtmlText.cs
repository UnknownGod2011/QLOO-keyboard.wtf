using System.Net;
using System.Text;

namespace KeyboardWtf.Staging.Api.Connectors;

// Readable text for an HTML-only Gmail MIME body, before the existing result
// budget. This is not a browser or CSS renderer: no attributes, links, scripts,
// stylesheets or external assets are executed or fetched.
internal static class ConnectorMailHtmlText
{
    internal const int MaximumInputCharacters = 150000;
    private const int MaximumTags = 8192;
    private const int MaximumHiddenDepth = 32;

    internal static string Extract(string source, out bool partial)
    {
        partial = source.Length > MaximumInputCharacters;
        var html = source.Length > MaximumInputCharacters ? source[..MaximumInputCharacters] : source;
        var text = new StringBuilder(Math.Min(html.Length, 16000));
        var position = 0;
        var tags = 0;
        while (position < html.Length)
        {
            if (html[position] != '<') { text.Append(html[position++]); continue; }
            if (!ReadTag(html, position, out var tag)) { partial = true; break; }
            position = tag.End;
            if (++tags > MaximumTags) { partial = true; break; }
            if (tag.Name.Length == 0) continue; // Comment/declaration.
            if (!tag.Closing && Hidden(tag.Name))
            {
                if (!SkipHidden(html, ref position, tag.Name, ref tags)) { partial = true; break; }
                text.Append('\n');
            }
            else if (Block(tag.Name)) text.Append('\n');
        }
        // Decode after removing markup: escaped markup remains ordinary message
        // text, never a second parser pass or a source of action authority.
        var decoded = WebUtility.HtmlDecode(text.ToString());
        var result = new StringBuilder(decoded.Length);
        var space = false;
        var line = false;
        foreach (var character in decoded)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                line |= character is '\r' or '\n';
                space = true;
                continue;
            }
            if (space && result.Length > 0) result.Append(line ? '\n' : ' ');
            result.Append(character);
            space = false; line = false;
        }
        return result.ToString();
    }

    private readonly record struct Tag(string Name, bool Closing, bool SelfClosing, int End);

    private static bool ReadTag(string html, int start, out Tag tag)
    {
        tag = default;
        if (html.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
        {
            var end = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0) return false;
            tag = new("", false, false, end + 3); return true;
        }
        var position = start + 1;
        var closing = position < html.Length && html[position] == '/';
        if (closing) position++;
        var nameStart = position;
        while (position < html.Length && (char.IsAsciiLetterOrDigit(html[position]) || html[position] is '-' or ':')) position++;
        var name = html[nameStart..position].ToLowerInvariant();
        if (name.Length == 0 && (position >= html.Length || html[position] is not ('!' or '?'))) return false;
        var quote = '\0';
        for (; position < html.Length; position++)
        {
            var value = html[position];
            if (quote != '\0') { if (value == quote) quote = '\0'; continue; }
            if (value is '\'' or '"') { quote = value; continue; }
            if (value != '>') continue;
            var last = position - 1;
            while (last > start && char.IsWhiteSpace(html[last])) last--;
            tag = new(name, closing, html[last] == '/', position + 1); return true;
        }
        return false;
    }

    private static bool SkipHidden(string html, ref int position, string name, ref int tags, int hiddenDepth = 1)
    {
        if (hiddenDepth > MaximumHiddenDepth) return false;
        if (name is "script" or "style" or "noscript")
        {
            // Raw-text bodies may contain '<' inside code/CSS. Seek only the
            // matching closing tag, with a real name boundary.
            var marker = "</" + name;
            while (position < html.Length)
            {
                var closing = html.IndexOf(marker, position, StringComparison.OrdinalIgnoreCase);
                if (closing < 0) return false;
                var afterName = closing + marker.Length;
                if (afterName < html.Length && !char.IsWhiteSpace(html[afterName]) && html[afterName] is not ('>' or '/'))
                { position = afterName; continue; }
                if (!ReadTag(html, closing, out var tag)) return false;
                position = tag.End;
                return ++tags <= MaximumTags;
            }
            return false;
        }
        // Head/template can contain other hidden blocks and nested templates.
        var depth = 1;
        while (position < html.Length)
        {
            var start = html.IndexOf('<', position);
            if (start < 0 || !ReadTag(html, start, out var tag)) return false;
            position = tag.End;
            if (++tags > MaximumTags) return false;
            if (tag.Name == name)
            {
                if (tag.Closing && --depth == 0) return true;
                if (!tag.Closing && ++depth > MaximumHiddenDepth) return false;
            }
            else if (!tag.Closing && Hidden(tag.Name)
                && !SkipHidden(html, ref position, tag.Name, ref tags, hiddenDepth + 1)) return false;
        }
        return false;
    }

    private static bool Hidden(string name) => name is "head" or "style" or "script" or "template" or "noscript";
    private static bool Block(string name) => name is "p" or "div" or "br" or "hr" or "li" or "ul" or "ol"
        or "table" or "tr" or "td" or "th" or "blockquote" or "pre" or "section" or "article"
        or "header" or "footer" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "dl" or "dt" or "dd";
}
