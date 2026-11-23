using Microsoft.AspNetCore.Identity;

namespace Booket.Infrastructure.Auth;

/// <summary>ASP.NET Core Identity's PBKDF2 hasher (versioned format, upgradeable), without the rest of Identity.</summary>
internal sealed class IdentityPasswordHasher : Application.Abstractions.IPasswordHasher
{
    private static readonly object User = new();
    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(User, password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(User, hash, password) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
}
