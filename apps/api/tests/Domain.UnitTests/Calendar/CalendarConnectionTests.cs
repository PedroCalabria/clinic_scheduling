using Clinic.Domain;
using Clinic.Domain.Calendar;

namespace Clinic.Domain.UnitTests.Calendar;

/// <summary>
/// The connection state machine (change 6a, design K7).
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs with no key, no HTTP and no database, which is the point of taking the
/// credential as an opaque string: the rule under test is "there is credential material", not
/// "it is AES-GCM". A domain that knew about the envelope would need one to be tested.
/// </para>
/// <para>
/// <b>The two tests worth reading are the null-credential pair.</b> Google issues a refresh token
/// only on the first grant for a client/user pair, so a reconnection can succeed and carry
/// nothing — and a handler that wrote that through would replace a working credential with
/// nothing and report success. That behaviour is specified here rather than at the call site,
/// because it is the guard that survives a change in Google's behaviour (design K6).
/// </para>
/// </remarks>
public sealed class CalendarConnectionTests
{
    private static readonly Guid ProfessionalId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddHours(3);

    private const string Sealed = "v1.nonce.ciphertext";
    private const string Resealed = "v1.another.ciphertext";

    private static CalendarConnection Connected() =>
        CalendarConnection.Establish(ProfessionalId, CalendarProvider.Google, "primary", Sealed, Now);

    [Fact]
    public void Establishing_records_the_grant_and_the_moment_it_was_observed()
    {
        var connection = Connected();

        Assert.Equal(ProfessionalId, connection.ProfessionalId);
        Assert.Equal(CalendarProvider.Google, connection.Provider);
        Assert.Equal("primary", connection.TargetCalendarId);
        Assert.Equal(Sealed, connection.SealedCredential);
        Assert.Equal(CalendarConnectionStatus.Connected, connection.Status);
        Assert.Equal(Now, connection.ConnectedAtUtc);
        Assert.Equal(Now, connection.StateObservedAtUtc);
        Assert.True(connection.IsUsable);
    }

    [Fact]
    public void A_connection_cannot_be_established_without_credential_material()
    {
        // The invariant that makes the status trustworthy: 6b reads it to decide whether to
        // enqueue work, and a connection reading as connected while holding nothing would
        // produce work that can never succeed.
        Assert.Throws<DomainRuleViolationException>(() =>
            CalendarConnection.Establish(ProfessionalId, CalendarProvider.Google, "primary", "  ", Now));
    }

    [Fact]
    public void A_connection_must_belong_to_a_professional_and_name_a_calendar()
    {
        Assert.Throws<DomainRuleViolationException>(() =>
            CalendarConnection.Establish(Guid.Empty, CalendarProvider.Google, "primary", Sealed, Now));

        Assert.Throws<DomainRuleViolationException>(() =>
            CalendarConnection.Establish(ProfessionalId, CalendarProvider.Google, " ", Sealed, Now));
    }

    [Fact]
    public void Reconnecting_with_fresh_material_replaces_what_is_held()
    {
        var connection = Connected();
        connection.ObserveRevoked(Now);

        connection.Reconnect(Resealed, "primary", Later);

        Assert.Equal(Resealed, connection.SealedCredential);
        Assert.Equal(CalendarConnectionStatus.Connected, connection.Status);
        Assert.Equal(Later, connection.StateObservedAtUtc);
    }

    [Fact]
    public void Reconnecting_without_material_keeps_what_is_already_held()
    {
        // Google returned no refresh token because this is not the first grant. The held
        // credential is still good, and writing null over it would break a working connection
        // while reporting success.
        var connection = Connected();
        connection.ObserveRevoked(Now);

        connection.Reconnect(null, "primary", Later);

        Assert.Equal(Sealed, connection.SealedCredential);
        Assert.Equal(CalendarConnectionStatus.Connected, connection.Status);
        Assert.True(connection.IsUsable);
    }

    [Fact]
    public void Reconnecting_with_nothing_held_and_nothing_returned_is_refused()
    {
        // The only combination that cannot produce a usable connection. Refused rather than
        // recorded, so no status of "connected" exists that nothing can act on.
        var connection = Connected();
        connection.Disconnect(Now);

        Assert.Throws<DomainRuleViolationException>(() => connection.Reconnect(null, "primary", Later));
        Assert.Equal(CalendarConnectionStatus.Disconnected, connection.Status);
        Assert.Null(connection.SealedCredential);
    }

    [Fact]
    public void Reconnecting_does_not_move_the_moment_the_relationship_began()
    {
        var connection = Connected();
        connection.ObserveRevoked(Now);

        connection.Reconnect(Resealed, "primary", Later);

        // ConnectedAtUtc records when this professional first gave the clinic access, which is
        // what a consent trail cares about. Reconnecting renews the authorization, not that.
        Assert.Equal(Now, connection.ConnectedAtUtc);
    }

    [Fact]
    public void Observing_a_revocation_keeps_the_material_so_the_two_absences_stay_distinguishable()
    {
        var connection = Connected();

        connection.ObserveRevoked(Later);

        Assert.Equal(CalendarConnectionStatus.Revoked, connection.Status);
        Assert.Equal(Later, connection.StateObservedAtUtc);
        Assert.False(connection.IsUsable);

        // Worthless to Google, kept here: clearing it would erase the difference between "your
        // permission lapsed" and "you never connected", and Reconnect's null case reads it.
        Assert.Equal(Sealed, connection.SealedCredential);
    }

    [Fact]
    public void Observing_a_working_connection_moves_only_the_observation()
    {
        var connection = Connected();
        connection.ObserveRevoked(Now);

        connection.ObserveUsable(Later);

        Assert.Equal(CalendarConnectionStatus.Connected, connection.Status);
        Assert.Equal(Later, connection.StateObservedAtUtc);
        Assert.Equal(Sealed, connection.SealedCredential);
    }

    [Fact]
    public void A_connection_holding_nothing_cannot_be_observed_working()
    {
        var connection = Connected();
        connection.Disconnect(Now);

        Assert.Throws<DomainRuleViolationException>(() => connection.ObserveUsable(Later));
    }

    [Fact]
    public void Disconnecting_destroys_the_credential_and_keeps_the_record()
    {
        var connection = Connected();

        connection.Disconnect(Later);

        // I10 governs records, and the row survives. Keeping a withdrawn professional's calendar
        // key "for history" would be data minimisation failing where it is easiest to get right.
        Assert.Equal(CalendarConnectionStatus.Disconnected, connection.Status);
        Assert.Null(connection.SealedCredential);
        Assert.False(connection.IsUsable);
        Assert.Equal(Now, connection.ConnectedAtUtc);
        Assert.Equal(Later, connection.StateObservedAtUtc);
    }

    [Fact]
    public void Reconnecting_after_a_withdrawal_needs_fresh_material_and_accepts_it()
    {
        var connection = Connected();
        connection.Disconnect(Now);

        connection.Reconnect(Resealed, "primary", Later);

        Assert.True(connection.IsUsable);
        Assert.Equal(Resealed, connection.SealedCredential);
    }

    [Fact]
    public void Revoked_and_disconnected_are_different_facts()
    {
        // Same remedy — reconnect — different sentence on the screen. One state would make S2
        // tell somebody they never connected when in fact their permission lapsed.
        var revoked = Connected();
        revoked.ObserveRevoked(Later);

        var disconnected = Connected();
        disconnected.Disconnect(Later);

        Assert.NotEqual(revoked.Status, disconnected.Status);
        Assert.False(revoked.IsUsable);
        Assert.False(disconnected.IsUsable);
    }
}
