using Clinic.Domain.Calendar;
using Clinic.Domain.Identity;

namespace Clinic.Api.Features.CalendarSync;

/// <summary>
/// What S2 is told about a professional's calendar authorization.
/// </summary>
/// <remarks>
/// <para>
/// <b>The status never travels alone.</b> <see cref="StateObservedAtUtc"/> is beside it because
/// nothing calls Google on a schedule in this change (design K8, K15), so a status is a memory
/// of the last look rather than current truth. A response that carried the status without its
/// date would let a screen state a fact more confidently than the server can support — and the
/// screen would be believed.
/// </para>
/// <para>
/// <b>No credential material of any kind</b>, sealed or otherwise. There is no field for it, so
/// it cannot be added by accident; a test asserts it against the serialized body rather than
/// against this record's shape, because the property that matters is what goes over the wire.
/// </para>
/// </remarks>
/// <param name="ConsentVersion">
/// The version of the calendar consent in force for this professional, or null when they hold
/// none.
/// </param>
/// <param name="ConsentGrantedAtUtc">When that consent was granted.</param>
internal sealed record CalendarConnectionResponse(
    bool Connected,
    string Status,
    string? Provider,
    string? TargetCalendarId,
    DateTimeOffset? ConnectedAtUtc,
    DateTimeOffset? StateObservedAtUtc,
    string? ConsentVersion,
    DateTimeOffset? ConsentGrantedAtUtc)
{
    /// <summary>
    /// The response for a professional who has no connection at all.
    /// </summary>
    /// <remarks>
    /// A successful read of a real state, not a refusal (design Open Question 4). In this change
    /// a connected calendar does nothing yet, so a screen that treated "never connected" as an
    /// error would be scolding somebody for declining a benefit that does not exist.
    /// </remarks>
    internal static CalendarConnectionResponse NeverConnected() =>
        new(false, "NotConnected", null, null, null, null, null, null);

    /// <summary>
    /// Describes a connection, with the calendar consent that belongs to it.
    /// </summary>
    /// <remarks>
    /// <b>The consent fields are here because nothing else could show them.</b> Consents are read
    /// through P7, which is a patient surface — so widening <c>identity-session</c>'s "visible to
    /// the user they belong to" to cover a professional's calendar consent needed a surface, and
    /// the alternative was a patient-shaped endpoint serving one field to a different role. S2 is
    /// the screen that obtained this consent, which makes it the screen that should be able to
    /// show what was agreed to and when.
    /// </remarks>
    internal static CalendarConnectionResponse From(
        CalendarConnection connection,
        Consent? consent = null) =>
        new(
            connection.IsUsable,
            connection.Status.ToString(),
            connection.Provider.ToString(),
            connection.TargetCalendarId,
            connection.ConnectedAtUtc,
            connection.StateObservedAtUtc,
            consent?.Version,
            consent?.GrantedAtUtc);
}

/// <summary>
/// The outcome of withdrawing a connection (design K9).
/// </summary>
/// <param name="RevokedAtProvider">
/// Whether the grant is confirmed gone from the provider's side. <b>False is not a failure</b> —
/// the local withdrawal happened either way — but it is not success either, and the screen says
/// so rather than reporting an unqualified success it cannot vouch for.
/// </param>
internal sealed record CalendarDisconnectResponse(
    CalendarConnectionResponse Connection,
    bool RevokedAtProvider);
