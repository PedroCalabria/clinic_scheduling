using NodaTime;

namespace Clinic.Domain.Scheduling;

/// <summary>
/// How much notice is required to change an appointment (02-domain-model.md §5, F3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own type rather than a fourth field on <see cref="SchedulingParameters"/>, and that is
/// the decision this file exists to record.</b> Those three numbers — step, lead time, horizon —
/// are handed to the solver on the read <em>and</em> the write, precisely so that what
/// availability offers and what booking accepts cannot diverge. The cutoff is not that kind of
/// number. It does not decide what may be offered; it decides who may undo something already
/// agreed. Putting it in that record would hand the solver a field it must then be trusted to
/// ignore, and the day somebody helpfully applies it, availability starts withholding slots for a
/// reason unrelated to whether they are free.
/// </para>
/// <para>
/// <b>The rule takes an authority, not a role.</b> <see cref="Permits"/> is told whether the
/// cutoff applies to this caller and never asks who they are — the same bargain
/// <see cref="AppointmentBooking.ProfessionalHoldsDurationForType"/> struck: the aggregate is
/// handed conclusions the caller has already established rather than reaching for a lookup.
/// <c>Domain</c> has no notion of a role, no session, and no way to acquire either without
/// breaking the boundary the compiler enforces.
/// </para>
/// <para>
/// Today exactly one caller exists and it always passes <c>true</c>: a patient changing their own
/// appointment. The front desk acting <em>inside</em> the cutoff is <c>booking-desk</c>'s, and it
/// arrives by passing <c>false</c> — not by relaxing this rule, and not by changing this
/// signature. Building the parameter now rather than then is deliberate: a signature change later
/// is exactly where a caller quietly keeps the old behaviour.
/// </para>
/// </remarks>
public sealed record CancellationCutoffPolicy
{
    private CancellationCutoffPolicy(Duration notice) => Notice = notice;

    /// <summary>How far before its start an appointment stops being changeable.</summary>
    public Duration Notice { get; }

    /// <summary>
    /// Whether an appointment starting at <paramref name="startsAt"/> may still be changed.
    /// </summary>
    /// <param name="cutoffApplies">
    /// Whether this caller is subject to the cutoff at all. The one place authority enters the
    /// rule, and it enters as a fact rather than as an identity.
    /// </param>
    /// <remarks>
    /// The comparison is <c>&gt;=</c>, so an appointment starting exactly the notice away is still
    /// changeable. A cutoff is a <em>minimum notice</em>, and refusing at the boundary would mean
    /// "24 hours' notice" quietly meant "more than 24 hours' notice" — the kind of off-by-one that
    /// nobody notices until a patient is refused at 09:00 for a 09:00 appointment the next day.
    /// </remarks>
    public bool Permits(Instant startsAt, Instant now, bool cutoffApplies) =>
        !cutoffApplies || startsAt - now >= Notice;

    /// <summary>Builds the policy from the unit it is configured in.</summary>
    /// <exception cref="DomainRuleViolationException">The notice is not positive.</exception>
    public static CancellationCutoffPolicy Of(int hours)
    {
        if (hours <= 0)
        {
            // Not defensive. A cutoff of zero is not a lenient clinic — it is the rule disabled by
            // a typo, and it would look identical to a clinic that had configured nothing at all.
            throw new DomainRuleViolationException(
                "The cancellation cutoff must be a positive number of hours.");
        }

        return new CancellationCutoffPolicy(Duration.FromHours(hours));
    }
}
