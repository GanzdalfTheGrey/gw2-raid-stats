using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("discord_user_links")]
public class DiscordUserLinkEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("discord_user_id"), NotNull]
    public long DiscordUserId { get; set; }

    [Column("player_id"), NotNull]
    public Guid PlayerId { get; set; }

    [Column("personal_best_dms_enabled"), NotNull]
    public bool PersonalBestDmsEnabled { get; set; }

    [Column("wall_of_shame_opted_in"), NotNull]
    public bool WallOfShameOptedIn { get; set; }

    [Column("linked_at"), NotNull]
    public DateTimeOffset LinkedAt { get; set; }
}
