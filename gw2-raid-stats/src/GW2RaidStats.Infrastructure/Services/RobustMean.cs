namespace GW2RaidStats.Infrastructure.Services;

/// <summary>
/// Outlier-resistant averaging for stack-distance metrics. A single hand-kiter
/// (Deimos) or pylon kiter (Qadim the Peerless) sits well off the stack, and a plain
/// mean lets them drag the squad average up so far that genuinely-drifting players
/// still read "green". <see cref="OutlierExcludedMean"/> drops values far from the
/// median before averaging, so the baseline reflects the actual stack.
/// </summary>
internal static class RobustMean
{
    /// <summary>
    /// Mean of the values with outliers removed first. An outlier is a value whose
    /// absolute deviation from the median exceeds 3 MADs (median absolute deviation,
    /// scaled by 1.4826 so the cutoff matches "3 sigma" for normal-ish data) — the
    /// standard robust rule. Falls back to the plain mean when there are too few
    /// points to judge an outlier, or when the data is too tight to define a spread.
    /// </summary>
    public static decimal? OutlierExcludedMean(IEnumerable<decimal?> values)
    {
        var xs = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (xs.Count == 0) return null;
        if (xs.Count < 4) return xs.Average(); // too few points to call anything an outlier

        var median = Median(xs);
        var deviations = xs.Select(x => Math.Abs(x - median)).ToList();
        var mad = Median(deviations);
        // MAD is 0 when more than half the values are identical; fall back to the mean
        // absolute deviation so a tight cluster doesn't reject every non-median value.
        var spread = mad > 0 ? mad : deviations.Average();
        if (spread <= 0) return xs.Average(); // every value identical

        var cutoff = 3m * 1.4826m * spread;
        var kept = xs.Where(x => Math.Abs(x - median) <= cutoff).ToList();
        return kept.Count > 0 ? kept.Average() : xs.Average();
    }

    private static decimal Median(List<decimal> xs)
    {
        var sorted = xs.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2m;
    }
}
