using System.Text.RegularExpressions;
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

                // Save under a temp name, then rename into place so the processor never sees a partial file
                var tempPath = destPath + ".uploading";
                await using (var stream = new FileStream(tempPath, FileMode.Create))
                {
                    await file.CopyToAsync(stream, ct);
                }
                System.IO.File.Move(tempPath, destPath);

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
    /// List failed logs with the reason recorded in their .error.txt, newest first
    /// </summary>
    [HttpGet("queue/failed")]
    public ActionResult<List<FailedLogResponse>> GetFailedLogs()
    {
        _storageOptions.EnsureDirectoriesExist();

        var failed = Directory.GetFiles(_storageOptions.FailedPath)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f =>
            {
                var errorPath = f + ".error.txt";
                var error = System.IO.File.Exists(errorPath)
                    ? ReadFailureReason(System.IO.File.ReadAllText(errorPath))
                    : "(no error file)";
                var failedAt = System.IO.File.GetLastWriteTimeUtc(System.IO.File.Exists(errorPath) ? errorPath : f);
                return new FailedLogResponse(Path.GetFileName(f), failedAt, error);
            })
            .OrderByDescending(f => f.FailedAt)
            .ToList();

        return Ok(failed);
    }

    /// <summary>
    /// Move a failed log back to pending under its original name and delete its .error.txt
    /// </summary>
    [HttpPost("queue/failed/retry")]
    public ActionResult RetryFailedLog([FromBody] RetryFailedLogRequest request)
    {
        var fileName = Path.GetFileName(request.FileName);
        if (fileName != request.FileName)
        {
            return BadRequest("Invalid file name");
        }

        var failedPath = Path.Combine(_storageOptions.FailedPath, fileName);
        if (!System.IO.File.Exists(failedPath))
        {
            return NotFound();
        }

        return RequeueFailedLog(failedPath) ? Ok() : Conflict($"{fileName} is already pending");
    }

    /// <summary>
    /// Move every failed log back to pending and delete their .error.txt files
    /// </summary>
    [HttpPost("queue/failed/retry-all")]
    public ActionResult<RetryAllFailedResponse> RetryAllFailedLogs()
    {
        var failedFiles = Directory.GetFiles(_storageOptions.FailedPath)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        var requeued = failedFiles.Count(RequeueFailedLog);
        _logger.LogInformation("Retry all: {Requeued} of {Total} failed logs re-queued", requeued, failedFiles.Count);

        return Ok(new RetryAllFailedResponse(requeued, failedFiles.Count - requeued));
    }

    // Returns false when a file with the same name is already pending
    private bool RequeueFailedLog(string failedPath)
    {
        var fileName = Path.GetFileName(failedPath);

        // Failed files are named "{yyyyMMdd-HHmmss}_{original}"; strip it so retries don't stack prefixes
        var match = FailedPrefixRegex.Match(fileName);
        var pendingName = match.Success ? match.Groups[1].Value : fileName;
        var pendingPath = Path.Combine(_storageOptions.PendingPath, pendingName);
        if (System.IO.File.Exists(pendingPath))
        {
            return false;
        }

        System.IO.File.Move(failedPath, pendingPath);
        System.IO.File.Delete(failedPath + ".error.txt");

        _logger.LogInformation("Retrying failed log {FileName} as {PendingName}", fileName, pendingName);
        return true;
    }

    private static readonly Regex FailedPrefixRegex = new(@"^\d{8}-\d{6}_(.+)$");

    // .error.txt is "Error: {reason}\nTimestamp: ...\nOriginal file: ..."; the reason may span lines
    private static string ReadFailureReason(string text)
    {
        var end = text.IndexOf("\nTimestamp: ", StringComparison.Ordinal);
        var reason = end >= 0 ? text[..end] : text;
        return reason.StartsWith("Error: ") ? reason["Error: ".Length..].Trim() : reason.Trim();
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

                    // Copy under a temp name, then rename into place so the processor never sees a partial file
                    var tempPath = destPath + ".uploading";
                    System.IO.File.Copy(file, tempPath);
                    System.IO.File.Move(tempPath, destPath);
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

public record FailedLogResponse(
    string FileName,
    DateTime FailedAt,
    string Error
);

public record RetryFailedLogRequest(string FileName);

public record RetryAllFailedResponse(int Requeued, int AlreadyPending);
