using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GW2RaidStats.Processor.Configuration;

namespace GW2RaidStats.Processor.Services;

public class Gw2EiRunner
{
    private readonly ProcessorOptions _options;
    private readonly ILogger<Gw2EiRunner> _logger;
    private readonly string _configPath;

    // EI's status for logs under its 2.2s minimum, e.g. "Program: Log is too short: 478 ms < 2200 ms"
    private static readonly Regex TooShortRegex = new(@"Log is too short: (\d+) ms");

    public Gw2EiRunner(
        IOptions<ProcessorOptions> options,
        ILogger<Gw2EiRunner> logger)
    {
        _options = options.Value;
        _logger = logger;
        _configPath = Path.Combine(AppContext.BaseDirectory, "gw2ei-config.conf");
    }

    public async Task<Gw2EiResult> ProcessLogAsync(
        string inputPath,
        string outputDirectory,
        CancellationToken ct = default)
    {
        // GW2EI generates output files next to the input file
        var inputDirectory = Path.GetDirectoryName(inputPath) ?? outputDirectory;
        var inputFileNameWithoutExt = Path.GetFileNameWithoutExtension(inputPath);

        // Ensure output directory exists (we'll move files there after)
        Directory.CreateDirectory(outputDirectory);

        // GW2EI generates output files next to the input file, not in a separate output directory
        // So we pass just the config and input file, then look for output next to the input
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{_options.Gw2EiPath}\" -c \"{_configPath}\" \"{inputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.LogInformation("Running GW2EI: {Arguments}", startInfo.Arguments);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ProcessingTimeoutSeconds));

            await process.WaitForExitAsync(timeoutCts.Token);

            var output = await outputTask;
            var error = await errorTask;

            if (!string.IsNullOrWhiteSpace(output))
            {
                _logger.LogDebug("GW2EI stdout: {Output}", output);
            }
            if (!string.IsNullOrWhiteSpace(error))
            {
                _logger.LogDebug("GW2EI stderr: {Error}", error);
            }

            // EI writes its own run log next to the input; nothing reads it
            File.Delete(Path.Combine(inputDirectory, inputFileNameWithoutExt + ".log"));

            if (process.ExitCode != 0)
            {
                _logger.LogError("GW2EI failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
                return new Gw2EiResult(false, null, null, error, null);
            }

            // Find the generated files - GW2EI generates them next to the input file
            // They are named like: inputfilename_class_boss_time_result.json/html
            // Filter by input filename prefix to avoid race conditions with parallel workers
            var matchingFiles = Directory.GetFiles(inputDirectory)
                .Where(f => Path.GetFileName(f).StartsWith(inputFileNameWithoutExt + "_"))
                .Where(f => f.EndsWith(".json") || f.EndsWith(".html"))
                .ToArray();

            _logger.LogDebug("Found {Count} matching files for {Input} in {Dir}: {Files}",
                matchingFiles.Length, inputFileNameWithoutExt, inputDirectory,
                string.Join(", ", matchingFiles.Select(Path.GetFileName)));

            var jsonFile = matchingFiles.FirstOrDefault(f => f.EndsWith(".json"));
            var htmlFile = matchingFiles.FirstOrDefault(f => f.EndsWith(".html"));

            if (jsonFile == null)
            {
                // EI exits 0 on a parse failure; the reason is only in its "Processed - {json}" summary line
                var status = ReadEiStatus(output);
                _logger.LogError("GW2EI did not generate JSON file: {Status}. Output: {Output}", status, output);

                var tooShort = status == null ? Match.Empty : TooShortRegex.Match(status);
                return new Gw2EiResult(false, null, null,
                    status ?? "No JSON file generated",
                    tooShort.Success ? int.Parse(tooShort.Groups[1].Value) : null);
            }

            // Move the generated files to the output directory
            var newJsonPath = Path.Combine(outputDirectory, Path.GetFileName(jsonFile));
            var newHtmlPath = htmlFile != null ? Path.Combine(outputDirectory, Path.GetFileName(htmlFile)) : null;

            File.Move(jsonFile, newJsonPath);
            if (htmlFile != null && newHtmlPath != null)
            {
                File.Move(htmlFile, newHtmlPath);
            }

            _logger.LogInformation("GW2EI completed successfully. JSON: {Json}, HTML: {Html}", newJsonPath, newHtmlPath);

            return new Gw2EiResult(true, newJsonPath, newHtmlPath, null, null);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            throw;
        }
    }

    private static string? ReadEiStatus(string output)
    {
        const string prefix = "Processed - ";
        var line = output.Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith(prefix));
        if (line == null) return null;

        using var doc = JsonDocument.Parse(line[prefix.Length..]);
        return doc.RootElement.GetProperty("status").GetString();
    }
}

public record Gw2EiResult(
    bool Success,
    string? JsonPath,
    string? HtmlPath,
    string? Error,
    int? TooShortDurationMs
);
