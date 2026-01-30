using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("discord_config")]
public class DiscordConfigEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("guild_id"), NotNull]
    public long GuildId { get; set; }

    [Column("guild_name")]
    public string? GuildName { get; set; }

    [Column("notification_channel_id")]
    public long? NotificationChannelId { get; set; }

    [Column("notifications_enabled"), NotNull]
    public bool NotificationsEnabled { get; set; }

    [Column("wall_of_shame_enabled"), NotNull]
    public bool WallOfShameEnabled { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at"), NotNull]
    public DateTimeOffset UpdatedAt { get; set; }
}
