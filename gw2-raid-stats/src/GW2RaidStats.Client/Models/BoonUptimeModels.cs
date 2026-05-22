namespace GW2RaidStats.Client.Models;

// Client mirrors of BoonCoverageService records — shared by the BoonUptimePanel
// component and the pages that host it (Home, Sessions).

public record EncounterBoonCoverage(
    Guid EncounterId,
    string BossName,
    bool IsCM,
    bool Success,
    int DurationMs,
    DateTimeOffset EncounterTime,
    List<SubBoonCoverage> Subs,
    List<BoonGeneratorInfo> Generators,
    decimal? SquadAvgDistance);

public record SubBoonCoverage(
    int SubGroup,
    int PlayerCount,
    BoonSet Average,
    decimal? AvgDistance,
    List<PlayerBoonRow> Players);

public record PlayerBoonRow(
    string AccountName,
    string Profession,
    BoonSet Boons,
    decimal? Distance);

// Quickness/Alacrity/Fury/Regeneration/Protection/Swiftness are % uptime 0-100;
// Might is average stacks 0-25.
public record BoonSet(
    decimal? Quickness,
    decimal? Alacrity,
    decimal? Might,
    decimal? Fury,
    decimal? Regeneration,
    decimal? Protection,
    decimal? Swiftness);

public record BoonGeneratorInfo(
    string AccountName,
    string Profession,
    string Role,
    int SubGroup,
    string Boon,
    decimal? GenerationPct);
