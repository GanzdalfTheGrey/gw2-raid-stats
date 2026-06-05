namespace GW2RaidStats.Core.Events;

/// <summary>
/// A single role slot on an event (e.g. "Heal Quick" with Count=2). Id is stable
/// across edits so existing signups keep their slot reference even if the label
/// changes. Stored on the event as a JSON array under role_slots_json.
///
/// Role and Boon are optional categorisation tags consumed by the event's boon-cap
/// enforcement (when <c>EnforceBoonCaps</c> is on) and by Squad Builder seeding to
/// map a slot to a Squad Builder role enum.
///
///   Role: "heal" | "boondps" | "dps" | null (no tag)
///   Boon: "quick" | "alac" | null (no boon)
///
/// Older slots in stored JSON don't have these fields — they deserialize with both
/// tags = null, which disables every cap and Squad Builder mapping for that slot.
/// </summary>
public record RoleSlot(string Id, string Label, int Count, string? Role = null, string? Boon = null);
