using Booket.Domain.Common;

namespace Booket.Domain.Users;

public enum UserRole
{
    Customer,
    Staff,
    Owner,
    Admin,
}

public sealed class User : Entity
{
    private User() { }

    public string Email { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public string FullName { get; private set; } = null!;
    public string? Phone { get; private set; }
    public UserRole Role { get; private set; }

    /// <summary>The business an <see cref="UserRole.Owner"/> or <see cref="UserRole.Staff"/> user belongs to.</summary>
    public Guid? BusinessId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static User Create(string email, string passwordHash, string fullName, string? phone, UserRole role, Guid? businessId, DateTimeOffset now)
    {
        if (role is UserRole.Owner or UserRole.Staff && businessId is null)
        {
            throw new ArgumentException($"{role} users must belong to a business.", nameof(businessId));
        }

        return new User
        {
            Email = NormalizeEmail(email),
            PasswordHash = passwordHash,
            FullName = fullName.Trim(),
            Phone = phone,
            Role = role,
            BusinessId = businessId,
            CreatedAtUtc = now,
        };
    }

    public void ChangePasswordHash(string passwordHash) => PasswordHash = passwordHash;
}
