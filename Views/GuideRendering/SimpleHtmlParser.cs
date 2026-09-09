using System.Text;

namespace Foot_Tracker.Views.GuideRendering;

/// <summary>
/// Deliberately minimal HTML parser for our own guide files
/// (DataFiles/Guides/&lt;name&gt;/index.html) - not a general-purpose web HTML parser.
/// Only extracts &lt;body&gt; content; head/meta/link/script/style are skipped
/// entirely (guide.css's colors are replicated directly in GuideHtmlRenderer.cs
/// rather than parsed dynamically - a real CSS engine is out of scope).
/// </summary>
public static class SimpleHtmlParser
{
    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "img", "meta", "link", "hr", "input", "area", "base", "col", "embed", "param", "source", "track", "wbr"
    };

    // Tags whose content should never be treated as visible text (raw CSS/script bodies etc.)
    private static readonly HashSet<string> SkipContentTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "head", "script", "style", "title"
    };

    public static HtmlNode ParseBody(string html)
    {
        var root = new HtmlNode { TagName = "body" };

        int bodyStart = IndexOfTagStart(html, "body");
        int contentStart = bodyStart >= 0 ? html.IndexOf('>', bodyStart) + 1 : 0;

        int bodyEnd = html.IndexOf("</body", contentStart, StringComparison.OrdinalIgnoreCase);
        string content = bodyEnd >= 0
            ? html[contentStart..bodyEnd]
            : html[contentStart..];

        Parse(content, root);
        return root;
    }

    private static int IndexOfTagStart(string html, string tagName)
    {
        int index = html.IndexOf($"<{tagName}", StringComparison.OrdinalIgnoreCase);
        return index;
    }

    private static void Parse(string html, HtmlNode root)
    {
        var stack = new Stack<HtmlNode>();
        stack.Push(root);

        int i = 0;
        int length = html.Length;

        while (i < length)
        {
            if (html[i] != '<')
            {
                int textStart = i;
                int nextTag = html.IndexOf('<', i);
                int textEnd = nextTag < 0 ? length : nextTag;

                string rawText = html[textStart..textEnd];
                string decoded = DecodeEntities(rawText);

                if (!string.IsNullOrEmpty(decoded))
                {
                    stack.Peek().Children.Add(new HtmlNode { Text = decoded });
                }

                i = textEnd;
                continue;
            }

            // Comments: <!-- ... -->
            if (i + 3 < length && html[i + 1] == '!' && html[i + 2] == '-' && html[i + 3] == '-')
            {
                int end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? length : end + 3;
                continue;
            }

            // Doctype or other <! ... > declarations
            if (i + 1 < length && html[i + 1] == '!')
            {
                int end = html.IndexOf('>', i + 1);
                i = end < 0 ? length : end + 1;
                continue;
            }

            int tagEnd = html.IndexOf('>', i);
            if (tagEnd < 0)
                break;

            string rawTag = html[(i + 1)..tagEnd].Trim();
            i = tagEnd + 1;

            if (rawTag.Length == 0)
                continue;

            // Closing tag: </tagname>
            if (rawTag[0] == '/')
            {
                string closingName = rawTag[1..].Trim();

                // Pop until we find a matching open tag, lenient about mismatches
                // (hand-authored guide HTML may have quirks).
                if (stack.Any(n => n.TagName.Equals(closingName, StringComparison.OrdinalIgnoreCase)))
                {
                    while (stack.Count > 1 &&
                           !stack.Peek().TagName.Equals(closingName, StringComparison.OrdinalIgnoreCase))
                    {
                        stack.Pop();
                    }

                    if (stack.Count > 1)
                        stack.Pop();
                }

                continue;
            }

            bool selfClosing = rawTag.EndsWith("/", StringComparison.Ordinal);
            if (selfClosing)
                rawTag = rawTag[..^1].TrimEnd();

            (string tagName, Dictionary<string, string> attributes) = ParseTag(rawTag);

            var node = new HtmlNode { TagName = tagName };
            foreach (var kvp in attributes)
                node.Attributes[kvp.Key] = kvp.Value;

            stack.Peek().Children.Add(node);

            bool isVoid = VoidTags.Contains(tagName);

            if (SkipContentTags.Contains(tagName) && !isVoid)
            {
                string closeTag = $"</{tagName}";
                int closeIndex = html.IndexOf(closeTag, i, StringComparison.OrdinalIgnoreCase);
                int skipTo = closeIndex < 0 ? length : html.IndexOf('>', closeIndex) + 1;
                i = skipTo <= 0 ? length : skipTo;
                continue;
            }

            if (!selfClosing && !isVoid)
            {
                stack.Push(node);
            }
        }
    }

    private static (string TagName, Dictionary<string, string> Attributes) ParseTag(string rawTag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int nameEnd = 0;
        while (nameEnd < rawTag.Length && !char.IsWhiteSpace(rawTag[nameEnd]))
            nameEnd++;

        string tagName = rawTag[..nameEnd];
        int pos = nameEnd;

        while (pos < rawTag.Length)
        {
            while (pos < rawTag.Length && char.IsWhiteSpace(rawTag[pos]))
                pos++;

            if (pos >= rawTag.Length)
                break;

            int attrNameStart = pos;
            while (pos < rawTag.Length && rawTag[pos] != '=' && !char.IsWhiteSpace(rawTag[pos]))
                pos++;

            string attrName = rawTag[attrNameStart..pos];

            while (pos < rawTag.Length && char.IsWhiteSpace(rawTag[pos]))
                pos++;

            if (pos < rawTag.Length && rawTag[pos] == '=')
            {
                pos++;
                while (pos < rawTag.Length && char.IsWhiteSpace(rawTag[pos]))
                    pos++;

                string value;

                if (pos < rawTag.Length && (rawTag[pos] == '"' || rawTag[pos] == '\''))
                {
                    char quote = rawTag[pos];
                    pos++;
                    int valueStart = pos;
                    while (pos < rawTag.Length && rawTag[pos] != quote)
                        pos++;

                    value = rawTag[valueStart..Math.Min(pos, rawTag.Length)];
                    if (pos < rawTag.Length)
                        pos++;
                }
                else
                {
                    int valueStart = pos;
                    while (pos < rawTag.Length && !char.IsWhiteSpace(rawTag[pos]))
                        pos++;

                    value = rawTag[valueStart..pos];
                }

                if (!string.IsNullOrEmpty(attrName))
                    attributes[attrName] = DecodeEntities(value);
            }
            else if (!string.IsNullOrEmpty(attrName))
            {
                attributes[attrName] = string.Empty;
            }
        }

        return (tagName, attributes);
    }

    private static string DecodeEntities(string text)
    {
        if (!text.Contains('&'))
            return text;

        var sb = new StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                sb.Append(text[i]);
                continue;
            }

            // Look for a closing ';' within a short window (real entities are short).
            int searchEnd = Math.Min(text.Length, i + 11);
            int semicolon = -1;

            for (int j = i + 1; j < searchEnd; j++)
            {
                if (text[j] == ';')
                {
                    semicolon = j;
                    break;
                }
            }

            if (semicolon > i)
            {
                string entity = text[(i + 1)..semicolon];

                string? replacement = entity switch
                {
                    "amp" => "&",
                    "lt" => "<",
                    "gt" => ">",
                    "quot" => "\"",
                    "apos" => "'",
                    "nbsp" => " ",
                    _ when entity.StartsWith('#') && int.TryParse(entity.AsSpan(1), out int code) =>
                        char.ConvertFromUtf32(code),
                    _ => null
                };

                if (replacement is not null)
                {
                    sb.Append(replacement);
                    i = semicolon;
                    continue;
                }
            }

            sb.Append('&');
        }

        return sb.ToString();
    }
}
