using Booket.Domain.Common;

namespace Booket.Domain.Users;

/// <summary>
/// A rotating refresh token. Only a SHA-256 hash of the token is stored. Tokens belong to a
/// family; presenting an already-rotated token revokes the whole family (reuse detection).
/// </summary>
public sealed class RefreshToken : Entity
{
    private RefreshToken() { }

    public Guid UserId { get; private set; }
    public Guid FamilyId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public Guid? ReplacedByTokenId { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAtUtc is null && now < ExpiresAtUtc;

    public static RefreshToken Issue(Guid userId, string tokenHash, DateTimeOffset now, TimeSpan lifetime, Guid? familyId = null) => new()
    {
        UserId = userId,
        FamilyId = familyId ?? Guid.CreateVersion7(),
        TokenHash = tokenHash,
        CreatedAtUtc = now,
        ExpiresAtUtc = now + lifetime,
    };

    public void Revoke(DateTimeOffset now, Guid? replacedBy = null)
    {
        RevokedAtUtc ??= now;
        ReplacedByTokenId ??= replacedBy;
    }
}
