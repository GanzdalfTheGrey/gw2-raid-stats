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
}
