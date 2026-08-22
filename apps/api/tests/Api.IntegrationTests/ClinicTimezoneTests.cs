using Clinic.Api.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The clinic timezone as required configuration (spec: the clinic's timezone is configured,
/// and working hours are stored unconverted).
/// </summary>
/// <remarks>
/// The failure this covers is the one that cannot be seen in normal use: a missing or misspelt
/// zone id that the app quietly replaces with a default or with the host's local zone. Working
/// hours would then be recorded correctly and interpreted an hour out, surfacing in change 4 as
/// wrong availability rather than as an error — so startup has to refuse, and something has to
/// prove it refuses.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ClinicTimezoneTests(ApiFixture fixture)
{
    [Fact]
    public void A_recognized_zone_starts_and_is_what_the_system_reports()
    {
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Clinic:Timezone"] = "Europe/Lisbon",
        });

        var timezone = host.Services.GetRequiredService<ClinicTimezone>();

        // Not merely "it started": the configured zone is the one downstream code will get,
        // resolved once rather than re-parsed per caller.
        Assert.Equal("Europe/Lisbon", timezone.Id);
        Assert.Equal("Europe/Lisbon", timezone.Zone.Id);
    }

    [Fact]
    public void An_unrecognized_zone_fails_startup_naming_the_setting()
    {
        // The plausible typo — a real city, wrong id shape — rather than obvious nonsense.
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Clinic:Timezone"] = "America/SaoPaulo",
        });

        var failure = Assert.Throws<OptionsValidationException>(() =>
            host.Services.GetRequiredService<ClinicTimezone>());

        Assert.Contains("Clinic__Timezone", failure.Message);
        Assert.Contains("America/SaoPaulo", failure.Message);
    }

    [Fact]
    public void A_missing_zone_fails_startup_naming_the_setting()
    {
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Clinic:Timezone"] = string.Empty,
        });

        var failure = Assert.Throws<OptionsValidationException>(() =>
            host.Services.GetRequiredService<ClinicTimezone>());

        // The operator needs to know which line to edit, so the setting name is part of the
        // contract rather than incidental to the message.
        Assert.Contains("Clinic__Timezone", failure.Message);
    }

    [Fact]
    public void The_zone_is_not_the_hosts_local_zone_by_accident()
    {
        // Guards the specific failure mode a default would hide: if the app ever fell back to
        // the machine's zone, this passes on a São Paulo laptop and fails in CI, or vice versa.
        // Asserting against the CONFIGURED value is what makes the test say something.
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Clinic:Timezone"] = "Asia/Tokyo",
        });

        var timezone = host.Services.GetRequiredService<ClinicTimezone>();

        Assert.Equal("Asia/Tokyo", timezone.Id);
        Assert.NotEqual(TimeZoneInfo.Local.Id, timezone.Id);
    }
}
