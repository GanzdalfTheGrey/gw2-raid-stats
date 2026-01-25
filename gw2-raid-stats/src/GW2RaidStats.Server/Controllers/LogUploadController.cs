using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using GW2RaidStats.Infrastructure.Configuration;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/logs")]
public class LogUploadController : ControllerBase
{
    private readonly StorageOptions _storageOptions;
    private readonly ILogger<LogUploadController> _logger;

    private static readonly string[] SupportedExtensions = [".zevtc", ".evtc"];
    private const long MaxFileSize = 50 * 1024 * 1024; // 50 MB

    public LogUploadController(
        IOptions<StorageOptions> storageOptions,
        ILogger<LogUploadController> logger)
    {
        _storageOptions = storageOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Upload raw .zevtc/.evtc log files for processing
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(500 * 1024 * 1024)] // 500 MB total for multiple files
    public async Task<ActionResult<LogUploadResponse>> UploadLogs(
        [FromForm] List<IFormFile> files,
        CancellationToken ct)
    {
        if (files == null || files.Count == 0)
        {
            return BadRequest(new LogUploadResponse(0, 0, ["No files provided"]));
        }

        _storageOptions.EnsureDirectoriesExist();

        var accepted = 0;
        var rejected = 0;
        var errors = new List<string>();

        foreach (var file in files)
        {
            try
            {
                // Validate file
                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (!SupportedExtensions.Contains(extension))
                {
                    errors.Add($"{file.FileName}: Unsupported file type. Use .zevtc or .evtc files.");
                    rejected++;
                    continue;
                }

                if (file.Length > MaxFileSize)
                {
                    errors.Add($"{file.FileName}: File too large (max {MaxFileSize / 1024 / 1024} MB)");
                    rejected++;
                    continue;
                }

                if (file.Length == 0)
                {
                    errors.Add($"{file.FileName}: Empty file");
                    rejected++;
                    continue;
                }

                // Generate unique filename to avoid collisions
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var uniqueId = Guid.NewGuid().ToString("N")[..8];
                var safeFileName = SanitizeFileName(file.FileName);
                var destFileName = $"{timestamp}_{uniqueId}_{safeFileName}";
                var destPath = Path.Combine(_storageOptions.PendingPath, destFileName);

                // Save file
                await using var stream = new FileStream(destPath, FileMode.Create);
                await file.CopyToAsync(stream, ct);

                _logger.LogInformation("Accepted file for processing: {FileName} -> {DestPath}", file.FileName, destFileName);
                accepted++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing upload: {FileName}", file.FileName);
                errors.Add($"{file.FileName}: Upload failed - {ex.Message}");
                rejected++;
            }
        }

        return Ok(new LogUploadResponse(accepted, rejected, errors));
    }

    /// <summary>
    /// Get the current processing queue status
    /// </summary>
    [HttpGet("queue/status")]
    public ActionResult<QueueStatusResponse> GetQueueStatus()
    {
        try
        {
            _storageOptions.EnsureDirectoriesExist();

            var pendingFiles = Directory.GetFiles(_storageOptions.PendingPath)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            var processingFiles = Directory.GetFiles(_storageOptions.ProcessingPath)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            var failedFiles = Directory.GetFiles(_storageOptions.FailedPath)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            return Ok(new QueueStatusResponse(
                pendingFiles.Count,
                processingFiles.Count,
                failedFiles.Count,
                pendingFiles.Select(Path.GetFileName).ToList()!,
                processingFiles.Select(Path.GetFileName).ToList()!
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting queue status");
            return Ok(new QueueStatusResponse(0, 0, 0, [], []));
        }
    }

    /// <summary>
    /// Scan a server directory for log files and queue them for processing
    /// </summary>
    [HttpPost("scan-directory")]
    public ActionResult<DirectoryScanResponse> ScanDirectory([FromBody] DirectoryScanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceDirectory))
        {
            return BadRequest(new DirectoryScanResponse(0, 0, 0, 1, "Source directory is required"));
        }

        if (!Directory.Exists(request.SourceDirectory))
        {
            return BadRequest(new DirectoryScanResponse(0, 0, 0, 1, $"Directory not found: {request.SourceDirectory}"));
        }

        try
        {
            _storageOptions.EnsureDirectoriesExist();

            var searchOption = request.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(request.SourceDirectory, "*.*", searchOption)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            var found = files.Count;
            var queued = 0;
            var skipped = 0;
            var failed = 0;

            foreach (var file in files)
            {
                try
                {
                    var fileName = Path.GetFileName(file);
                    var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    var uniqueId = Guid.NewGuid().ToString("N")[..8];
                    var destFileName = $"{timestamp}_{uniqueId}_{fileName}";
                    var destPath = Path.Combine(_storageOptions.PendingPath, destFileName);

                    // Check if file with same name already exists in pending/processing/failed
                    var existsInQueue = Directory.GetFiles(_storageOptions.PendingPath, $"*_{fileName}").Any() ||
                                        Directory.GetFiles(_storageOptions.ProcessingPath, $"*_{fileName}").Any();

                    if (existsInQueue)
                    {
                        skipped++;
                        continue;
                    }

                    // Copy file to pending folder
                    System.IO.File.Copy(file, destPath);
                    queued++;

                    _logger.LogInformation("Queued file from scan: {Source} -> {Dest}", file, destFileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error copying file: {File}", file);
                    failed++;
                }
            }

            _logger.LogInformation("Directory scan complete: {Found} found, {Queued} queued, {Skipped} skipped, {Failed} failed",
                found, queued, skipped, failed);

            return Ok(new DirectoryScanResponse(found, queued, skipped, failed, null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning directory: {Directory}", request.SourceDirectory);
            return Ok(new DirectoryScanResponse(0, 0, 0, 1, ex.Message));
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        // Remove any path components and invalid characters
        var name = Path.GetFileName(fileName);
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(name.Where(c => !invalidChars.Contains(c)).ToArray());
    }
}

public record DirectoryScanRequest
{
    public required string SourceDirectory { get; init; }
    public bool Recursive { get; init; } = true;
}

public record DirectoryScanResponse(
    int Found,
    int Queued,
    int Skipped,
    int Failed,
    string? Error
);

public record LogUploadResponse(
    int Accepted,
    int Rejected,
    List<string> Errors
);

public record QueueStatusResponse(
    int PendingCount,
    int ProcessingCount,
    int FailedCount,
    List<string> PendingFiles,
    List<string> ProcessingFiles
);
