using LinqToDB;
using LinqToDB.Data;
using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Database;

public class RaidStatsDb : DataConnection
{
    public RaidStatsDb(DataOptions<RaidStatsDb> options) : base(options.Options) { }

    public ITable<PlayerEntity> Players => this.GetTable<PlayerEntity>();
    public ITable<EncounterEntity> Encounters => this.GetTable<EncounterEntity>();
    public ITable<PlayerEncounterEntity> PlayerEncounters => this.GetTable<PlayerEncounterEntity>();
    public ITable<MechanicEventEntity> MechanicEvents => this.GetTable<MechanicEventEntity>();
    public ITable<IgnoredBossEntity> IgnoredBosses => this.GetTable<IgnoredBossEntity>();
    public ITable<IncludedPlayerEntity> IncludedPlayers => this.GetTable<IncludedPlayerEntity>();
    public ITable<SettingsEntity> Settings => this.GetTable<SettingsEntity>();
    public ITable<RecapFunStatEntity> RecapFunStats => this.GetTable<RecapFunStatEntity>();
    public ITable<EncounterPhaseStatEntity> EncounterPhaseStats => this.GetTable<EncounterPhaseStatEntity>();
    public ITable<DiscordConfigEntity> DiscordConfigs => this.GetTable<DiscordConfigEntity>();
    public ITable<DiscordUserLinkEntity> DiscordUserLinks => this.GetTable<DiscordUserLinkEntity>();
    public ITable<NotificationQueueEntity> NotificationQueue => this.GetTable<NotificationQueueEntity>();
}
