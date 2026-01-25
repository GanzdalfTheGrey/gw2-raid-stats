namespace GW2RaidStats.Infrastructure.Configuration;

public class StorageOptions
{
    public const string SectionName = "Storage";

    public string BasePath { get; set; } = "./data/gw2-logs";
    public string PendingFolder { get; set; } = "queue/pending";
    public string ProcessingFolder { get; set; } = "queue/processing";
    public string FailedFolder { get; set; } = "queue/failed";
    public string EncountersFolder { get; set; } = "encounters";

    public string PendingPath => Path.Combine(BasePath, PendingFolder);
    public string ProcessingPath => Path.Combine(BasePath, ProcessingFolder);
    public string FailedPath => Path.Combine(BasePath, FailedFolder);
    public string EncountersPath => Path.Combine(BasePath, EncountersFolder);

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(PendingPath);
        Directory.CreateDirectory(ProcessingPath);
        Directory.CreateDirectory(FailedPath);
        Directory.CreateDirectory(EncountersPath);
    }

    public string GetEncounterPath(DateTimeOffset encounterTime, Guid encounterId)
    {
        var relativePath = Path.Combine(
            encounterTime.Year.ToString(),
            encounterTime.Month.ToString("D2"),
            encounterId.ToString());
        return relativePath;
    }

    public string GetFullEncounterPath(string relativePath)
    {
        return Path.Combine(EncountersPath, relativePath);
    }
}
