using System.Text.Json;
using System.Text.Json.Serialization;

namespace GW2RaidStats.Core.EliteInsights;

/// <summary>
/// Root object for Elite Insights JSON log files
/// </summary>
public class EliteInsightsLog
{
    [JsonPropertyName("triggerID")]
    public int TriggerId { get; set; }

    [JsonPropertyName("fightName")]
    public string FightName { get; set; } = string.Empty;

    [JsonPropertyName("fightIcon")]
    public string? FightIcon { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("isCM")]
    public bool IsCM { get; set; }

    [JsonPropertyName("isLegendaryCM")]
    public bool? IsLegendaryCM { get; set; }

    [JsonPropertyName("durationMS")]
    public int DurationMs { get; set; }

    [JsonPropertyName("encounterStart")]
    public string? EncounterStart { get; set; }

    [JsonPropertyName("encounterEnd")]
    public string? EncounterEnd { get; set; }

    [JsonPropertyName("timeStart")]
    public string? TimeStart { get; set; }

    [JsonPropertyName("timeEnd")]
    public string? TimeEnd { get; set; }

    [JsonPropertyName("timeStartStd")]
    public string? TimeStartStd { get; set; }

    [JsonPropertyName("timeEndStd")]
    public string? TimeEndStd { get; set; }

    [JsonPropertyName("recordedBy")]
    public string? RecordedBy { get; set; }

    [JsonPropertyName("recordedAccountBy")]
    public string? RecordedAccountBy { get; set; }

    [JsonPropertyName("players")]
    public List<EIPlayer> Players { get; set; } = [];

    [JsonPropertyName("mechanics")]
    public List<EIMechanic>? Mechanics { get; set; }

    [JsonPropertyName("uploadLinks")]
    public List<string>? UploadLinks { get; set; }

    [JsonPropertyName("phases")]
    public List<EIPhase>? Phases { get; set; }

    [JsonPropertyName("targets")]
    public List<EITarget>? Targets { get; set; }
}

public class EIPlayer
{
    [JsonPropertyName("account")]
    public string Account { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("profession")]
    public string Profession { get; set; } = string.Empty;

    [JsonPropertyName("group")]
    public int Group { get; set; }

    [JsonPropertyName("hasCommanderTag")]
    public bool HasCommanderTag { get; set; }

    // Character attributes (stats)
    [JsonPropertyName("healing")]
    public int HealingPower { get; set; }

    [JsonPropertyName("concentration")]
    public int Concentration { get; set; }

    [JsonPropertyName("condition")]
    public int ConditionDamage { get; set; }

    [JsonPropertyName("toughness")]
    public int Toughness { get; set; }

    [JsonPropertyName("dpsAll")]
    public List<EIDpsStats>? DpsAll { get; set; }

    [JsonPropertyName("dpsTargets")]
    public List<List<EIDpsStats>>? DpsTargets { get; set; }

    [JsonPropertyName("defenses")]
    public List<EIDefenseStats>? Defenses { get; set; }

    // Timestamp pairs in ms (start, end). end = log end if the player stayed dead.
    // Used to derive "dead at phase start" by intersecting these intervals with each
    // phase's start timestamp. Not currently consumed outside the HTCM phase importer.
    [JsonPropertyName("deadCombatTimes")]
    public List<List<int>>? DeadCombatTimes { get; set; }

    [JsonPropertyName("downCombatTimes")]
    public List<List<int>>? DownCombatTimes { get; set; }

    [JsonPropertyName("dcCombatTimes")]
    public List<List<int>>? DcCombatTimes { get; set; }

    [JsonPropertyName("support")]
    public List<EISupportStats>? Support { get; set; }

    [JsonPropertyName("statsAll")]
    public List<EIStatsAll>? StatsAll { get; set; }

    [JsonPropertyName("squadBuffs")]
    public List<EISquadBuff>? SquadBuffs { get; set; }

    [JsonPropertyName("groupBuffs")]
    public List<EISquadBuff>? GroupBuffs { get; set; }

    // Boons the player had on themselves, on "Phase active duration" basis (excludes
    // dead/down time). This is the field behind the HTML report's Buffs → Boons → Uptime
    // tab. Inner buffData shape differs from SquadBuffs/GroupBuffs (dict-valued source
    // breakdowns instead of scalar generation), so a separate slim DTO is used.
    [JsonPropertyName("buffUptimesActive")]
    public List<EIBuffUptime>? BuffUptimesActive { get; set; }

    [JsonPropertyName("extHealingStats")]
    public JsonElement? ExtHealingStats { get; set; }
}

public class EIDpsStats
{
    [JsonPropertyName("dps")]
    public int Dps { get; set; }

    [JsonPropertyName("damage")]
    public long Damage { get; set; }

    [JsonPropertyName("condiDps")]
    public int CondiDps { get; set; }

    [JsonPropertyName("condiDamage")]
    public long CondiDamage { get; set; }

    [JsonPropertyName("powerDps")]
    public int PowerDps { get; set; }

    [JsonPropertyName("powerDamage")]
    public long PowerDamage { get; set; }

    [JsonPropertyName("breakbarDamage")]
    public decimal BreakbarDamage { get; set; }
}

public class EIDefenseStats
{
    [JsonPropertyName("damageTaken")]
    public long DamageTaken { get; set; }

    [JsonPropertyName("blockedCount")]
    public int BlockedCount { get; set; }

    [JsonPropertyName("evadedCount")]
    public int EvadedCount { get; set; }

    [JsonPropertyName("dodgeCount")]
    public int DodgeCount { get; set; }

    [JsonPropertyName("deadCount")]
    public int DeadCount { get; set; }

    [JsonPropertyName("deadDuration")]
    public decimal DeadDuration { get; set; }

    [JsonPropertyName("downCount")]
    public int DownCount { get; set; }

    [JsonPropertyName("downDuration")]
    public decimal DownDuration { get; set; }
}

public class EISupportStats
{
    [JsonPropertyName("resurrects")]
    public int Resurrects { get; set; }

    [JsonPropertyName("resurrectTime")]
    public decimal ResurrectTime { get; set; }

    [JsonPropertyName("condiCleanse")]
    public int CondiCleanse { get; set; }

    [JsonPropertyName("condiCleanseTime")]
    public decimal CondiCleanseTime { get; set; }

    [JsonPropertyName("condiCleanseSelf")]
    public int CondiCleanseSelf { get; set; }

    [JsonPropertyName("boonStrips")]
    public int BoonStrips { get; set; }
}

public class EIStatsAll
{
    [JsonPropertyName("killed")]
    public int Killed { get; set; }

    [JsonPropertyName("downed")]
    public int Downed { get; set; }

    [JsonPropertyName("wasted")]
    public int Wasted { get; set; }

    [JsonPropertyName("timeWasted")]
    public decimal? TimeWasted { get; set; }

    [JsonPropertyName("saved")]
    public int Saved { get; set; }

    [JsonPropertyName("timeSaved")]
    public decimal? TimeSaved { get; set; }

    [JsonPropertyName("stackDist")]
    public decimal? StackDist { get; set; }
}

public class EIMechanic
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icd")]
    public int Icd { get; set; }

    [JsonPropertyName("mechanicsData")]
    public List<EIMechanicData>? MechanicsData { get; set; }
}

public class EIMechanicData
{
    [JsonPropertyName("time")]
    public int Time { get; set; }

    [JsonPropertyName("actor")]
    public string? Actor { get; set; }
}

public class EISquadBuff
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("buffData")]
    public List<EIBuffData>? BuffData { get; set; }
}

public class EIBuffData
{
    [JsonPropertyName("generation")]
    public decimal Generation { get; set; }

    [JsonPropertyName("overstack")]
    public decimal Overstack { get; set; }

    [JsonPropertyName("wasted")]
    public decimal Wasted { get; set; }

    [JsonPropertyName("unknownExtended")]
    public decimal UnknownExtended { get; set; }

    [JsonPropertyName("byExtension")]
    public decimal ByExtension { get; set; }

    [JsonPropertyName("extended")]
    public decimal Extended { get; set; }

    [JsonPropertyName("uptime")]
    public decimal Uptime { get; set; }
}

public class EIBuffUptime
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("buffData")]
    public List<EIBuffUptimeData>? BuffData { get; set; }
}

public class EIBuffUptimeData
{
    // For duration buffs (boons): % uptime 0-100. For STACKING buffs (Might,
    // Debilitated, etc.) this is the average stack count over the phase's active
    // duration — NOT a percentage. Use Presence below for the true uptime % on
    // stacking buffs.
    [JsonPropertyName("uptime")]
    public decimal Uptime { get; set; }

    // For stacking buffs: % of phase active time the buff was up at any stack
    // count (0-100). For duration buffs Presence equals Uptime. Used to read
    // Debilitated's actual uptime % since Uptime alone gives average stacks.
    [JsonPropertyName("presence")]
    public decimal Presence { get; set; }
}

public class EIPhase
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }

    [JsonPropertyName("targets")]
    public List<int>? Targets { get; set; }

    public int Duration => End - Start;
}

public class EITarget
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("finalHealth")]
    public long FinalHealth { get; set; }

    [JsonPropertyName("healthPercentBurned")]
    public decimal HealthPercentBurned { get; set; }

    [JsonPropertyName("totalHealth")]
    public long TotalHealth { get; set; }
}

/// <summary>
/// Well-known buff IDs from GW2
/// </summary>
public static class GW2BuffIds
{
    public const long Quickness = 1187;
    public const long Alacrity = 30328;
    public const long Might = 740;
    public const long Fury = 725;
    public const long Protection = 717;
    public const long Regeneration = 718;
    public const long Vigor = 726;
    public const long Swiftness = 719;
    public const long Stability = 1122;
    public const long Aegis = 743;
    public const long Resistance = 26980;
    public const long Resolution = 873;
}
