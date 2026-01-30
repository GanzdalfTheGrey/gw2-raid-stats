using System.Collections.Concurrent;
using System.Diagnostics;
using LinqToDB.Data;
using GW2RaidStats.Core.EliteInsights;
using GW2RaidStats.Infrastructure.Database;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.Infrastructure.Services.Import;

public class BulkImportService
{
    private readonly Func<RaidStatsDb> _dbFactory;
    private readonly IncludedPlayerService _includedPlayerService;
    private readonly ILogger<BulkImportService> _logger;

    public BulkImportService(Func<RaidStatsDb> dbFactory, IncludedPlayerService includedPlayerService, ILogger<BulkImportService> logger)
    {
        _dbFactory = dbFactory;
        _includedPlayerService = includedPlayerService;
        _logger = logger;
    }

    public async Task<BulkImportResult> ImportDirectoryAsync(
        string sourceDir,
        string? completedDir = null,
        string? failedDir = null,
        int maxParallelism = 8,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // Validate source directory
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
        }

        // Create output directories if specified
        if (!string.IsNullOrEmpty(completedDir))
            Directory.CreateDirectory(completedDir);
        if (!string.IsNullOrEmpty(failedDir))
            Directory.CreateDirectory(failedDir);

        // Get all JSON files
        var files = Directory.GetFiles(sourceDir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f)
            .ToList();

        _logger.LogInformation("Found {Count} JSON files in {Directory}", files.Count, sourceDir);

        if (files.Count == 0)
        {
            return new BulkImportResult(0, 0, 0, 0, stopwatch.Elapsed, []);
        }

        // Thread-safe collections for results
        var results = new ConcurrentBag<ImportResult>();
        var imported = 0;
        var duplicates = 0;
        var failed = 0;
        var processed = 0;

        // Process files in parallel
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(files, options, async (filePath, token) =>
        {
            var fileName = Path.GetFileName(filePath);
            ImportResult result;

            try
            {
                // Each parallel task gets its own DB connection
                using var db = _dbFactory();
                var recordNotificationService = new RecordNotificationService(db, _includedPlayerService);
                var importService = new LogImportService(db, recordNotificationService);

                result = await importService.ImportLogFromFileAsync(filePath, token);
                results.Add(result);

                if (result.Success)
                {
                    if (result.WasDuplicate)
                    {
                        Interlocked.Increment(ref duplicates);
                        _logger.LogDebug("Duplicate: {FileName}", fileName);
                    }
                    else
                    {
                        Interlocked.Increment(ref imported);
                        _logger.LogDebug("Imported: {FileName} - {BossName}", fileName, result.BossName);
                    }

                    // Move to completed directory
                    if (!string.IsNullOrEmpty(completedDir))
                    {
                        MoveFile(filePath, completedDir, fileName);
                    }
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning("Failed: {FileName} - {Error}", fileName, result.Error);

                    // Move to failed directory
                    if (!string.IsNullOrEmpty(failedDir))
                    {
                        MoveFile(filePath, failedDir, fileName);
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                result = new ImportResult(false, null, fileName, null, ex.Message, false);
                results.Add(result);
                _logger.LogError(ex, "Error processing {FileName}", fileName);

                if (!string.IsNullOrEmpty(failedDir))
                {
                    MoveFile(filePath, failedDir, fileName);
                }
            }

            // Report progress
            var currentProcessed = Interlocked.Increment(ref processed);
            progress?.Report(new ImportProgress(
                currentProcessed,
                files.Count,
                fileName,
                imported,
                duplicates,
                failed
            ));
        });

        stopwatch.Stop();

        var finalResult = new BulkImportResult(
            files.Count,
            imported,
            duplicates,
            failed,
            stopwatch.Elapsed,
            results.OrderBy(r => r.FileName).ToList()
        );

        _logger.LogInformation(
            "Import complete: {Total} files processed in {Duration:F1}s - Imported: {Imported}, Duplicates: {Duplicates}, Failed: {Failed}",
            finalResult.TotalFiles,
            finalResult.Duration.TotalSeconds,
            finalResult.Imported,
            finalResult.Duplicates,
            finalResult.Failed
        );

        return finalResult;
    }

    private static void MoveFile(string sourcePath, string destDir, string fileName)
    {
        try
        {
            var destPath = Path.Combine(destDir, fileName);

            // Handle duplicate file names by appending a number
            if (File.Exists(destPath))
            {
                var baseName = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                var counter = 1;

                do
                {
                    destPath = Path.Combine(destDir, $"{baseName}_{counter}{ext}");
                    counter++;
                } while (File.Exists(destPath));
            }

            File.Move(sourcePath, destPath);
        }
        catch
        {
            // Ignore move errors - file might be locked
        }
    }
}
