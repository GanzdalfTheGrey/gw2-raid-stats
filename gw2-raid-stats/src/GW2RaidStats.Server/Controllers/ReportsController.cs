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
