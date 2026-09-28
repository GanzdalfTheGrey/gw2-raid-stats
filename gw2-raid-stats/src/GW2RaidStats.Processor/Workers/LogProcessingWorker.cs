using System.Collections.Concurrent;
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
    // Paths currently queued or being processed, shared across workers so a file is only handed out once
    private readonly ConcurrentDictionary<string, byte> _queuedPaths = new();

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

        // Anything left in processing/ was orphaned by a previous run that was killed mid-parse
        RecoverOrphanedFiles();

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

        FileSystemEventHandler onNewFile = (_, e) =>
        {
            if (IsSupportedFile(e.FullPath))
            {
                _logger.LogDebug("New file detected: {FileName}", e.Name);
                Enqueue(e.FullPath);
            }
        };
        watcher.Created += onNewFile;
        // Uploads land as a temp file and are renamed into place once fully written
        watcher.Renamed += (sender, e) => onNewFile(sender, e);

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

                Enqueue(file);
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

        await foreach (var filePath in _fileQueue.Reader.ReadAllAsync(ct))
        {
            if (!File.Exists(filePath))
            {
                _queuedPaths.TryRemove(filePath, out _);
                continue;
            }

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
            finally
            {
                _queuedPaths.TryRemove(filePath, out _);
            }
        }
    }

    private void Enqueue(string filePath)
    {
        if (_queuedPaths.TryAdd(filePath, 0))
        {
            _fileQueue.Writer.TryWrite(filePath);
        }
    }

    private void RecoverOrphanedFiles()
    {
        foreach (var file in Directory.GetFiles(_storageOptions.ProcessingPath))
        {
            if (IsSupportedFile(file))
            {
                File.Move(file, Path.Combine(_storageOptions.PendingPath, Path.GetFileName(file)));
                _logger.LogWarning("Recovered orphaned file {FileName} back to pending", Path.GetFileName(file));
            }
            else
            {
                // Partial GW2EI output from the killed run - would otherwise be picked up as this log's report
                File.Delete(file);
            }
        }
    }

    private static bool IsSupportedFile(string path)
    {
        var fileName = Path.GetFileName(path).ToLowerInvariant();
        return SupportedExtensions.Any(ext => fileName.EndsWith(ext));
    }
}
