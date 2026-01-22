using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Services;

public class SettingsService
{
    private readonly RaidStatsDb _db;

    // Setting keys
    public const string AutoIncludeThresholdKey = "auto_include_threshold";

    // Default values
    public const int DefaultAutoIncludeThreshold = 300;

    public SettingsService(RaidStatsDb db)
    {
        _db = db;
    }

    /// <summary>
    /// Get the auto-include threshold (minimum encounters for auto-inclusion)
    /// </summary>
    public async Task<int> GetAutoIncludeThresholdAsync(CancellationToken ct = default)
    {
        var setting = await _db.Settings
            .Where(s => s.Key == AutoIncludeThresholdKey)
            .FirstOrDefaultAsync(ct);

        if (setting != null && int.TryParse(setting.Value, out var threshold))
        {
            return threshold;
        }

        return DefaultAutoIncludeThreshold;
    }

    /// <summary>
    /// Set the auto-include threshold
    /// </summary>
    public async Task SetAutoIncludeThresholdAsync(int threshold, CancellationToken ct = default)
    {
        var existing = await _db.Settings
            .Where(s => s.Key == AutoIncludeThresholdKey)
            .FirstOrDefaultAsync(ct);

        if (existing != null)
        {
            await _db.Settings
                .Where(s => s.Key == AutoIncludeThresholdKey)
                .Set(s => s.Value, threshold.ToString())
                .Set(s => s.UpdatedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(ct);
        }
        else
        {
            await _db.InsertAsync(new SettingsEntity
            {
                Key = AutoIncludeThresholdKey,
                Value = threshold.ToString(),
                UpdatedAt = DateTimeOffset.UtcNow
            }, token: ct);
        }
    }

    /// <summary>
    /// Get a setting value by key
    /// </summary>
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var setting = await _db.Settings
            .Where(s => s.Key == key)
            .FirstOrDefaultAsync(ct);

        return setting?.Value;
    }

    /// <summary>
    /// Set a setting value
    /// </summary>
    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var existing = await _db.Settings
            .Where(s => s.Key == key)
            .FirstOrDefaultAsync(ct);

        if (existing != null)
        {
            await _db.Settings
                .Where(s => s.Key == key)
                .Set(s => s.Value, value)
                .Set(s => s.UpdatedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(ct);
        }
        else
        {
            await _db.InsertAsync(new SettingsEntity
            {
                Key = key,
                Value = value,
                UpdatedAt = DateTimeOffset.UtcNow
            }, token: ct);
        }
    }
}
