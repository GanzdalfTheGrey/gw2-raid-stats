using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("event_signups")]
public class EventSignupEntity
{
    [Column("id"), PrimaryKey] public Guid Id { get; set; }
    [Column("event_id"), NotNull] public Guid EventId { get; set; }
    [Column("discord_user_id"), NotNull] public long DiscordUserId { get; set; }
    [Column("player_id")] public Guid? PlayerId { get; set; }
    [Column("slot_id")] public string? SlotId { get; set; }
    [Column("status"), NotNull] public string Status { get; set; } = "Accepted";
    [Column("signed_up_at"), NotNull] public DateTimeOffset SignedUpAt { get; set; }
    [Column("updated_at"), NotNull] public DateTimeOffset UpdatedAt { get; set; }
}
