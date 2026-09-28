using Avalonia.Controls;

namespace ProTracker.Companion;

/// <summary>
/// §295. The page stack. The desktop opens a window per thing; a phone has
/// one screen, so a thing is a page pushed onto this, and the hardware back
/// button pops it. MainView shows whatever is on top.
/// </summary>
public static class Nav
{
    private sealed record Entry(Control Page, string Title);

    private static readonly Stack<Entry> stack = new();

    public static event Action? Changed;

    public static Control? Current => stack.Count == 0 ? null : stack.Peek().Page;

    public static string Title => stack.Count == 0 ? string.Empty : stack.Peek().Title;

    public static bool CanGoBack => stack.Count > 1;

    /// <summary>Replace the whole stack - a bottom-bar tab.</summary>
    public static void Root(Control page, string title)
    {
        stack.Clear();
        stack.Push(new Entry(page, title));
        Changed?.Invoke();
    }

    public static void Push(Control page, string title)
    {
        stack.Push(new Entry(page, title));
        Changed?.Invoke();
    }

    public static bool Back()
    {
        if (stack.Count <= 1)
            return false;

        stack.Pop();
        Changed?.Invoke();
        return true;
    }
}
