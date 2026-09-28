using System.IO.Compression;

namespace GW2RaidStats.Processor.Services;

/// <summary>
/// Reads just enough of a raw arcdps log to identify it when GW2EI refuses to parse it:
/// the boss species ID from the header and the log start time from the LogStart event.
/// Layout per the arcdps evtc README (revision 1).
/// </summary>
public static class EvtcHeaderReader
{
    private const int AgentSize = 96;
    private const int SkillSize = 68;
    private const int EventSize = 64;
    private const int EventValueOffset = 24;
    private const int EventStateChangeOffset = 56;
    private const byte LogStartStateChange = 9;

    public static EvtcHeader Read(string path)
    {
        using var file = File.OpenRead(path);
        using var raw = new MemoryStream();

        if (path.EndsWith(".evtc", StringComparison.OrdinalIgnoreCase))
        {
            file.CopyTo(raw);
        }
        else
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            using var entry = zip.Entries.Single().Open();
            entry.CopyTo(raw);
        }

        var bytes = raw.GetBuffer().AsSpan(0, (int)raw.Length);

        if (!bytes[..4].SequenceEqual("EVTC"u8))
            throw new InvalidDataException("Not an EVTC file");

        var revision = bytes[12];
        if (revision != 1)
            throw new InvalidDataException($"Unsupported EVTC revision {revision}");

        var triggerId = BitConverter.ToUInt16(bytes[13..15]);

        var offset = 16;
        var agentCount = BitConverter.ToInt32(bytes[offset..(offset + 4)]);
        offset += 4 + agentCount * AgentSize;
        var skillCount = BitConverter.ToInt32(bytes[offset..(offset + 4)]);
        offset += 4 + skillCount * SkillSize;

        for (; offset + EventSize <= bytes.Length; offset += EventSize)
        {
            if (bytes[offset + EventStateChangeOffset] != LogStartStateChange) continue;

            var serverUnixSeconds = BitConverter.ToUInt32(bytes[(offset + EventValueOffset)..(offset + EventValueOffset + 4)]);
            return new EvtcHeader(triggerId, DateTimeOffset.FromUnixTimeSeconds(serverUnixSeconds));
        }

        throw new InvalidDataException("No LogStart event found");
    }
}

public record EvtcHeader(int TriggerId, DateTimeOffset LogStart);
