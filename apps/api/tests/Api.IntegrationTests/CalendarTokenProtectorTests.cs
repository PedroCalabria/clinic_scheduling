using Clinic.Api.Infrastructure.Calendar;
using Microsoft.Extensions.Options;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The envelope that makes "encrypted at rest" true (change 6a, design K3).
/// </summary>
/// <remarks>
/// <para>
/// Unit tests in the integration project, because the type they cover is <c>internal</c> to
/// <c>Api</c> and this is the assembly that can see it. Nothing here touches a database, a
/// network or a host — they run in microseconds and belong to the tier only by accident of
/// visibility.
/// </para>
/// <para>
/// The two that earn their place are <see cref="A_tampered_envelope_will_not_open"/> and
/// <see cref="An_envelope_sealed_under_a_different_key_will_not_open"/>: the first is why GCM
/// was chosen over CBC-plus-HMAC, and the second is the operational failure this change's
/// <c>.env.example</c> warns about in as many words.
/// </para>
/// </remarks>
public sealed class CalendarTokenProtectorTests
{
    private const string Token = "1//0eXaMpLe-refresh-token-value";

    private static CalendarTokenProtector Protector(string? key = null) =>
        new(Options.Create(new CalendarOptions
        {
            TokenEncryptionKey = key ?? ApiFixture.CalendarEncryptionKey,
        }));

    private static string OtherKey() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void A_sealed_token_round_trips()
    {
        var protector = Protector();

        Assert.Equal(Token, protector.Open(protector.Seal(Token)));
    }

    [Fact]
    public void What_comes_out_of_sealing_is_not_what_went_in()
    {
        var sealedValue = Protector().Seal(Token);

        Assert.NotEqual(Token, sealedValue);
        Assert.DoesNotContain("refresh-token-value", sealedValue, StringComparison.Ordinal);
        Assert.StartsWith("v1.", sealedValue, StringComparison.Ordinal);
    }

    [Fact]
    public void Sealing_the_same_token_twice_produces_different_envelopes()
    {
        // A fresh nonce per operation. This is the property being relied on, not an accident:
        // reusing a nonce under one key is how GCM fails catastrophically.
        var protector = Protector();

        Assert.NotEqual(protector.Seal(Token), protector.Seal(Token));
    }

    [Fact]
    public void A_tampered_envelope_will_not_open()
    {
        var protector = Protector();
        var parts = protector.Seal(Token).Split('.');

        // Flip one character of the ciphertext. Under CBC without an HMAC this would decrypt to
        // garbage and be returned as if it were a token; under GCM it fails, which is the whole
        // reason for choosing an authenticated mode.
        var ciphertext = parts[2];
        var tampered = string.Concat(
            parts[0], ".", parts[1], ".",
            ciphertext[0] == 'A' ? 'B' : 'A', ciphertext[1..]);

        Assert.Throws<CalendarTokenProtectionException>(() => protector.Open(tampered));
    }

    [Fact]
    public void An_envelope_sealed_under_a_different_key_will_not_open()
    {
        // The operational case: Calendar__TokenEncryptionKey changed between deploys. It fails
        // loudly rather than returning something wrong, and the slice turns it into a connection
        // that needs re-establishing.
        var sealedValue = Protector(OtherKey()).Seal(Token);

        Assert.Throws<CalendarTokenProtectionException>(() => Protector().Open(sealedValue));
    }

    [Fact]
    public void An_envelope_from_an_unknown_scheme_says_so()
    {
        var protector = Protector();
        var parts = protector.Seal(Token).Split('.');

        var exception = Assert.Throws<CalendarTokenProtectionException>(
            () => protector.Open($"v2.{parts[1]}.{parts[2]}"));

        // The day this fires, somebody is mid-rotation and needs to know which scheme they are
        // looking at — so the version is named rather than swallowed into "could not decrypt".
        Assert.Contains("v2", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-envelope")]
    [InlineData("v1.only-two-parts")]
    [InlineData("v1..")]
    [InlineData("v1.!!!.!!!")]
    public void A_malformed_envelope_is_refused_rather_than_misread(string stored)
    {
        var protector = Protector();

        Assert.ThrowsAny<Exception>(() => protector.Open(stored));
    }

    [Fact]
    public void A_key_of_the_wrong_size_is_refused_at_first_use()
    {
        // The validator refuses this at startup; this asserts the type does not quietly accept
        // it either, so the two cannot disagree about what a usable key is.
        var short_ = Convert.ToBase64String(new byte[16]);

        Assert.Throws<InvalidOperationException>(() => Protector(short_).Seal(Token));
    }

    [Fact]
    public void No_key_at_all_is_a_programming_error_at_the_moment_of_use()
    {
        // Sealing or opening with the feature off means something is trying to protect a
        // credential that should not exist. Reported as an error rather than degrading into
        // storing a token in clear.
        var protector = Protector(string.Empty);

        // A well-formed envelope, so Open gets past its shape checks and actually reaches for
        // the key. A malformed one would fail earlier and prove nothing about this.
        var wellFormed = Protector().Seal(Token);

        Assert.Throws<InvalidOperationException>(() => protector.Seal(Token));
        Assert.Throws<InvalidOperationException>(() => protector.Open(wellFormed));
    }

    [Fact]
    public void A_protector_with_no_key_can_still_be_constructed()
    {
        // Load-bearing, and not obvious. This type is a dependency of CalendarWithdrawal, which
        // is on the account DISABLE path — and disabling a staff account must keep working on a
        // deployment that never configured a calendar, which is a supported deployment (K4).
        // Throwing in the constructor would have made turning off an account fail because the
        // clinic does not use Google Calendar.
        var exception = Record.Exception(() => Protector(string.Empty));

        Assert.Null(exception);
    }

    [Fact]
    public void Sealing_nothing_is_refused()
    {
        Assert.ThrowsAny<ArgumentException>(() => Protector().Seal("   "));
    }
}
