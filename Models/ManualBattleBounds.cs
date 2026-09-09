namespace Foot_Tracker.Models;

/// <summary>
/// §135. A battle-window box the user drew by hand in the Set Screen
/// Boundaries window: its position and size in PIXELS of the frame it was
/// drawn on, top-left origin, plus that frame's size.
///
/// Pixels rather than fractions of the frame on purpose. PRO's battle window
/// is a fixed pixel size for a given GUI scale, and it opens CENTRED in the
/// game window: across seventeen real frames at eight window sizes (959x765
/// up to 1622x1080) the automatically located window sat at a constant +37
/// pixels right of centre and within about ten pixels of vertical centre. A
/// fraction would stretch the box with the window; a pixel box does not.
///
/// So the box is re-placed, not re-scaled, when the frame is a different
/// size from the one it was drawn on: LeftIn/TopIn shift it by half the
/// difference in frame size, which keeps it on the re-centred battle window
/// after the player resizes the game (and leaves it exactly where it was
/// drawn when the frame is unchanged). What it cannot survive is a change
/// of GUI scale, which changes the battle window's pixel size - the log and
/// the Set Screen Boundaries window both say "drawn on a WxH frame" so that
/// case is recognisable, and the box is simply redrawn.
///
/// Stored per client inside UiPreferences (a fallback for the capture of
/// THIS client's window belongs with that client's other display choices),
/// and only ever used when automatic detection finds nothing - see
/// BattleWindowLocator.TryLocate.
/// </summary>
public sealed class ManualBattleBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }

    /// <summary>Where the box's left edge falls in a frame of the given
    /// width: unchanged for the frame it was drawn on, shifted by half the
    /// width difference otherwise, because the battle window re-centres.</summary>
    public int LeftIn(int frameWidth) => X + (frameWidth - FrameWidth) / 2;

    /// <summary>Same rule for the top edge.</summary>
    public int TopIn(int frameHeight) => Y + (frameHeight - FrameHeight) / 2;
}
