using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GW2RaidStats.Infrastructure.Configuration;
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

        try
        {
            // Move to processing folder
            File.Move(filePath, processingPath, overwrite: true);
            _logger.LogInformation("Processing {FileName}", fileName);

            // Create temp directory for GW2EI output
            var tempOutputDir = Path.Combine(Path.GetTempPath(), $"gw2ei-{Guid.NewGuid()}");

            try
            {
                // Run GW2EI
                var gw2EiResult = await _gw2EiRunner.ProcessLogAsync(processingPath, tempOutputDir, ct);

                if (!gw2EiResult.Success || gw2EiResult.JsonPath == null)
                {
                    _logger.LogError("GW2EI failed for {FileName}: {Error}", fileName, gw2EiResult.Error);
                    await MoveToFailedAsync(processingPath, fileName, gw2EiResult.Error ?? "GW2EI parsing failed");
                    return new ProcessResult(false, fileName, null, gw2EiResult.Error);
                }

                // Import the JSON to database
                await using var jsonStream = File.OpenRead(gw2EiResult.JsonPath);
                var importResult = await _importService.ImportLogAsync(jsonStream, fileName, ct);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing {FileName}", fileName);

            // Try to move to failed
            if (File.Exists(processingPath))
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
