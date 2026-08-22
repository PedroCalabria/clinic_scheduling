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
