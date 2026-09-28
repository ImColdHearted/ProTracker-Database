using System;
using Avalonia;
using Avalonia.Controls;
// §192: Screen lives in Avalonia.Platform, Screens in Avalonia.Controls -
// KeepOnScreen below needs both.
using Avalonia.Platform;
using Serilog;

namespace Foot_Tracker.Services;

/// <summary>
/// One-instance-per-window-type opener (MIGRATION_GUIDE.md §103): clicking a
/// menu item whose window is already open ACTIVATES that window instead of
/// stacking a duplicate - the same pattern MainWindow's per-Pokémon session
/// history dictionary already used (§99), generalized. The §103 bug this
/// grew out of was different (the Stats menu's parent item had its own Click
/// handler, so Avalonia's routed MenuItem.Click BUBBLED every child click
/// into a second window - fixed at the menu, not here), but once one window
/// of each kind is the rule, this registry is what enforces it everywhere.
///
/// The registry only ever holds windows that are currently OPEN: each entry
/// removes itself in its window's Closed handler, so nothing here pins a
/// closed window against garbage collection. Ownership stays explicit - the
/// caller passes the owner every time, and different window types remain
/// free to be open together (the key is the window type, plus an optional
/// per-content key for types deliberately allowed multiple instances, e.g.
/// one CounterpartsWindow per event group). Not for modal dialogs - those
/// block their owner, so duplicates cannot happen in the first place.
/// </summary>
public static class WindowRegistry
{
    private static readonly Dictionary<string, Window> openWindows = new();

    /// <summary>Shows a new TWindow owned by <paramref name="owner"/>, or
    /// activates the already-open one. <paramref name="contentKey"/>
    /// distinguishes deliberate multi-instance types (one window per guide,
    /// per counterpart group, ...); leave null for strict one-per-type.</summary>
    public static void ShowOrActivate<TWindow>(Window owner, Func<TWindow> factory, string? contentKey = null)
        where TWindow : Window
    {
        string key = contentKey is null
            ? typeof(TWindow).FullName!
            : $"{typeof(TWindow).FullName}|{contentKey}";

        if (openWindows.TryGetValue(key, out Window? existing))
        {
            existing.Activate();
            return;
        }

        TWindow window = factory();

        openWindows[key] = window;
        window.Closed += (_, _) => openWindows.Remove(key);

        ShowUnowned(window, owner);
    }

    /// <summary>§407. The open window of a type, if there is one - for a
    /// caller that wants to hand it something before bringing it forward,
    /// as the Search's Locations does with the Maps window.</summary>
    public static TWindow? TryGet<TWindow>(string? contentKey = null)
        where TWindow : Window
    {
        string key = contentKey is null
            ? typeof(TWindow).FullName!
            : $"{typeof(TWindow).FullName}|{contentKey}";

        return openWindows.TryGetValue(key, out Window? existing) ? existing as TWindow : null;
    }

    /// <summary>
    /// §189. Shows a non-modal window WITHOUT making it an OWNED window, and
    /// then puts it where WindowStartupLocation="CenterOwner" would have.
    ///
    /// An owned window is minimized, restored and z-ordered by the operating
    /// system along with its owner. That is exactly right for a modal dialog
    /// and exactly wrong for every window here: minimizing the tracker took
    /// Boss Cooldowns, the Simulator, Catch Logs and everything else down
    /// with it, which is the opposite of why a second window is open. Users
    /// asked for the two to be independent.
    ///
    /// Ownership was not buying anything else. The registry above already
    /// keeps one window per type and still removes each on Closed, and modal
    /// dialogs still pass their owner to ShowDialog, where ownership is what
    /// makes them modal. The one thing worth keeping was being centred on the
    /// window you opened it from, and that is done here by hand.
    ///
    /// It is done on Opened rather than before Show because a window that
    /// sizes to its content has no size to centre until it has one.
    ///
    /// §192: and the result is then clamped onto the screen. Section 189 left
    /// it unclamped on the reasoning that a monitor to the left of the
    /// primary one has negative coordinates, so clamping at zero would drag
    /// windows onto the wrong screen. That much was true; the conclusion was
    /// not. Centring a TALLER child on a SHORTER owner puts the child's top
    /// ABOVE the owner's - the Simulator is 770 tall against the tracker's
    /// 600, which is 85 above - so opening it while the tracker sat at the
    /// top of the screen put its title bar off the top edge, with no way to
    /// move the window back. The answer was never to clamp at zero. It is to
    /// clamp to the working area of the screen the OWNER is on, which is what
    /// KeepOnScreen below does and which handles the negative-coordinate
    /// monitor correctly, because that monitor's working area has negative
    /// coordinates too.
    ///
    /// One consequence to know about: an unowned window can now go BEHIND the
    /// tracker when the tracker is clicked. Clicking the menu item again
    /// brings it back, because ShowOrActivate above activates the window it
    /// already has.
    /// </summary>
    public static void ShowUnowned(Window window, Window owner)
    {
        // Without an owner there is nothing for CenterOwner to centre on, so
        // it is switched off before the platform can act on it.
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        window.Opened += CentreOnOpener;
        window.Show();

        void CentreOnOpener(object? sender, EventArgs e)
        {
            window.Opened -= CentreOnOpener;

            try
            {
                // FrameSize includes the title bar and border, which is what
                // has to be centred; Bounds is the client area and is the
                // fallback for a platform that does not report a frame.
                Size ownerSize = owner.FrameSize ?? owner.Bounds.Size;
                Size windowSize = window.FrameSize ?? window.Bounds.Size;

                // Position is in physical pixels, the sizes above are in
                // device-independent ones.
                double scaling = owner.DesktopScaling;

                var centred = new PixelPoint(
                    owner.Position.X + (int)((ownerSize.Width - windowSize.Width) * scaling / 2),
                    owner.Position.Y + (int)((ownerSize.Height - windowSize.Height) * scaling / 2));

                window.Position = KeepOnScreen(centred, owner, windowSize, scaling);
            }
            catch (Exception ex)
            {
                // Where the window landed is a convenience, not a
                // requirement - it is already open and usable.
                Log.Warning(ex, "Could not centre {Window} on the window that opened it",
                    window.GetType().Name);
            }
        }
    }

    /// <summary>
    /// §192. Pulls a computed window position onto the working area of the
    /// screen the owner is on, so a centred window can never open with its
    /// title bar off an edge.
    ///
    /// The working area rather than the full bounds, so a window does not
    /// open underneath the taskbar. The OWNER's screen rather than the
    /// primary one, so a tracker on a second monitor opens its windows on
    /// that monitor - including a monitor to the left of the primary, whose
    /// working area has negative coordinates and whose windows a naive clamp
    /// at zero would have dragged across to the wrong screen.
    ///
    /// The far edges are pulled in BEFORE the near ones on purpose. A window
    /// larger than the working area cannot satisfy both, and of the two, the
    /// top-left is the one that has to win: that is where the title bar is,
    /// and a window whose title bar cannot be reached cannot be moved.
    ///
    /// Scaling comes from the owner, the same value the centring above uses.
    /// On a mixed-DPI desktop the two screens can disagree, which makes this
    /// approximate rather than exact - a few pixels either way is beside the
    /// point when the job is keeping a title bar on screen.
    /// </summary>
    private static PixelPoint KeepOnScreen(
        PixelPoint wanted, Window owner, Size windowSize, double scaling)
    {
        Screens? screens = owner.Screens;

        Screen? screen =
            screens?.ScreenFromWindow(owner)
            ?? screens?.ScreenFromPoint(owner.Position)
            ?? screens?.Primary;

        if (screen is null)
            return wanted;

        PixelRect area = screen.WorkingArea;

        int width = (int)(windowSize.Width * scaling);
        int height = (int)(windowSize.Height * scaling);

        int x = Math.Min(wanted.X, area.X + Math.Max(0, area.Width - width));
        int y = Math.Min(wanted.Y, area.Y + Math.Max(0, area.Height - height));

        return new PixelPoint(
            Math.Max(x, area.X),
            Math.Max(y, area.Y));
    }
}
