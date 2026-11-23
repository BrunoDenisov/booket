using System.Text.RegularExpressions;
using FluentValidation;

namespace Booket.Application.Common;

internal static partial class ValidationRules
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugRegex();

    public static IRuleBuilderOptions<T, string> Password<T>(this IRuleBuilder<T, string> rule) => rule
        .NotEmpty()
        .MinimumLength(8)
        .MaximumLength(128)
        .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
        .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
        .Matches("[0-9]").WithMessage("Password must contain a digit.");

    public static IRuleBuilderOptions<T, string> Slug<T>(this IRuleBuilder<T, string> rule) => rule
        .NotEmpty()
        .MaximumLength(64)
        .Must(s => s is not null && SlugRegex().IsMatch(s))
        .WithMessage("Slug may only contain lowercase letters, digits and single hyphens.");

    /// <summary>Accepts IANA time zone ids only (e.g. Europe/London), on every OS.</summary>
    public static IRuleBuilderOptions<T, string> IanaTimeZone<T>(this IRuleBuilder<T, string> rule) => rule
        .NotEmpty()
        .Must(IsIanaTimeZone)
        .WithMessage("'{PropertyValue}' is not a valid IANA time zone id (e.g. Europe/London).");

    public static IRuleBuilderOptions<T, string> Currency<T>(this IRuleBuilder<T, string> rule) => rule
        .NotEmpty()
        .Length(3)
        .Matches("^[A-Za-z]{3}$").WithMessage("Currency must be a 3-letter ISO 4217 code.");

    private static bool IsIanaTimeZone(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && (id == "UTC" || TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
        && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);
}
