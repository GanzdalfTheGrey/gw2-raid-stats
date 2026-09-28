using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GW2RaidStats.Core;
using GW2RaidStats.Infrastructure.Configuration;
using GW2RaidStats.Infrastructure.Database.Entities;
using GW2RaidStats.Infrastructure.Services.Import;
using GW2RaidStats.Infrastructure.Database;
using LinqToDB;
using LinqToDB.Async;

namespace GW2RaidStats.Processor.Services;

public class LogProcessor
{
    private readonly Gw2EiRunner _gw2EiRunner;
    private readonly LogImportService _importService;
    private readonly RaidStatsDb _db;
    private readonly StorageOptions _storageOptions;
    private readonly ILogger<LogProcessor> _logger;

    public LogProcessor(
        Gw2EiRunner gw2EiRunner,
        LogImportService importService,
        RaidStatsDb db,
        IOptions<StorageOptions> storageOptions,
        ILogger<LogProcessor> logger)
    {
        _gw2EiRunner = gw2EiRunner;
        _importService = importService;
        _db = db;
        _storageOptions = storageOptions.Value;
        _logger = logger;
    }

    public async Task<ProcessResult> ProcessFileAsync(string filePath, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(filePath);
        var processingPath = Path.Combine(_storageOptions.ProcessingPath, fileName);
        var claimed = false;

        try
        {
            // Move to processing folder
            File.Move(filePath, processingPath, overwrite: true);
            claimed = true;
            _logger.LogInformation("Processing {FileName}", fileName);

            // Create temp directory for GW2EI output
            var tempOutputDir = Path.Combine(Path.GetTempPath(), $"gw2ei-{Guid.NewGuid()}");

            try
            {
                // Run GW2EI
                var gw2EiResult = await _gw2EiRunner.ProcessLogAsync(processingPath, tempOutputDir, ct);

                if (gw2EiResult.TooShortDurationMs is { } tooShortMs)
                {
                    return await RecordTooShortAsync(processingPath, fileName, tooShortMs, gw2EiResult.Error!, ct);
                }

                if (!gw2EiResult.Success || gw2EiResult.JsonPath == null)
                {
                    _logger.LogError("GW2EI failed for {FileName}: {Error}", fileName, gw2EiResult.Error);
                    await MoveToFailedAsync(processingPath, fileName, gw2EiResult.Error ?? "GW2EI parsing failed");
                    return new ProcessResult(false, fileName, null, gw2EiResult.Error);
                }

                // Import the JSON to database
                await using var jsonStream = File.OpenRead(gw2EiResult.JsonPath);
                var importResult = await _importService.ImportLogAsync(jsonStream, fileName, ct);

                if (importResult.WasSkipped)
                {
                    _logger.LogInformation("Deleting skipped {FileName}: {Reason}", fileName, importResult.Error);
                    File.Delete(processingPath);
                    return new ProcessResult(true, fileName, null, importResult.Error);
                }

                if (!importResult.Success)
                {
                    _logger.LogError("Import failed for {FileName}: {Error}", fileName, importResult.Error);
                    await MoveToFailedAsync(processingPath, fileName, importResult.Error ?? "Import failed");
                    return new ProcessResult(false, fileName, null, importResult.Error);
                }

                if (importResult.WasDuplicate)
                {
                    _logger.LogInformation("Skipping duplicate {FileName}", fileName);
                    // Clean up - don't keep duplicates
                    File.Delete(processingPath);
                    return new ProcessResult(true, fileName, importResult.EncounterId, "Duplicate");
                }

                // Get encounter time from database for folder organization
                var encounter = await _db.Encounters
                    .FirstOrDefaultAsync(e => e.Id == importResult.EncounterId, ct);

                if (encounter == null)
                {
                    _logger.LogError("Encounter not found after import: {EncounterId}", importResult.EncounterId);
                    await MoveToFailedAsync(processingPath, fileName, "Encounter not found after import");
                    return new ProcessResult(false, fileName, null, "Encounter not found");
                }

                // Create encounter folder and move files
                var relativePath = _storageOptions.GetEncounterPath(encounter.EncounterTime, encounter.Id);
                var encounterDir = _storageOptions.GetFullEncounterPath(relativePath);
                Directory.CreateDirectory(encounterDir);

                // Move/copy files to final location
                var finalZevtcPath = Path.Combine(encounterDir, "log.zevtc");
                var finalJsonPath = Path.Combine(encounterDir, "report.json");
                var finalHtmlPath = Path.Combine(encounterDir, "report.html");

                File.Move(processingPath, finalZevtcPath, overwrite: true);
                File.Move(gw2EiResult.JsonPath, finalJsonPath, overwrite: true);

                if (gw2EiResult.HtmlPath != null && File.Exists(gw2EiResult.HtmlPath))
                {
                    File.Move(gw2EiResult.HtmlPath, finalHtmlPath, overwrite: true);
                }

                // Update encounter with file paths
                await _db.Encounters
                    .Where(e => e.Id == encounter.Id)
                    .Set(e => e.FilesPath, relativePath)
                    .Set(e => e.OriginalFilename, fileName)
                    .UpdateAsync(ct);

                _logger.LogInformation("Successfully processed {FileName} -> {EncounterId}", fileName, encounter.Id);

                return new ProcessResult(true, fileName, encounter.Id, null);
            }
            finally
            {
                // Clean up temp directory
                if (Directory.Exists(tempOutputDir))
                {
                    try { Directory.Delete(tempOutputDir, recursive: true); }
                    catch { /* ignore cleanup errors */ }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down - leave the file in processing/ so startup recovery re-queues it
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing {FileName}", fileName);

            // Try to move to failed - only a file this call claimed, never one another run owns
            if (claimed && File.Exists(processingPath))
            {
                await MoveToFailedAsync(processingPath, fileName, ex.Message);
            }
            else if (File.Exists(filePath))
            {
                await MoveToFailedAsync(filePath, fileName, ex.Message);
            }

            return new ProcessResult(false, fileName, null, ex.Message);
        }
    }

    /// <summary>
    /// EI refuses logs under 2.2s, so there is no report to import. Record the pull as a failed
    /// encounter from the raw log header (boss + start time) and drop the log itself.
    /// </summary>
    private async Task<ProcessResult> RecordTooShortAsync(
        string processingPath, string fileName, int durationMs, string eiStatus, CancellationToken ct)
    {
        var header = EvtcHeaderReader.Read(processingPath);
        var boss = WingMapping.AllBosses.FirstOrDefault(b => b.TriggerId == header.TriggerId);

        if (boss == null)
        {
            var error = $"{eiStatus} - not recorded, trigger {header.TriggerId} is not a known raid boss";
            _logger.LogWarning("Too-short log {FileName}: {Error}", fileName, error);
            await MoveToFailedAsync(processingPath, fileName, error);
            return new ProcessResult(false, fileName, null, error);
        }

        // Dedupe on the raw log bytes (json_hash holds 64-char SHA-256 hex for both kinds)
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(processingPath, ct)));
        var existingId = await _db.Encounters
            .Where(e => e.JsonHash == hash)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(ct);

        if (existingId != null)
        {
            _logger.LogInformation("Skipping duplicate too-short log {FileName}", fileName);
            File.Delete(processingPath);
            return new ProcessResult(true, fileName, existingId, "Duplicate");
        }

        // EI decides CM from the parsed fight, which we don't have - inherit it from the previous
        // log of the same boss, since a too-short pull is almost always a reset mid-session
        var previous = await _db.Encounters
            .Where(e => e.TriggerId == header.TriggerId && e.EncounterTime < header.LogStart)
            .OrderByDescending(e => e.EncounterTime)
            .Select(e => new { e.IsCM, e.IsLegendaryCM })
            .FirstOrDefaultAsync(ct);

        var encounter = new EncounterEntity
        {
            Id = Guid.NewGuid(),
            TriggerId = header.TriggerId,
            BossName = boss.Name,
            Wing = WingMapping.GetWing(header.TriggerId),
            IsCM = previous?.IsCM ?? false,
            IsLegendaryCM = previous?.IsLegendaryCM ?? false,
            Success = false,
            DurationMs = durationMs,
            EncounterTime = header.LogStart,
            JsonHash = hash,
            OriginalFilename = fileName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _db.InsertAsync(encounter, token: ct);
        File.Delete(processingPath);

        _logger.LogInformation("Recorded too-short log {FileName} ({DurationMs} ms, {Boss}) as failed encounter {EncounterId}",
            fileName, durationMs, boss.Name, encounter.Id);

        return new ProcessResult(true, fileName, encounter.Id, null);
    }

    private async Task MoveToFailedAsync(string sourcePath, string fileName, string error)
    {
        try
        {
            var failedDir = _storageOptions.FailedPath;
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var failedPath = Path.Combine(failedDir, $"{timestamp}_{fileName}");
            var errorLogPath = Path.Combine(failedDir, $"{timestamp}_{fileName}.error.txt");

            File.Move(sourcePath, failedPath, overwrite: true);
            await File.WriteAllTextAsync(errorLogPath, $"Error: {error}\nTimestamp: {DateTime.UtcNow:O}\nOriginal file: {fileName}");

            _logger.LogWarning("Moved failed file to {FailedPath}", failedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move file to failed folder");
        }
    }
}

public record ProcessResult(
    bool Success,
    string FileName,
    Guid? EncounterId,
    string? Error
);
