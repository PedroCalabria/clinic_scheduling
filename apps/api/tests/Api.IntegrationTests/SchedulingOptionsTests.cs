using Clinic.Api.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The scheduling policy as optional configuration with defaults (design F8).
/// </summary>
/// <remarks>
/// The mirror image of <see cref="ClinicTimezoneTests"/>, and the pairing is the point. That
/// setting must fail startup when absent because no default is right; these must NOT, because
/// their defaults are right until a clinic says otherwise. Both halves are asserted so the
/// difference is a decision on record rather than an accident of which one somebody remembered
/// to give a default.
///
/// It also covers the trap <c>professional-configuration</c> discovered the hard way: required
/// configuration breaks every host that builds its own <c>WebApplicationFactory</c>. Defaults
/// are what keep this change from repeating that, and the first test is what proves it.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SchedulingOptionsTests(ApiFixture fixture)
{
    [Fact]
    public void Absent_configuration_starts_on_the_documented_defaults()
    {
        // Nothing overridden: exactly the state a deployment that never set these is in.
        using var host = fixture.CreateHost(new Dictionary<string, string>());

        var scheduling = host.Services.GetRequiredService<ClinicScheduling>();

        // The values .env.example documents. If a default changes, that file changes with it.
        Assert.Equal(15, scheduling.Parameters.SlotStartStep.TotalMinutes);
        Assert.Equal(60, scheduling.Parameters.MinimumLeadTime.TotalMinutes);
        Assert.Equal(60, scheduling.Parameters.Horizon.TotalDays);
        Assert.Equal(31, scheduling.MaxWindowDays);
        Assert.Equal(60, scheduling.AvailabilityRequestsPerMinute);
    }

    [Fact]
    public void Configured_values_are_what_the_solver_gets()
    {
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Scheduling:SlotStartStepMinutes"] = "20",
            ["Scheduling:MinimumLeadTimeMinutes"] = "0",
            ["Scheduling:HorizonDays"] = "90",
            ["Scheduling:MaxWindowDays"] = "7",
        });

        var scheduling = host.Services.GetRequiredService<ClinicScheduling>();

        Assert.Equal(20, scheduling.Parameters.SlotStartStep.TotalMinutes);

        // Zero lead time is legitimate, not a missing value — a clinic that takes walk-ins.
        Assert.Equal(0, scheduling.Parameters.MinimumLeadTime.TotalMinutes);
        Assert.Equal(90, scheduling.Parameters.Horizon.TotalDays);
        Assert.Equal(7, scheduling.MaxWindowDays);
    }

    [Theory]
    [InlineData("Scheduling:SlotStartStepMinutes", "0")]
    [InlineData("Scheduling:SlotStartStepMinutes", "-15")]
    [InlineData("Scheduling:MinimumLeadTimeMinutes", "-1")]
    [InlineData("Scheduling:HorizonDays", "0")]
    [InlineData("Scheduling:MaxWindowDays", "0")]
    [InlineData("Scheduling:AvailabilityRequestsPerMinute", "0")]
    public void A_nonsensical_value_fails_startup_naming_the_setting(string key, string value)
    {
        using var host = fixture.CreateHost(new Dictionary<string, string> { [key] = value });

        var failure = Assert.Throws<OptionsValidationException>(() =>
            host.Services.GetRequiredService<ClinicScheduling>());

        // The operator needs to know which line to edit. The property name is the last segment
        // of the configuration key, which is what the annotation reports.
        Assert.Contains(key.Split(':')[1], failure.Message);
    }
}
