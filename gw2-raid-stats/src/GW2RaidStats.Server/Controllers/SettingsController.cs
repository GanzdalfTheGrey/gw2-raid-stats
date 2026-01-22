using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly SettingsService _settingsService;

    public SettingsController(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Get the guild name
    /// </summary>
    [HttpGet("guild-name")]
    public async Task<ActionResult<GuildNameResponse>> GetGuildName(CancellationToken ct)
    {
        var guildName = await _settingsService.GetGuildNameAsync(ct);
        return Ok(new GuildNameResponse(guildName));
    }

    /// <summary>
    /// Update the guild name
    /// </summary>
    [HttpPut("guild-name")]
    public async Task<ActionResult> UpdateGuildName(
        [FromBody] UpdateGuildNameRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.GuildName))
        {
            return BadRequest("Guild name cannot be empty");
        }

        await _settingsService.SetGuildNameAsync(request.GuildName.Trim(), ct);
        return NoContent();
    }
}

public record GuildNameResponse(string GuildName);

public record UpdateGuildNameRequest(string GuildName);
