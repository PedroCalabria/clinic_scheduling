using System.ComponentModel.DataAnnotations;
using Clinic.Domain.Scheduling;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Infrastructure.Time;

/// <summary>
/// The availability read's policy numbers, bound from the <c>Scheduling</c> section — so
/// <c>Scheduling__SlotStartStepMinutes</c> as an environment variable (see <c>.env.example</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>These carry defaults, and <see cref="ClinicTimeOptions"/> deliberately does not.</b> The
/// contrast is the point rather than an inconsistency: a timezone default is wrong for every
/// clinic but one, and wrong <em>silently</em> — hours recorded correctly and interpreted against
/// the wrong zone. A 15-minute slot step is right until a clinic says otherwise, and being wrong
/// about it is visible the moment somebody looks at the offered times. Same mechanism, opposite
/// call, because the failure modes are not comparable.
/// </para>
/// <para>
/// The practical consequence is that a deployment which restarts without these still starts,
/// which is why this change adds no line to the README's local-run section.
/// </para>
/// </remarks>
internal sealed class SchedulingOptions
{
    internal const string SectionName = "Scheduling";

    /// <summary>How far apart candidate slot starts are placed (02-domain-model.md §4).</summary>
    [Range(1, 24 * 60)]
    public int SlotStartStepMinutes { get; set; } = 15;

    /// <summary>How soon from now a slot may start. Zero permits immediate booking.</summary>
    [Range(0, 7 * 24 * 60)]
    public int MinimumLeadTimeMinutes { get; set; } = 60;

    /// <summary>How far ahead the clinic accepts bookings.</summary>
    [Range(1, 2 * 365)]
    public int HorizonDays { get; set; } = 60;

    /// <summary>
    /// The widest window a single availability request may ask for.
    /// </summary>
    /// <remarks>
    /// Smaller than the horizon on purpose. The response grows with window × professionals ÷
    /// step, and this is the only thing bounding it; a month at a time is a natural read, and two
    /// requests still cover the whole horizon. Exceeding it is
    /// <c>availability.window_invalid</c> rather than a silent truncation — a read that quietly
    /// answers a narrower question than it was asked is worse than one that refuses.
    /// </remarks>
    [Range(1, 366)]
    public int MaxWindowDays { get; set; } = 31;

    /// <summary>
    /// How much notice a patient must give to cancel or reschedule (02-domain-model.md §5, F3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Configured here and deliberately not carried in <see cref="ClinicScheduling.Parameters"/>.</b>
    /// The three numbers above decide what availability may <em>offer</em>, and they are handed to
    /// the solver on the read and the write alike so the two cannot apply different rules. This one
    /// decides who may <em>undo</em>. The solver has no use for it, and a solver holding it would
    /// eventually have it applied — at which point availability starts hiding slots for a reason
    /// that has nothing to do with whether they are free.
    /// </para>
    /// <para>
    /// A default, like its neighbours: a clinic that has not said otherwise gets 24 hours and
    /// starts. The range refuses zero, because a cutoff of nothing is not a lenient policy — it is
    /// the rule silently disabled, and the operator who typed it deserves a startup failure rather
    /// than a discovery.
    /// </para>
    /// </remarks>
    [Range(1, 30 * 24)]
    public int CancellationCutoffHours { get; set; } = 24;

    /// <summary>
    /// Availability requests permitted per caller per minute (03-nfr.md §2).
    /// </summary>
    /// <remarks>
    /// Lives here rather than with the auth options because it bounds this slice's query cost,
    /// not a credential guess. The login limiter defends a different thing and keeps its own
    /// number.
    /// </remarks>
    [Range(1, 10_000)]
    public int AvailabilityRequestsPerMinute { get; set; } = 60;
}

/// <summary>
/// The scheduling policy, resolved once and validated by the domain.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ClinicTimezone"/>: configuration is parsed and checked at startup, and
/// everything downstream reads a value that cannot be malformed. The domain's own factory does
/// the checking, so "a step of zero" is refused by the same rule whether it arrives from
/// configuration or from a test.
/// </remarks>
internal sealed class ClinicScheduling
{
    public ClinicScheduling(IOptions<SchedulingOptions> options)
    {
        var value = options.Value;

        Parameters = SchedulingParameters.Of(
            value.SlotStartStepMinutes,
            value.MinimumLeadTimeMinutes,
            value.HorizonDays);

        CancellationCutoff = CancellationCutoffPolicy.Of(value.CancellationCutoffHours);
        MaxWindowDays = value.MaxWindowDays;
        AvailabilityRequestsPerMinute = value.AvailabilityRequestsPerMinute;
    }

    /// <summary>What the solver reads.</summary>
    /// <remarks>
    /// The cancellation cutoff is <b>not</b> in here, and its absence is the design rather than an
    /// oversight — see <see cref="SchedulingOptions.CancellationCutoffHours"/> and
    /// <see cref="CancellationCutoff"/>.
    /// </remarks>
    public SchedulingParameters Parameters { get; }

    /// <summary>What the two lifecycle transitions read, and the solver never does.</summary>
    public CancellationCutoffPolicy CancellationCutoff { get; }

    /// <summary>What the endpoint validates the requested window against.</summary>
    public int MaxWindowDays { get; }

    /// <summary>What the limiter permits.</summary>
    public int AvailabilityRequestsPerMinute { get; }
}
