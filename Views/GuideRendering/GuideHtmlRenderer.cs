using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Foot_Tracker.Views.GuideRendering;

/// <summary>
/// Renders our own guide HTML (parsed by SimpleHtmlParser) into native Avalonia
/// controls - no browser engine involved at all. guide.css's colors/spacing are
/// replicated directly here rather than parsed dynamically (a real CSS engine is
/// out of scope for what's ultimately a handful of static, styled guide pages).
///
/// Supported tags: article, section, div, h1/h2/h3, p, ul/li, a, img, br, u.
/// Anything else is skipped (its children are still rendered, e.g. an unknown
/// wrapper tag), so unfamiliar markup degrades gracefully rather than disappearing.
/// </summary>
public sealed class GuideHtmlRenderer
{
    private static readonly Color HeadingColor = Color.FromRgb(0xFF, 0x00, 0x8C);   // #ff008c
    private static readonly Color LinkColor = Color.FromRgb(0x42, 0xC8, 0xFF);      // #42c8ff
    private static readonly Color BodyTextColor = Colors.White;
    private static readonly Color SectionBackground = Color.FromRgb(0x10, 0x10, 0x10); // #101010
    private static readonly Color SectionBorder = Color.FromRgb(0x55, 0x55, 0x55);     // #555

    private readonly string _assetsBaseFolder;
    private readonly string _guideFolder;
    private readonly Dictionary<string, Control> _anchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _onExternalLink;
    private ScrollViewer? _scrollViewer;

    /// <param name="assetsBaseFolder">AppContext.BaseDirectory - used to resolve
    /// the guide's "https://assets.local/..." image URLs to real sprite files.</param>
    /// <param name="guideFolder">The specific guide's own folder (e.g.
    /// DataFiles/Guides/Test) - used to resolve plain relative image paths.</param>
    /// <param name="onExternalLink">Called with the URL when a non-anchor link is clicked.</param>
    public GuideHtmlRenderer(string assetsBaseFolder, string guideFolder, Action<string> onExternalLink)
    {
        _assetsBaseFolder = assetsBaseFolder;
        _guideFolder = guideFolder;
        _onExternalLink = onExternalLink;
    }

    /// <summary>Builds the full scrollable guide view from a parsed body node.</summary>
    public ScrollViewer Render(HtmlNode bodyNode)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            MaxWidth = 850,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        RenderChildren(bodyNode, content);

        _scrollViewer = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(18),
                Child = content
            }
        };

        return _scrollViewer;
    }

    /// <summary>Scrolls so the element with the given id (from an href="#id" link) is visible.</summary>
    public void ScrollToAnchor(string anchorId)
    {
        if (_scrollViewer is null || !_anchors.TryGetValue(anchorId, out Control? target))
            return;

        Point position = target.TranslatePoint(new Point(0, 0), _scrollViewer) ?? default;
        double targetOffset = _scrollViewer.Offset.Y + position.Y - 16;

        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, Math.Max(0, targetOffset));
    }

    private void RenderChildren(HtmlNode parent, Panel host)
    {
        foreach (HtmlNode child in parent.Children)
        {
            Control? rendered = RenderNode(child);
            if (rendered is not null)
                host.Children.Add(rendered);
        }
    }

    private Control? RenderNode(HtmlNode node)
    {
        if (node.IsText)
        {
            string text = (node.Text ?? string.Empty).Trim();
            return text.Length == 0 ? null : MakeTextBlock(text, BodyTextColor);
        }

        switch (node.TagName.ToLowerInvariant())
        {
            case "h1":
                return MakeHeading(node, fontSize: 26);

            case "h2":
                return MakeHeading(node, fontSize: 20);

            case "h3":
                return MakeHeading(node, fontSize: 16);

            case "p":
                return MakeParagraph(node);

            case "ul":
            case "ol":
                return MakeList(node);

            case "img":
                return MakeImage(node);

            case "section":
                return MakeSection(node);

            case "article":
            case "div":
            {
                var panel = new StackPanel { Spacing = 8 };
                RenderChildren(node, panel);
                return panel;
            }

            case "a":
                return MakeLink(node);

            default:
                // Unknown tag - render its children directly so content still shows up.
                if (node.Children.Count == 0)
                    return null;

                var wrapper = new StackPanel { Spacing = 4 };
                RenderChildren(node, wrapper);
                return wrapper.Children.Count == 0 ? null : wrapper;
        }
    }

    private Control MakeHeading(HtmlNode node, double fontSize)
    {
        string text = node.GetTextContent().Trim();

        var block = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(HeadingColor),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4)
        };

        // The guide wraps whole headings in <u> - detect and apply as a block-level
        // underline rather than trying to build partial-underline inline runs.
        if (HasSingleWrappingTag(node, "u"))
        {
            block.TextDecorations = TextDecorations.Underline;
        }

        RegisterAnchorIfPresent(node, block);
        return block;
    }

    private Control MakeParagraph(HtmlNode node)
    {
        var inlines = new InlineCollection();

        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(BodyTextColor),
            LineHeight = 20,
            Inlines = inlines
        };

        AppendInlines(node, inlines);
        RegisterAnchorIfPresent(node, block);
        return block;
    }

    /// <summary>Builds TextBlock.Inlines from mixed text/&lt;br&gt;/&lt;u&gt; content -
    /// deliberately using only Run and LineBreak, the most fundamental Inline types,
    /// rather than gambling on less-certain inline formatting APIs.</summary>
    private void AppendInlines(HtmlNode node, InlineCollection inlines)
    {
        foreach (HtmlNode child in node.Children)
        {
            if (child.IsText)
            {
                string text = child.Text ?? string.Empty;
                if (text.Trim().Length > 0 || inlines.Count > 0)
                    inlines.Add(new Run(NormalizeWhitespace(text)));

                continue;
            }

            switch (child.TagName.ToLowerInvariant())
            {
                case "br":
                    inlines.Add(new LineBreak());
                    break;

                case "u":
                case "span":
                case "b":
                case "strong":
                case "i":
                case "em":
                    // Fold formatting-only wrapper tags down to plain text - see the
                    // class doc comment for why per-run underline isn't attempted.
                    AppendInlines(child, inlines);
                    break;

                default:
                    string text = child.GetTextContent().Trim();
                    if (text.Length > 0)
                        inlines.Add(new Run(text));
                    break;
            }
        }
    }

    private Control MakeList(HtmlNode node)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(8, 4, 0, 4) };

        foreach (HtmlNode item in node.Children.Where(c => !c.IsText && c.TagName.Equals("li", StringComparison.OrdinalIgnoreCase)))
        {
            // A list item that's just a link (e.g. the "Available Mega Stones" index)
            // renders as a clickable line; anything else renders as plain bulleted text.
            HtmlNode? onlyLink = item.Children.FirstOrDefault(c => !c.IsText && c.TagName.Equals("a", StringComparison.OrdinalIgnoreCase));

            if (onlyLink is not null && item.Children.Count(c => !c.IsText) == 1)
            {
                Control link = MakeLink(onlyLink, bulletPrefix: true);
                panel.Children.Add(link);
                RegisterAnchorIfPresent(item, link);
                continue;
            }

            var line = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(BodyTextColor)
            };

            var lineInlines = new InlineCollection();
            lineInlines.Add(new Run("•  "));
            AppendInlines(item, lineInlines);
            line.Inlines = lineInlines;

            panel.Children.Add(line);
            RegisterAnchorIfPresent(item, line);
        }

        return panel;
    }

    private Control MakeLink(HtmlNode node, bool bulletPrefix = false)
    {
        string text = node.GetTextContent().Trim();
        string href = node.GetAttribute("href") ?? string.Empty;

        var block = new TextBlock
        {
            Text = bulletPrefix ? $"•  {text}" : text,
            Foreground = new SolidColorBrush(LinkColor),
            TextDecorations = TextDecorations.Underline,
            TextWrapping = TextWrapping.Wrap,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };

        block.PointerPressed += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(href))
                return;

            if (href.StartsWith('#'))
            {
                ScrollToAnchor(href[1..]);
            }
            else
            {
                _onExternalLink(href);
            }
        };

        RegisterAnchorIfPresent(node, block);
        return block;
    }

    private Control? MakeImage(HtmlNode node)
    {
        string src = node.GetAttribute("src") ?? string.Empty;
        string? localPath = ResolveImagePath(src);

        if (localPath is null || !File.Exists(localPath))
            return null; // Missing/placeholder images are skipped, not shown broken.

        try
        {
            var image = new Image
            {
                Source = new Bitmap(localPath),
                Stretch = Stretch.Uniform,
                MaxWidth = 60,
                MaxHeight = 60,
                Margin = new Thickness(0, 4, 0, 4)
            };

            RegisterAnchorIfPresent(node, image);
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The guide's src="https://assets.local/Assets/Sprites/462.png" URLs only ever
    /// worked because the original WinForms WebView2 mapped that fake domain to a
    /// local folder (SetVirtualHostNameToFolderMapping). Replicating the same mapping
    /// here: strip the fake domain, resolve the rest under SharedPokemonLibrary/.
    /// A plain relative path (no scheme) resolves against the guide's own folder
    /// instead - covers future guides that just ship their own local images.
    /// Remote (non-assets.local) http(s) URLs aren't fetched - out of scope here.
    /// </summary>
    private string? ResolveImagePath(string src)
    {
        if (string.IsNullOrWhiteSpace(src))
            return null;

        const string virtualHostPrefix = "https://assets.local/";

        if (src.StartsWith(virtualHostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string relative = src[virtualHostPrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(_assetsBaseFolder, "SharedPokemonLibrary", relative);
        }

        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string relativePath = src.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_guideFolder, relativePath);
    }

    private Control MakeSection(HtmlNode node)
    {
        var panel = new StackPanel { Spacing = 8 };
        RenderChildren(node, panel);

        var border = new Border
        {
            Background = new SolidColorBrush(SectionBackground),
            BorderBrush = new SolidColorBrush(SectionBorder),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 24),
            Child = panel
        };

        RegisterAnchorIfPresent(node, border);
        return border;
    }

    private static TextBlock MakeTextBlock(string text, Color color) =>
        new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(color)
        };

    private void RegisterAnchorIfPresent(HtmlNode node, Control control)
    {
        string? id = node.GetAttribute("id");
        if (!string.IsNullOrWhiteSpace(id))
            _anchors[id] = control;
    }

    private static bool HasSingleWrappingTag(HtmlNode node, string tagName)
    {
        var elementChildren = node.Children.Where(c => !c.IsText).ToList();
        return elementChildren.Count == 1 &&
               elementChildren[0].TagName.Equals(tagName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
