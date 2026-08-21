namespace Clinic.Domain.Identity;

/// <summary>
/// A record that a staff member reached a patient's personal data
/// (02-domain-model.md §LGPD).
/// </summary>
/// <remarks>
/// Append-only by construction: there is no method that changes a recorded entry and no
/// soft-delete marker, because an audit trail that can be edited answers a weaker question
/// than one that cannot.
///
/// A patient reading their own data produces nothing — see
/// <see cref="PatientDataAccessDecision.AllowedAsOwner"/>. Logging self-access would bury
/// the entries that matter in noise, and it is not the access LGPD asks about.
/// </remarks>
public sealed class AccessLog
{
    /// <summary>EF materialization only.</summary>
    private AccessLog()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The staff member who acted.</summary>
    public Guid ActorUserId { get; private set; }

    /// <summary>The patient whose data was reached.</summary>
    public Guid PatientId { get; private set; }

    public PatientDataAction Action { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static AccessLog Record(
        Guid actorUserId,
        Guid patientId,
        PatientDataAction action,
        DateTimeOffset occurredAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            ActorUserId = actorUserId,
            PatientId = patientId,
            Action = action,
            OccurredAtUtc = occurredAtUtc,
        };
}
