namespace GW2RaidStats.Core.Events;

/// <summary>
/// A single role slot on an event (e.g. "Heal Quick" with Count=2). Id is stable
/// across edits so existing signups keep their slot reference even if the label
/// changes. Stored on the event as a JSON array under role_slots_json.
/// </summary>
public record RoleSlot(string Id, string Label, int Count);
