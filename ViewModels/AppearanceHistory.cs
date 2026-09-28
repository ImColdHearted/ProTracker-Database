namespace Foot_Tracker.ViewModels;

/// <summary>
/// §381. Undo and redo for the Appearance editor, as a stack of snapshots.
///
/// A snapshot is the whole working appearance serialised to a string (see
/// AppearanceViewModel.Snapshot) - every edit the editor can make is a change
/// to that one object, so remembering the object before each edit is enough
/// to undo any of them, and there is no per-property command class to keep
/// in step with the settings model.
///
/// Edits are coalesced by KEY: a colour picker being dragged raises dozens
/// of changes to the same property in a second, and undoing them one hue at
/// a time is not what anyone means by Undo. Consecutive edits to the same
/// key within <see cref="CoalesceWindow"/> of each other fold into one step,
/// which remembers the state before the FIRST of them.
/// </summary>
public sealed class AppearanceHistory
{
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(700);

    /// <summary>Bounded so a long session of slider dragging cannot grow
    /// without limit; the oldest step falls off the far end.</summary>
    public const int Capacity = 200;

    private readonly List<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string? _lastKey;
    private DateTime _lastAt = DateTime.MinValue;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoDepth => _undo.Count;

    /// <summary>Records that an edit with this key is about to leave
    /// <paramref name="before"/> behind. Any redo history is discarded: a new
    /// edit after an undo is a fork, and the abandoned branch is gone.</summary>
    public void Record(string key, string before)
    {
        _redo.Clear();

        DateTime now = DateTime.UtcNow;

        bool coalesce = _undo.Count > 0 &&
                        string.Equals(key, _lastKey, StringComparison.Ordinal) &&
                        now - _lastAt <= CoalesceWindow;

        if (!coalesce)
        {
            _undo.Add(before);

            if (_undo.Count > Capacity)
                _undo.RemoveAt(0);
        }

        _lastKey = key;
        _lastAt = now;
    }

    /// <summary>The snapshot to restore, with <paramref name="current"/>
    /// kept for Redo; null when there is nothing to undo.</summary>
    public string? Undo(string current)
    {
        if (_undo.Count == 0)
            return null;

        string before = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Push(current);
        _lastKey = null;

        return before;
    }

    /// <summary>The snapshot to restore, with <paramref name="current"/>
    /// kept for Undo; null when there is nothing to redo.</summary>
    public string? Redo(string current)
    {
        if (_redo.Count == 0)
            return null;

        string next = _redo.Pop();
        _undo.Add(current);
        _lastKey = null;

        return next;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _lastKey = null;
    }
}
