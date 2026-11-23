using Booket.Application.Features.Auth;
using Booket.Application.Features.Availability;
using Booket.Application.Features.Businesses;
using Booket.Application.Features.Staff;

namespace Booket.Application.Tests;

public sealed class ValidatorTests
{
    private static RegisterBusinessCommand ValidBusiness() =>
        new("owner@shop.test", "Passw0rd!", "Owner", "Shop", "my-shop", "Europe/London");

    [Fact]
    public void Valid_business_registration_passes()
    {
        new RegisterBusinessValidator().Validate(ValidBusiness()).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Europe/London", true)]
    [InlineData("America/New_York", true)]
    [InlineData("UTC", true)]
    [InlineData("GMT Standard Time", false)] // Windows id, not IANA
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("", false)]
    public void Time_zone_must_be_an_iana_id(string timeZone, bool valid)
    {
        new RegisterBusinessValidator().Validate(ValidBusiness() with { TimeZoneId = timeZone }).IsValid.ShouldBe(valid);
    }

    [Theory]
    [InlineData("my-shop", true)]
    [InlineData("shop42", true)]
    [InlineData("My-Shop", false)]
    [InlineData("my--shop", false)]
    [InlineData("-shop", false)]
    [InlineData("my shop", false)]
    public void Slug_format_is_enforced(string slug, bool valid)
    {
        new RegisterBusinessValidator().Validate(ValidBusiness() with { BusinessSlug = slug }).IsValid.ShouldBe(valid);
    }

    [Theory]
    [InlineData("Passw0rd!", true)]
    [InlineData("password1", false)] // no uppercase
    [InlineData("PASSWORD1", false)] // no lowercase
    [InlineData("Password", false)]  // no digit
    [InlineData("Pa1", false)]       // too short
    public void Password_policy_is_enforced(string password, bool valid)
    {
        new RegisterCustomerValidator().Validate(new RegisterCustomerCommand("c@x.test", password, "C", null)).IsValid.ShouldBe(valid);
    }

    [Fact]
    public void Availability_range_cannot_exceed_31_days_or_run_backwards()
    {
        var validator = new GetAvailabilityValidator();
        var from = new DateOnly(2026, 6, 1);

        validator.Validate(new GetAvailabilityQuery(Guid.NewGuid(), Guid.NewGuid(), from, from.AddDays(30), null)).IsValid.ShouldBeTrue();
        validator.Validate(new GetAvailabilityQuery(Guid.NewGuid(), Guid.NewGuid(), from, from.AddDays(31), null)).IsValid.ShouldBeFalse();
        validator.Validate(new GetAvailabilityQuery(Guid.NewGuid(), Guid.NewGuid(), from, from.AddDays(-1), null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Working_hours_must_end_after_they_start()
    {
        var command = new SetWorkingHoursCommand(Guid.NewGuid(), Guid.NewGuid(),
            [new WorkingHoursDto(DayOfWeek.Monday, new TimeOnly(17, 0), new TimeOnly(9, 0))]);

        new SetWorkingHoursValidator().Validate(command).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(7, false)]
    public void Slot_granularity_must_be_a_supported_step(int minutes, bool valid)
    {
        var policy = new BookingPolicyDto(60, 30, 24, minutes);

        new BookingPolicyDtoValidator().Validate(policy).IsValid.ShouldBe(valid);
    }
}
