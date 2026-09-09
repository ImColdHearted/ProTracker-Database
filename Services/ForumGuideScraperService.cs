using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Foot_Tracker.Views.GuideRendering;

namespace Foot_Tracker.Services;

/// <summary>
/// §194. Turns a PRO forum topic into a draft guide page.
///
/// WHAT IT IS NOT. The obvious question was whether BossWikiScraperService
/// could do this, and it cannot: that is a MediaWiki API client that asks for
/// wikitext and pulls apart {{BossPokemon|...}} templates into typed fields.
/// The forum is Invision Community HTML. Nothing about the parsing carries
/// over. What DOES carry over is the shape around it - a gated window that
/// fetches, shows a preview, and writes nothing until Save - and that shape is
/// the whole reason this is safe to run against a page nobody here controls.
///
/// WHAT IT REUSES. AnnouncementsService has been reading this same forum in
/// production for a while, and its two hard-won facts are the ones that make
/// this possible without guessing at markup: a post's body is the div marked
/// data-ips-hook="postContent", and that div has to be closed by counting
/// nesting rather than by finding the next closing tag, because post bodies
/// contain quotes, spoilers and tables of their own. Both are borrowed here
/// rather than reinvented. SimpleHtmlParser, already in the tree for rendering
/// guides, does the parsing.
///
/// WHAT IT PRODUCES. A draft, not a finished guide. GuideHtmlRenderer only
/// understands a deliberate subset of HTML, so everything is normalised into
/// that subset: a blockquote becomes a section, a spoiler becomes a heading
/// plus its contents, a run of bullet-glyph lines becomes a real list, and
/// anything with no equivalent is dropped and counted in the warnings. Images
/// are dropped too - the renderer will not fetch remote ones and this
/// deliberately does not download them - so a scraped page is the words, and
/// the screenshots stay a manual job.
///
/// COURTESY AND SCOPE. It refuses any host but the PRO forum, sends a
/// User-Agent that says what it is, and fetches exactly one page. Whoever
/// wrote the thread wrote it for players to read, not for an app to
/// redistribute silently, so the generated page carries a credit line and a
/// link back to the source and the header says so in words.
/// </summary>
public static class ForumGuideScraperService
{
    /// <summary>The only host this will fetch from. A tool that writes files
    /// into the application folder should not also be a general-purpose
    /// downloader.</summary>
    public const string AllowedHost = "pokemonrevolution.net";

    /// <summary>Tags GuideHtmlRenderer understands and that survive the
    /// conversion unchanged. Anything outside this set is either mapped onto
    /// something in it or dropped - see Convert.</summary>
    private static readonly HashSet<string> PassThroughTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "h1", "h2", "h3", "p", "ul", "ol", "li", "u", "b", "strong", "i", "em", "span"
    };

    /// <summary>Tags with no rendered equivalent whose contents are still
    /// worth keeping - the children are emitted where the tag was.</summary>
    private static readonly HashSet<string> UnwrapTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "section", "article", "font", "center", "small", "sup", "sub", "label"
    };

    /// <summary>Tags dropped whole, contents included. Tables are the awkward
    /// one: the renderer has no table, and flattening a table into
    /// paragraphs reliably produces nonsense, so it is reported instead of
    /// guessed at.</summary>
    private static readonly HashSet<string> DropTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "table", "thead", "tbody", "tfoot", "tr", "td", "th",
        "script", "style", "iframe", "noscript", "video", "audio", "svg", "form", "input", "button"
    };

    /// <summary>The characters PRO guide authors use as bullets in a plain
    /// paragraph. A line starting with one of these is a list item that never
    /// got marked up as one.</summary>
    private const string BulletGlyphs = "▹•●▸‣»›";

    public sealed record GuideScrapeResult(
        string Html,
        string Title,
        string SourceUrl,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Fetches one topic page and converts its FIRST post into a draft guide.
    /// The first post is the guide by convention on this forum; replies are
    /// discussion, and pulling them in would turn a walkthrough into a thread.
    /// </summary>
    public static async Task<GuideScrapeResult> ScrapeAsync(
        string topicUrl, string guideTitle, HttpClient httpClient)
    {
        var warnings = new List<string>();

        if (!Uri.TryCreate(topicUrl, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("That is not a web address.", nameof(topicUrl));
        }

        if (!uri.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase)
            && !uri.Host.EndsWith("." + AllowedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"This only reads {AllowedHost} topics. The address given was for {uri.Host}.",
                nameof(topicUrl));
        }

        string html = await GetHtmlAsync(httpClient, uri.ToString()).ConfigureAwait(false);

        string body = ExtractFirstPostBody(html)
            ?? throw new InvalidOperationException(
                "No post content was found on that page. If the topic is visible only to logged-in " +
                "members, this cannot read it.");

        HtmlNode parsed = SimpleHtmlParser.ParseBody(body);

        var output = new StringBuilder();
        var state = new ConversionState(uri, warnings);

        Convert(parsed, output, state, depth: 3);

        if (state.ImagesDropped > 0)
        {
            warnings.Add(
                $"{state.ImagesDropped} image(s) were left out. Guides do not fetch remote images, " +
                "so screenshots have to be saved into the guide folder by hand and added as " +
                "<img src=\"filename.png\" />.");
        }

        if (state.TablesDropped > 0)
        {
            warnings.Add(
                $"{state.TablesDropped} table(s) were left out - the guide renderer has no table, and " +
                "flattening one into paragraphs reliably produces nonsense. Check the source page for " +
                "anything that mattered.");
        }

        if (output.Length == 0)
            warnings.Add("Nothing convertible was found in the first post.");

        string title = string.IsNullOrWhiteSpace(guideTitle) ? "Guide" : guideTitle.Trim();

        return new GuideScrapeResult(
            BuildPage(title, uri.ToString(), output.ToString()),
            title,
            uri.ToString(),
            warnings);
    }

    private static async Task<string> GetHtmlAsync(HttpClient httpClient, string url)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    // ====================================================================
    // Finding the post - both facts borrowed from AnnouncementsService,
    // which has been reading this forum in production.
    // ====================================================================

    private static readonly Regex PostContentHookRegex = new(
        @"<div[^>]*\bdata-ips-hook=""postContent""[^>]*>", RegexOptions.Compiled);

    private static readonly Regex DivTagRegex = new(@"<(/?)div\b[^>]*>", RegexOptions.Compiled);

    /// <summary>The first post's inner HTML, or null if the page has none.
    /// The closing div is found by counting nesting, not by taking the next
    /// one: a post body is full of its own divs.</summary>
    internal static string? ExtractFirstPostBody(string html)
    {
        Match opening = PostContentHookRegex.Match(html);

        if (!opening.Success)
            return null;

        int contentStart = opening.Index + opening.Length;
        int depth = 1;
        int pos = contentStart;

        while (true)
        {
            Match tag = DivTagRegex.Match(html, pos);

            if (!tag.Success)
                return null;

            depth += tag.Groups[1].Value.Length == 0 ? 1 : -1;
            pos = tag.Index + tag.Length;

            if (depth == 0)
                return html[contentStart..tag.Index];
        }
    }

    // ====================================================================
    // Conversion
    // ====================================================================

    private sealed class ConversionState(Uri source, List<string> warnings)
    {
        public Uri Source { get; } = source;
        public List<string> Warnings { get; } = warnings;
        public int ImagesDropped { get; set; }
        public int TablesDropped { get; set; }
    }

    private static void Convert(HtmlNode node, StringBuilder output, ConversionState state, int depth)
    {
        foreach (HtmlNode child in node.Children)
        {
            if (child.IsText)
            {
                string text = Collapse(child.Text ?? string.Empty);

                if (text.Length > 0)
                    output.Append(Indent(depth)).Append(WebUtility.HtmlEncode(text)).Append('\n');

                continue;
            }

            string tag = child.TagName.ToLowerInvariant();

            switch (tag)
            {
                case "img":
                    state.ImagesDropped++;
                    continue;

                case "br":
                    output.Append(Indent(depth)).Append("<br />\n");
                    continue;

                case "hr":
                    continue;

                case "blockquote":
                    // The renderer has no blockquote. A section is the closest
                    // thing it does have, and on this forum a blockquote is
                    // almost always a requirements box anyway.
                    output.Append(Indent(depth)).Append("<section>\n");
                    Convert(child, output, state, depth + 1);
                    output.Append(Indent(depth)).Append("</section>\n");
                    continue;

                case "a":
                    WriteLink(child, output, state, depth);
                    continue;

                case "h4":
                case "h5":
                case "h6":
                    // Nothing below h3 exists in the renderer; flattening is
                    // better than losing the heading entirely.
                    output.Append(Indent(depth)).Append("<h3>");
                    output.Append(WebUtility.HtmlEncode(Collapse(child.GetTextContent())));
                    output.Append("</h3>\n");
                    continue;
            }

            if (DropTags.Contains(tag))
            {
                if (tag is "table")
                    state.TablesDropped++;

                continue;
            }

            if (tag == "p" && TryWriteBulletList(child, output, depth))
                continue;

            if (IsSpoiler(child))
            {
                WriteSpoiler(child, output, state, depth);
                continue;
            }

            if (UnwrapTags.Contains(tag))
            {
                Convert(child, output, state, depth);
                continue;
            }

            if (PassThroughTags.Contains(tag))
            {
                output.Append(Indent(depth)).Append('<').Append(tag).Append(">\n");
                Convert(child, output, state, depth + 1);
                output.Append(Indent(depth)).Append("</").Append(tag).Append(">\n");
                continue;
            }

            // Unknown tag: keep what is inside it, same as the renderer's own
            // default branch does.
            Convert(child, output, state, depth);
        }
    }

    private static void WriteLink(HtmlNode node, StringBuilder output, ConversionState state, int depth)
    {
        string text = Collapse(node.GetTextContent());

        if (text.Length == 0)
            return;

        string? href = node.GetAttribute("href");

        // Relative links on the source page mean nothing once the words are
        // sitting in a local file, so they are resolved against the topic they
        // came from. Anything that will not resolve becomes plain text rather
        // than a link to nowhere.
        if (!string.IsNullOrWhiteSpace(href)
            && Uri.TryCreate(state.Source, href, out Uri? absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            output.Append(Indent(depth))
                  .Append("<a href=\"").Append(WebUtility.HtmlEncode(absolute.ToString())).Append("\">")
                  .Append(WebUtility.HtmlEncode(text))
                  .Append("</a>\n");
            return;
        }

        output.Append(Indent(depth)).Append(WebUtility.HtmlEncode(text)).Append('\n');
    }

    /// <summary>Invision marks a collapsible block with the ipsSpoiler class.
    /// The renderer cannot collapse anything, so the block is flattened - its
    /// header becomes a heading and its contents follow, which is what a
    /// reader wanted from it anyway.</summary>
    private static bool IsSpoiler(HtmlNode node) =>
        (node.GetAttribute("class") ?? string.Empty)
            .Contains("ipsSpoiler", StringComparison.OrdinalIgnoreCase);

    private static void WriteSpoiler(HtmlNode node, StringBuilder output, ConversionState state, int depth)
    {
        HtmlNode? header = FindByClass(node, "ipsSpoiler_header");
        HtmlNode? contents = FindByClass(node, "ipsSpoiler_contents");

        string heading = header is null ? string.Empty : Collapse(header.GetTextContent());

        // "Spoiler" on its own is Invision's placeholder, not a title.
        if (heading.Length > 0 && !heading.Equals("Spoiler", StringComparison.OrdinalIgnoreCase))
            output.Append(Indent(depth)).Append("<h3>").Append(WebUtility.HtmlEncode(heading)).Append("</h3>\n");

        Convert(contents ?? node, output, state, depth);
    }

    private static HtmlNode? FindByClass(HtmlNode node, string className)
    {
        foreach (HtmlNode child in node.Children)
        {
            if (child.IsText)
                continue;

            if ((child.GetAttribute("class") ?? string.Empty)
                .Contains(className, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }

            HtmlNode? found = FindByClass(child, className);

            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// A paragraph whose lines start with a bullet glyph is a list that was
    /// typed rather than marked up - the requirement blocks in these guides
    /// are written that way. It is split on the line breaks and emitted as a
    /// real list, which the renderer indents properly.
    ///
    /// Deliberately text-only: any bold or underline inside such a line is
    /// dropped. Carrying inline formatting through a split on br would double
    /// the size of this for a requirements line.
    /// </summary>
    private static bool TryWriteBulletList(HtmlNode paragraph, StringBuilder output, int depth)
    {
        var lines = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            string line = Collapse(current.ToString());

            if (line.Length > 0)
                lines.Add(line);

            current.Clear();
        }

        void Walk(HtmlNode node)
        {
            foreach (HtmlNode child in node.Children)
            {
                if (child.IsText)
                    current.Append(child.Text);
                else if (child.TagName.Equals("br", StringComparison.OrdinalIgnoreCase))
                    Flush();
                else
                    Walk(child);
            }
        }

        Walk(paragraph);
        Flush();

        var items = new List<string>();

        foreach (string line in lines)
        {
            if (BulletGlyphs.IndexOf(line[0]) < 0)
                return false;   // a non-bullet line means this is prose, not a list

            string item = Collapse(line[1..]);

            if (item.Length > 0)
                items.Add(item);
        }

        if (items.Count < 2)
            return false;

        output.Append(Indent(depth)).Append("<ul>\n");

        foreach (string item in items)
        {
            output.Append(Indent(depth + 1))
                  .Append("<li>").Append(WebUtility.HtmlEncode(item)).Append("</li>\n");
        }

        output.Append(Indent(depth)).Append("</ul>\n");
        return true;
    }

    // ====================================================================
    // Page assembly
    // ====================================================================

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Runs of whitespace become one space. The .NET regex class
    /// \s covers the non-breaking spaces this forum's markup is full of, so
    /// they collapse with everything else.</summary>
    private static string Collapse(string text) =>
        WhitespaceRegex.Replace(text, " ").Trim();

    private static string Indent(int depth) => new(' ', depth * 4);

    /// <summary>The same skeleton the hand-written guides use, so a scraped
    /// page and a hand-written one are the same kind of file. The credit is
    /// part of the page rather than a comment: this is somebody else's
    /// writing, and a reader should be able to see whose and go and read the
    /// original.</summary>
    private static string BuildPage(string title, string sourceUrl, string bodyHtml)
    {
        string encodedTitle = WebUtility.HtmlEncode(title);
        string encodedUrl = WebUtility.HtmlEncode(sourceUrl);

        return "<!DOCTYPE html>\n"
             + "<html lang=\"en\">\n"
             + "<head>\n"
             + "    <meta charset=\"utf-8\">\n"
             + "    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
             + "    <link rel=\"stylesheet\" href=\"guide.css\">\n"
             + "    <title>" + encodedTitle + "</title>\n"
             + "</head>\n"
             + "<body>\n"
             + "    <article class=\"guide\">\n"
             + "        <h1>" + encodedTitle + "</h1>\n"
             + "        <section>\n"
             + "            <p>Scraped from the PRO forums. The words below are the original author's.</p>\n"
             + "            <a href=\"" + encodedUrl + "\">" + encodedUrl + "</a>\n"
             + "        </section>\n"
             + "        <section>\n"
             + bodyHtml
             + "        </section>\n"
             + "    </article>\n"
             + "</body>\n"
             + "</html>\n";
    }
}
