using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GW2RaidStats.Infrastructure.Configuration;
using GW2RaidStats.Processor.Configuration;
using GW2RaidStats.Processor.Services;

namespace GW2RaidStats.Processor.Workers;

public class LogProcessingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StorageOptions _storageOptions;
    private readonly ProcessorOptions _processorOptions;
    private readonly ILogger<LogProcessingWorker> _logger;
    private readonly Channel<string> _fileQueue;

    private static readonly string[] SupportedExtensions = [".zevtc", ".evtc", ".zevtc.zip"];

    public LogProcessingWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<StorageOptions> storageOptions,
        IOptions<ProcessorOptions> processorOptions,
        ILogger<LogProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _storageOptions = storageOptions.Value;
        _processorOptions = processorOptions.Value;
        _logger = logger;
        _fileQueue = Channel.CreateUnbounded<string>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Log Processing Worker starting...");

        // Ensure directories exist
        _storageOptions.EnsureDirectoriesExist();

        _logger.LogInformation("Watching folder: {PendingPath}", _storageOptions.PendingPath);
        _logger.LogInformation("Max concurrent processing: {Max}", _processorOptions.MaxConcurrentProcessing);

        // Start the file watcher
        var watcherTask = WatchForFilesAsync(stoppingToken);

        // Start the processor workers
        var processorTasks = Enumerable
            .Range(0, _processorOptions.MaxConcurrentProcessing)
            .Select(i => ProcessFilesAsync(i, stoppingToken))
            .ToList();

        // Also scan for any existing files on startup
        await ScanExistingFilesAsync(stoppingToken);

        // Wait for all tasks
        await Task.WhenAll([watcherTask, .. processorTasks]);
    }

    private async Task WatchForFilesAsync(CancellationToken ct)
    {
        using var watcher = new FileSystemWatcher(_storageOptions.PendingPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        watcher.Created += (_, e) =>
        {
            if (IsSupportedFile(e.FullPath))
            {
                _logger.LogDebug("New file detected: {FileName}", e.Name);
                _fileQueue.Writer.TryWrite(e.FullPath);
            }
        };

        // Also poll periodically in case FileSystemWatcher misses something
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_processorOptions.PollingIntervalSeconds), ct);
                await ScanExistingFilesAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private Task ScanExistingFilesAsync(CancellationToken ct)
    {
        try
        {
            var files = Directory.GetFiles(_storageOptions.PendingPath)
                .Where(IsSupportedFile)
                .OrderBy(File.GetCreationTimeUtc)
                .ToList();

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                // Only queue if not already queued
                _fileQueue.Writer.TryWrite(file);
            }

            if (files.Count > 0)
            {
                _logger.LogInformation("Found {Count} pending files", files.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning for existing files");
        }

        return Task.CompletedTask;
    }

    private async Task ProcessFilesAsync(int workerId, CancellationToken ct)
    {
        _logger.LogDebug("Worker {WorkerId} starting", workerId);

        var processedPaths = new HashSet<string>();

        await foreach (var filePath in _fileQueue.Reader.ReadAllAsync(ct))
        {
            // Skip if already processed or file doesn't exist
            if (processedPaths.Contains(filePath) || !File.Exists(filePath))
            {
                continue;
            }

            processedPaths.Add(filePath);

            // Small delay to ensure file is fully written
            await Task.Delay(500, ct);

            try
            {
                // Create a new scope for each file processing
                using var scope = _scopeFactory.CreateScope();
                var logProcessor = scope.ServiceProvider.GetRequiredService<LogProcessor>();

                var result = await logProcessor.ProcessFileAsync(filePath, ct);

                if (result.Success)
                {
                    _logger.LogInformation("[Worker {WorkerId}] Processed {FileName}: {EncounterId}",
                        workerId, result.FileName, result.EncounterId);
                }
                else
                {
                    _logger.LogWarning("[Worker {WorkerId}] Failed {FileName}: {Error}",
                        workerId, result.FileName, result.Error);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Worker {WorkerId}] Error processing file", workerId);
            }

            // Clean up processed paths periodically
            if (processedPaths.Count > 1000)
            {
                processedPaths.Clear();
            }
        }
    }

    private static bool IsSupportedFile(string path)
    {
        var fileName = Path.GetFileName(path).ToLowerInvariant();
        return SupportedExtensions.Any(ext => fileName.EndsWith(ext));
    }
}
