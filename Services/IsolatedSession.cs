namespace Foot_Tracker.Services;

/// <summary>§249. Why the active hunting session is isolated from the normal
/// client's records - or <see cref="None"/> when it is not.</summary>
public enum IsolationReason
{
    /// <summary>The normal per-client session is active. Every store writes.</summary>
    None,

    /// <summary>§101. The Admin Client override: an in-memory diagnostic
    /// session that is never persisted, so nothing an admin does can reach a
    /// normal client's data. See <see cref="AdminModeService"/>.</summary>
    Admin,

    /// <summary>§250. World Quest mode: a persisted per-client, per-quest
    /// session hunting the quest species, isolated from the normal client's
    /// records for the same reasons. See <see cref="WorldQuestMode"/>.</summary>
    WorldQuest,
}

/// <summary>
/// §249. The one question every data store asks before it writes: is the
/// hunting session currently isolated from the normal client's records?
///
/// Until now that question was spelled <c>AdminModeService.IsActive</c>, in
/// forty-two places, because the Admin Client override was the only reason a
/// session was ever isolated. The World Quest mode that follows is a second
/// reason - a separate session, with the quest species forced as the target,
/// that must not touch lifetime stats, the Catch Logs or the encounter
/// history either. Adding a second flag beside the first at every one of
/// those sites would mean forty-two more checks and one missed check would be
/// silent contamination of exactly the data the mode exists to protect.
///
/// So the question is asked once, here. The stores that guard NORMAL-CLIENT
/// HUNTING RECORDS read <see cref="IsActive"/> and no longer care why; the
/// code that needs to know WHICH isolated session is running - the session
/// router, and one day the title bar - reads <see cref="Reason"/>. A new
/// reason is added in exactly one place: the expression behind
/// <see cref="Reason"/>.
///
/// What deliberately did NOT move here, and why, is recorded in
/// MIGRATION_GUIDE.md §249. In short: anything that names the admin context
/// or drives admin UI still reads <c>AdminModeService.IsActive</c>, and so do
/// the boss-cooldown and PVP registrations and the forced-takeover check -
/// because for those the right behaviour under a future isolation reason is
/// the NORMAL one, not the admin one. A boss defeated during a World Quest is
/// a real boss on a real cooldown.
///
/// §249 introduced this as a pure refactor with only the admin reason. §250
/// added the second reason it was built for, in the one expression below.
/// </summary>
public static class IsolatedSession
{
    /// <summary>Why the session is isolated. The ONLY place a reason is
    /// decided - a new mode is added here and nowhere else.
    ///
    /// Admin Client wins if both are on: it is the more restrictive context
    /// (nothing it does is ever persisted), and WorldQuestMode.Enter refuses
    /// while it is active anyway, so the ordering here is belt and braces
    /// rather than a path anything takes.</summary>
    public static IsolationReason Reason =>
        AdminModeService.IsActive ? IsolationReason.Admin
        : WorldQuestMode.IsActive ? IsolationReason.WorldQuest
        : IsolationReason.None;

    /// <summary>True while the active session must not touch the normal
    /// client's hunting records, for any reason. This is what the data gates
    /// read.</summary>
    public static bool IsActive => Reason != IsolationReason.None;
}
