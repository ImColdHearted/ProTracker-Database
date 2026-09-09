namespace Foot_Tracker.ViewModels;

/// <summary>
/// §160. The team-row confirmation card: clicking a teammate in battle
/// used to switch on the spot, so an accidental click threw the turn away.
/// Now the click opens a small dialog showing who would come in - sprite,
/// HP, status and held item - and the switch only happens on an explicit
/// "Switch In" (or "Send In" after a faint). Everything here is set once
/// before the dialog opens; the SimulatorTeamSlotItem it wraps is the live
/// row item, so the numbers shown are the battle's own.
/// </summary>
public sealed class SimulatorSwitchConfirmViewModel
{
    public required SimulatorTeamSlotItem Slot { get; init; }

    /// <summary>"Switch to X?" mid-battle, "Send X in?" after a faint.</summary>
    public required string Prompt { get; init; }

    /// <summary>The action button's label: "Switch In" or "Send In".</summary>
    public required string ActionLabel { get; init; }
}
