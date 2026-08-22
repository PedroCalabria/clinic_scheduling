namespace Clinic.Domain.Configuration;

/// <summary>
/// Why the catalog refused a lifecycle change. Each value maps to exactly one code from
/// <c>docs/07-error-codes.md</c>.
/// </summary>
public enum CatalogRefusal
{
    /// <summary>Active records still reference the entity — <c>config.in_use</c>, 409.</summary>
    InUse = 1,

    /// <summary>An active entity of the same kind already holds the name — <c>config.duplicate_name</c>, 409.</summary>
    DuplicateName = 2,

    /// <summary>
    /// Something the entity itself points at is not active — <c>config.not_found</c>, 404
    /// (design D5).
    /// </summary>
    ReferenceInactive = 3,

    /// <summary>
    /// A duration was set for an appointment type whose specialty the professional does not
    /// hold — <c>config.specialty_not_held</c>, 422 (invariant I2's gate, design E2).
    /// </summary>
    SpecialtyNotHeld = 4,

    /// <summary>
    /// A working-hour segment collides with one already stored — <c>config.working_hours_overlap</c>,
    /// 409. A conflict needs BOTH the effective ranges and the times of day to overlap
    /// (design E5).
    /// </summary>
    WorkingHoursOverlap = 5,

    /// <summary>
    /// A working-hour segment is impossible rather than merely conflicting — its end is not
    /// after its start, which covers both a zero-length segment and one crossing midnight —
    /// <c>config.working_hours_invalid</c>, 422 (design E5).
    /// </summary>
    WorkingHoursInvalid = 6,
}

/// <summary>
/// A catalog rule said no, and named which one.
/// </summary>
/// <remarks>
/// Distinct from <see cref="DomainRuleViolationException"/>, which the change-2 slices catch
/// and answer with a single code they already know. The catalog needs more than that: one
/// endpoint can refuse for two different reasons — reactivation can fail on a taken name or
/// on an inactive reference — and those are different codes and different statuses. Carrying
/// the reason keeps the mapping in one place per slice instead of forcing the slice to
/// re-derive what it just asked the domain.
///
/// The message is for logs and developers only, never returned to a caller (Decision I).
/// </remarks>
public sealed class CatalogRuleViolationException(
    CatalogRefusal reason,
    string message,
    int? blockingRecords = null) : Exception(message)
{
    public CatalogRefusal Reason { get; } = reason;

    /// <summary>
    /// How many active records blocked the change, when the refusal was
    /// <see cref="CatalogRefusal.InUse"/>.
    /// </summary>
    /// <remarks>
    /// Carried so the API can answer with a number rather than a bare code: "this is used by
    /// three active records" tells an administrator whether they are one deactivation away from
    /// done or thirty. A list of names was considered and rejected — it has no length bound,
    /// and a refusal message is the wrong place to paginate.
    /// </remarks>
    public int? BlockingRecords { get; } = blockingRecords;
}
