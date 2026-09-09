namespace Foot_Tracker.Views.GuideRendering;

/// <summary>Minimal HTML tree node - either an element (TagName set) or a text node (Text set).</summary>
public sealed class HtmlNode
{
    public string TagName { get; init; } = string.Empty;
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<HtmlNode> Children { get; } = new();
    public string? Text { get; init; }

    public bool IsText => Text is not null;

    public string? GetAttribute(string name) =>
        Attributes.TryGetValue(name, out string? value) ? value : null;

    /// <summary>Concatenated plain-text content of this node and all descendants.</summary>
    public string GetTextContent()
    {
        if (IsText)
            return Text ?? string.Empty;

        return string.Concat(Children.Select(c => c.GetTextContent()));
    }
}
