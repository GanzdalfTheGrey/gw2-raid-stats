using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Configuration;
using GW2RaidStats.Infrastructure.Database;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly RaidStatsDb _db;
    private readonly StorageOptions _storageOptions;

    public ReportsController(
        RaidStatsDb db,
        IOptions<StorageOptions> storageOptions)
    {
        _db = db;
        _storageOptions = storageOptions.Value;
    }

    /// <summary>
    /// Get the report URL for an encounter
    /// </summary>
    [HttpGet("{encounterId:guid}")]
    public async Task<ActionResult<ReportInfo>> GetReportInfo(Guid encounterId, CancellationToken ct)
    {
        var encounter = await _db.Encounters
            .Where(e => e.Id == encounterId)
            .Select(e => new { e.FilesPath, e.OriginalFilename, e.LogUrl })
            .FirstOrDefaultAsync(ct);

        if (encounter == null)
        {
            return NotFound();
        }

        string? htmlReportUrl = null;
        string? jsonReportUrl = null;
        bool hasLocalReport = false;

        if (!string.IsNullOrEmpty(encounter.FilesPath))
        {
            var htmlPath = Path.Combine(_storageOptions.EncountersPath, encounter.FilesPath, "report.html");
            var jsonPath = Path.Combine(_storageOptions.EncountersPath, encounter.FilesPath, "report.json");

            if (System.IO.File.Exists(htmlPath))
            {
                htmlReportUrl = $"/reports/{encounter.FilesPath}/report.html";
                hasLocalReport = true;
            }

            if (System.IO.File.Exists(jsonPath))
            {
                jsonReportUrl = $"/reports/{encounter.FilesPath}/report.json";
            }
        }

        return Ok(new ReportInfo(
            encounterId,
            htmlReportUrl,
            jsonReportUrl,
            encounter.LogUrl,
            encounter.OriginalFilename,
            hasLocalReport
        ));
    }

    /// <summary>
    /// Download the raw .zevtc log file for an encounter
    /// </summary>
    [HttpGet("{encounterId:guid}/download")]
    public async Task<IActionResult> DownloadLog(Guid encounterId, CancellationToken ct)
    {
        var encounter = await _db.Encounters
            .Where(e => e.Id == encounterId)
            .Select(e => new { e.FilesPath, e.OriginalFilename, e.BossName, e.EncounterTime })
            .FirstOrDefaultAsync(ct);

        if (encounter == null)
        {
            return NotFound();
        }

        if (string.IsNullOrEmpty(encounter.FilesPath))
        {
            return NotFound("No local log file available for this encounter");
        }

        var zevtcPath = Path.Combine(_storageOptions.EncountersPath, encounter.FilesPath, "log.zevtc");
        if (!System.IO.File.Exists(zevtcPath))
        {
            return NotFound("Log file not found on disk");
        }

        // Use original filename if available, otherwise generate one
        var filename = !string.IsNullOrEmpty(encounter.OriginalFilename)
            ? encounter.OriginalFilename
            : $"{encounter.BossName}_{encounter.EncounterTime:yyyyMMdd_HHmmss}.zevtc";

        var stream = new FileStream(zevtcPath, FileMode.Open, FileAccess.Read);
        return File(stream, "application/octet-stream", filename);
    }

    /// <summary>
    /// Download all logs from a session (all encounters on the same day as the most recent log)
    /// </summary>
    [HttpGet("download-session")]
    public async Task<IActionResult> DownloadSessionLogs(CancellationToken ct)
    {
        // Get the most recent encounter to determine the session date
        var latestEncounter = await _db.Encounters
            .OrderByDescending(e => e.EncounterTime)
            .FirstOrDefaultAsync(ct);

        if (latestEncounter == null)
        {
            return NotFound("No encounters found");
        }

        // Get all encounters from the same calendar date (session)
        var sessionDate = latestEncounter.EncounterTime.Date;
        var encounterOffset = latestEncounter.EncounterTime.Offset;
        var sessionStart = new DateTimeOffset(sessionDate, encounterOffset);
        var sessionEnd = sessionStart.AddDays(1);

        var sessionEncounters = await _db.Encounters
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime < sessionEnd)
            .Where(e => e.FilesPath != null)
            .OrderBy(e => e.EncounterTime)
            .Select(e => new { e.FilesPath, e.OriginalFilename, e.BossName, e.EncounterTime })
            .ToListAsync(ct);

        if (sessionEncounters.Count == 0)
        {
            return NotFound("No downloadable logs found for this session");
        }

        // Create a ZIP file in memory
        var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var encounter in sessionEncounters)
            {
                var zevtcPath = Path.Combine(_storageOptions.EncountersPath, encounter.FilesPath!, "log.zevtc");
                if (System.IO.File.Exists(zevtcPath))
                {
                    var filename = !string.IsNullOrEmpty(encounter.OriginalFilename)
                        ? encounter.OriginalFilename
                        : $"{encounter.BossName}_{encounter.EncounterTime:yyyyMMdd_HHmmss}.zevtc";

                    var entry = archive.CreateEntry(filename, CompressionLevel.NoCompression);
                    using var entryStream = entry.Open();
                    using var fileStream = System.IO.File.OpenRead(zevtcPath);
                    fileStream.CopyTo(entryStream);
                }
            }
        }

        memoryStream.Position = 0;
        var zipFilename = $"raid_logs_{sessionDate:yyyy-MM-dd}.zip";
        return File(memoryStream, "application/zip", zipFilename);
    }

    /// <summary>
    /// Redirect to the HTML report for an encounter (for direct linking)
    /// </summary>
    [HttpGet("{encounterId:guid}/view")]
    public async Task<IActionResult> ViewReport(Guid encounterId, CancellationToken ct)
    {
        var encounter = await _db.Encounters
            .Where(e => e.Id == encounterId)
            .Select(e => new { e.FilesPath, e.LogUrl })
            .FirstOrDefaultAsync(ct);

        if (encounter == null)
        {
            return NotFound();
        }

        // Prefer local report
        if (!string.IsNullOrEmpty(encounter.FilesPath))
        {
            var htmlPath = Path.Combine(_storageOptions.EncountersPath, encounter.FilesPath, "report.html");
            if (System.IO.File.Exists(htmlPath))
            {
                return Redirect($"/reports/{encounter.FilesPath}/report.html");
            }
        }

        // Fall back to external log URL (dps.report)
        if (!string.IsNullOrEmpty(encounter.LogUrl))
        {
            return Redirect(encounter.LogUrl);
        }

        return NotFound("No report available for this encounter");
    }
}

public record ReportInfo(
    Guid EncounterId,
    string? HtmlReportUrl,
    string? JsonReportUrl,
    string? ExternalLogUrl,
    string? OriginalFilename,
    bool HasLocalReport
);
