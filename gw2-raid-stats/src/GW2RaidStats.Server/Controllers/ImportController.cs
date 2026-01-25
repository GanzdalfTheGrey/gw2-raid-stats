using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Core.EliteInsights;
using GW2RaidStats.Infrastructure.Services.Import;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin/[controller]")]
public class ImportController : ControllerBase
{
    private readonly LogImportService _logImportService;
    private readonly BulkImportService _bulkImportService;
    private readonly ILogger<ImportController> _logger;

    public ImportController(
        LogImportService logImportService,
        BulkImportService bulkImportService,
        ILogger<ImportController> logger)
    {
        _logImportService = logImportService;
        _bulkImportService = bulkImportService;
        _logger = logger;
    }

    /// <summary>
    /// Upload one or more Elite Insights JSON log files
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(100_000_000)] // 100MB limit
    public async Task<ActionResult<List<ImportResult>>> UploadLogs(
        List<IFormFile> files,
        CancellationToken ct)
    {
        if (files.Count == 0)
        {
            return BadRequest("No files provided");
        }

        var results = new List<ImportResult>();

        foreach (var file in files)
        {
            if (!file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ImportResult(false, null, file.FileName, null, "Not a JSON file", false));
                continue;
            }

            await using var stream = file.OpenReadStream();
            var result = await _logImportService.ImportLogAsync(stream, file.FileName, ct);
            results.Add(result);
        }

        return Ok(results);
    }

    /// <summary>
    /// Import all JSON files from a directory on the server
    /// </summary>
    [HttpPost("directory")]
    public async Task<ActionResult<BulkImportResult>> ImportDirectory(
        [FromBody] DirectoryImportRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SourceDirectory))
        {
            return BadRequest("Source directory is required");
        }

        if (!Directory.Exists(request.SourceDirectory))
        {
            return BadRequest($"Directory not found: {request.SourceDirectory}");
        }

        _logger.LogInformation(
            "Starting directory import from {SourceDir} with parallelism {Parallelism}",
            request.SourceDirectory,
            request.MaxParallelism);

        var result = await _bulkImportService.ImportDirectoryAsync(
            request.SourceDirectory,
            request.CompletedDirectory,
            request.FailedDirectory,
            request.MaxParallelism,
            null, // TODO: Add SignalR progress reporting
            ct
        );

        return Ok(result);
    }
}

public record DirectoryImportRequest
{
    public required string SourceDirectory { get; init; }
    public string? CompletedDirectory { get; init; }
    public string? FailedDirectory { get; init; }
    public int MaxParallelism { get; init; } = 8;
}
