namespace GW2RaidStats.Infrastructure.Services;

/// <summary>
/// Helper for grouping multi-hit mechanics by Internal Cooldown (ICD).
/// Events within the ICD window are counted as a single occurrence.
/// </summary>
public static class MechanicIcdHelper
{
    /// <summary>
    /// Known ICD values for multi-hit mechanics (in milliseconds).
    /// Mechanics not in this list are counted as individual hits.
    /// </summary>
    /// <remarks>
    /// These values are based on Elite Insights internal mechanic definitions.
    /// Add new mechanics here as needed.
    /// </remarks>
    public static readonly Dictionary<string, int> KnownIcds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Dhuum CM
        ["Echo PU"] = 500,           // Ender's Echo Pick Up - multi-hit grab
        ["Snatch"] = 500,            // Dhuum snatch - can hit multiple times

        // Deimos
        ["Tear"] = 300,              // Tear Instability

        // Soulless Horror
        ["yourAGolem"] = 500,        // Golem - can hit multiple times

        // Add more mechanics as needed...
        // Format: ["MechanicShortName"] = IcdInMilliseconds,
    };

    /// <summary>
    /// Gets the ICD for a mechanic, or 0 if not a known multi-hit mechanic.
    /// </summary>
    public static int GetIcd(string mechanicName)
    {
        return KnownIcds.TryGetValue(mechanicName, out var icd) ? icd : 0;
    }

    /// <summary>
    /// Counts mechanic events with ICD grouping using a sliding window.
    /// Events within ICD of the previous event are part of the same occurrence.
    /// Only when there's a gap > ICD does a new occurrence start.
    /// </summary>
    /// <param name="eventTimesMs">List of event times in milliseconds</param>
    /// <param name="icdMs">Internal cooldown in milliseconds</param>
    /// <returns>Number of distinct occurrences</returns>
    public static int CountWithIcd(List<int> eventTimesMs, int icdMs)
    {
        if (eventTimesMs.Count == 0) return 0;
        if (icdMs <= 0) return eventTimesMs.Count;

        // Sort to ensure proper ordering
        eventTimesMs.Sort();

        int count = 1;
        int previousEventTime = eventTimesMs[0];

        for (int i = 1; i < eventTimesMs.Count; i++)
        {
            if (eventTimesMs[i] - previousEventTime > icdMs)
            {
                // Gap is larger than ICD, this is a new occurrence
                count++;
            }
            // Always update to compare next event against this one (sliding window)
            previousEventTime = eventTimesMs[i];
        }

        return count;
    }

    /// <summary>
    /// Groups mechanic events by player and counts with ICD grouping.
    /// </summary>
    /// <param name="events">List of (PlayerId, EventTimeMs) tuples</param>
    /// <param name="mechanicName">Name of the mechanic to look up ICD</param>
    /// <returns>Dictionary of PlayerId to count</returns>
    public static Dictionary<Guid, int> CountByPlayerWithIcd(
        IEnumerable<(Guid PlayerId, int EventTimeMs)> events,
        string mechanicName)
    {
        var icd = GetIcd(mechanicName);

        // Group by player
        var byPlayer = events
            .GroupBy(e => e.PlayerId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.EventTimeMs).ToList()
            );

        // Count with ICD grouping
        var result = new Dictionary<Guid, int>();
        foreach (var (playerId, times) in byPlayer)
        {
            result[playerId] = CountWithIcd(times, icd);
        }

        return result;
    }
}
