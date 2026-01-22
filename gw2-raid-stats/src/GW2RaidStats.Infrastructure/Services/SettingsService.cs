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
    public const string RecapIncludeAllBossesKey = "recap_include_all_bosses";

    // Default values
    public const int DefaultAutoIncludeThreshold = 300;
    public const bool DefaultRecapIncludeAllBosses = false;

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
    /// Get whether recap should include all bosses (ignoring the ignored bosses list)
    /// </summary>
    public async Task<bool> GetRecapIncludeAllBossesAsync(CancellationToken ct = default)
    {
        var setting = await _db.Settings
            .Where(s => s.Key == RecapIncludeAllBossesKey)
            .FirstOrDefaultAsync(ct);

        if (setting != null && bool.TryParse(setting.Value, out var value))
        {
            return value;
        }

        return DefaultRecapIncludeAllBosses;
    }

    /// <summary>
    /// Set whether recap should include all bosses
    /// </summary>
    public async Task SetRecapIncludeAllBossesAsync(bool includeAll, CancellationToken ct = default)
    {
        var existing = await _db.Settings
            .Where(s => s.Key == RecapIncludeAllBossesKey)
            .FirstOrDefaultAsync(ct);

        if (existing != null)
        {
            await _db.Settings
                .Where(s => s.Key == RecapIncludeAllBossesKey)
                .Set(s => s.Value, includeAll.ToString().ToLower())
                .Set(s => s.UpdatedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(ct);
        }
        else
        {
            await _db.InsertAsync(new SettingsEntity
            {
                Key = RecapIncludeAllBossesKey,
                Value = includeAll.ToString().ToLower(),
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
