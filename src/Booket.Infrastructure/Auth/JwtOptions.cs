using System.ComponentModel.DataAnnotations;

namespace Booket.Infrastructure.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "booket";

    [Required]
    public string Audience { get; set; } = "booket-api";

    /// <summary>HMAC-SHA256 key. Supply via environment variable or user-secrets; never commit a real one.</summary>
    [Required]
    [MinLength(32, ErrorMessage = "Jwt:SigningKey must be at least 32 characters.")]
    public string SigningKey { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);
}
