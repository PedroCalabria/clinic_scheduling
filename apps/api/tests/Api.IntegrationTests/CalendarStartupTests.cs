using Clinic.Api.Infrastructure.Calendar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The calendar feature's conditional startup rule (change 6a, design K4).
/// </summary>
/// <remarks>
/// <para>
/// Two halves, and they are different kinds of fact. <b>No calendar configuration</b> is a
/// supported deployment: the app starts, everything else works, and only S2 reports itself
/// unavailable — the same bargain <c>GoogleOptions</c> makes for the whole federated path.
/// <b>Calendar configured with no usable key</b> is a misconfiguration, and startup refuses.
/// </para>
/// <para>
/// The failure being prevented is the one that cannot be seen in normal use: an API that starts
/// happily and writes a Google refresh token into the database in clear while every screen
/// reports success. Nothing about that looks wrong until somebody reads the table.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CalendarStartupTests(ApiFixture fixture)
{
    /// <summary>Thirty-two bytes, as AES-256 requires and the validator insists.</summary>
    private const string UsableKey = "Y2xpbmljLXNjaGVkdWxpbmctdGVzdC1rZXktMzJieXQ=";

    [Fact]
    public void A_configured_calendar_with_a_usable_key_starts()
    {
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Calendar:RedirectUri"] = "https://localhost/api/calendar/connect/callback",
            ["Calendar:TokenEncryptionKey"] = UsableKey,
        });

        var options = host.Services.GetRequiredService<IOptions<CalendarOptions>>().Value;

        Assert.True(options.IsPresent);

        // Not merely "it started": the protector is constructible, so the key that passed
        // validation is the same one that will actually protect a credential.
        Assert.NotNull(host.Services.GetRequiredService<CalendarTokenProtector>());
    }

    [Fact]
    public void No_calendar_configuration_at_all_starts_normally()
    {
        // The deployment that has not turned the feature on. It must be completely unaffected —
        // this is what keeps a new required secret from breaking every existing stack.
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Calendar:RedirectUri"] = string.Empty,
            ["Calendar:TokenEncryptionKey"] = string.Empty,
        });

        var options = host.Services.GetRequiredService<IOptions<CalendarOptions>>().Value;

        Assert.False(options.IsPresent);
    }

    [Fact]
    public void A_configured_calendar_with_no_key_refuses_to_start_naming_the_setting()
    {
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Calendar:RedirectUri"] = "https://localhost/api/calendar/connect/callback",
            ["Calendar:TokenEncryptionKey"] = string.Empty,
        });

        var failure = Assert.Throws<OptionsValidationException>(() =>
            host.Services.GetRequiredService<IOptions<CalendarOptions>>().Value);

        Assert.Contains("Calendar__TokenEncryptionKey", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_that_is_not_base64_refuses_to_start()
    {
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Calendar:RedirectUri"] = "https://localhost/api/calendar/connect/callback",

            // What somebody types when they treat this as a passphrase rather than a key.
            ["Calendar:TokenEncryptionKey"] = "not base64 at all!!",
        });

        var failure = Assert.Throws<OptionsValidationException>(() =>
            host.Services.GetRequiredService<IOptions<CalendarOptions>>().Value);

        Assert.Contains("base64", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_key_of_the_wrong_length_refuses_to_start_and_says_how_long_it_was()
    {
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Calendar:RedirectUri"] = "https://localhost/api/calendar/connect/callback",

            // Valid base64, sixteen bytes. AES would accept this as AES-128; the validator does
            // not, because half the intended key strength arriving silently is exactly the kind
            // of thing nobody notices.
            ["Calendar:TokenEncryptionKey"] = "MDEyMzQ1Njc4OWFiY2RlZg==",
        });

        var failure = Assert.Throws<OptionsValidationException>(() =>
            host.Services.GetRequiredService<IOptions<CalendarOptions>>().Value);

        Assert.Contains("16 bytes", failure.Message, StringComparison.Ordinal);
        Assert.Contains("32", failure.Message, StringComparison.Ordinal);
    }
}
