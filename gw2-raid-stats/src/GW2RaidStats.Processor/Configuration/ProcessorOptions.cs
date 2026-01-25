namespace GW2RaidStats.Processor.Configuration;

public class ProcessorOptions
{
    public const string SectionName = "Processor";

    /// <summary>
    /// Path to GW2EI CLI executable or DLL
    /// </summary>
    public string Gw2EiPath { get; set; } = "/opt/gw2ei/GW2EI.dll";

    /// <summary>
    /// Number of concurrent log processors
    /// </summary>
    public int MaxConcurrentProcessing { get; set; } = 4;

    /// <summary>
    /// Polling interval in seconds when no files are pending
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Timeout for GW2EI processing in seconds
    /// </summary>
    public int ProcessingTimeoutSeconds { get; set; } = 300;
}
