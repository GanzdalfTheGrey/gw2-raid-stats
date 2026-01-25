using Microsoft.AspNetCore.Mvc;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminAuthController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public AdminAuthController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Check if admin authentication is required
    /// </summary>
    [HttpGet("auth-required")]
    public ActionResult<AuthRequiredResponse> IsAuthRequired()
    {
        var password = _configuration["Admin:Password"];
        var isRequired = !string.IsNullOrEmpty(password);
        return Ok(new AuthRequiredResponse(isRequired));
    }

    /// <summary>
    /// Verify admin password
    /// </summary>
    [HttpPost("verify")]
    public ActionResult<VerifyResponse> VerifyPassword([FromBody] VerifyRequest request)
    {
        var configuredPassword = _configuration["Admin:Password"];

        // If no password configured, allow access
        if (string.IsNullOrEmpty(configuredPassword))
        {
            return Ok(new VerifyResponse(true));
        }

        // Simple string comparison (for basic security)
        // In production, you'd want to hash this
        var isValid = request.Password == configuredPassword;

        if (!isValid)
        {
            return Unauthorized(new VerifyResponse(false));
        }

        return Ok(new VerifyResponse(true));
    }
}

public record AuthRequiredResponse(bool Required);
public record VerifyRequest(string Password);
public record VerifyResponse(bool Valid);
