using NodaTime;

namespace Clinic.Domain.Scheduling;

/// <summary>
/// The three numbers that decide what availability may offer (02-domain-model.md §4, design F8).
/// </summary>
/// <remarks>
/// <para>
/// Configuration rather than code, because all three are clinic policy: how finely slots are
/// offered, how close to now a booking may be, and how far ahead the clinic takes work. The
/// domain holds them as a validated value so the solver cannot be handed a step of zero and
/// loop forever.
/// </para>
/// <para>
/// <see cref="MinimumLeadTime"/> and <see cref="Horizon"/> are invariant I8 — enforced at write
/// time in change 5. They are applied to the read as well, from this same value, because a read
/// that offers a slot the write will refuse is a lying read and SC-1 is the product's whole
/// claim.
/// </para>
/// <para>
/// <b>The cancellation cutoff is deliberately not a fourth number here</b>, and this note is where
/// a reader looking for it should find out why. Everything in this record is handed to the solver,
/// which is the point — the read and the write cannot apply different rules if there is one value.
/// The cutoff does not decide what may be offered, only who may undo, so a solver holding it would
/// be a solver eventually applying it, at which point availability withholds slots for a reason
/// that has nothing to do with whether they are free. It lives on
/// <see cref="CancellationCutoffPolicy"/> instead.
/// </para>
/// </remarks>
public sealed record SchedulingParameters
{
    private SchedulingParameters(Duration slotStartStep, Duration minimumLeadTime, Duration horizon)
    {
        SlotStartStep = slotStartStep;
        MinimumLeadTime = minimumLeadTime;
        Horizon = horizon;
    }

    /// <summary>
    /// How far apart consecutive candidate starts are placed.
    /// </summary>
    /// <remarks>
    /// Independent of the appointment's length, which is what makes candidate slots overlap: a
    /// 40-minute visit in 09:00-12:00 is offered at 09:00, 09:15, 09:30 and so on. Correct for a
    /// read whose slots are not reservations — the patient picks a convenient time, and booking
    /// one removes its neighbours.
    /// </remarks>
    public Duration SlotStartStep { get; }

    /// <summary>How soon from now a slot may start. Zero is legitimate — some clinics take walk-ins.</summary>
    public Duration MinimumLeadTime { get; }

    /// <summary>How far ahead the clinic accepts bookings, measured from now.</summary>
    public Duration Horizon { get; }

    /// <summary>
    /// Builds the parameters from the units they are configured in.
    /// </summary>
    /// <exception cref="DomainRuleViolationException">
    /// The step or the horizon is not positive, or the lead time is negative.
    /// </exception>
    public static SchedulingParameters Of(int slotStartStepMinutes, int minimumLeadTimeMinutes, int horizonDays)
    {
        if (slotStartStepMinutes <= 0)
        {
            // Not defensive: a step of zero is an infinite loop in the solver, and the operator
            // who typed it deserves a startup failure rather than a hung request.
            throw new DomainRuleViolationException("The slot start step must be a positive number of minutes.");
        }

        if (minimumLeadTimeMinutes < 0)
        {
            throw new DomainRuleViolationException("The minimum lead time cannot be negative.");
        }

        if (horizonDays <= 0)
        {
            throw new DomainRuleViolationException("The scheduling horizon must be a positive number of days.");
        }

        return new SchedulingParameters(
            Duration.FromMinutes(slotStartStepMinutes),
            Duration.FromMinutes(minimumLeadTimeMinutes),
            Duration.FromDays(horizonDays));
    }
}
