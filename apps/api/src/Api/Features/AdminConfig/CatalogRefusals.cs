using Clinic.Api.Infrastructure.Errors;
using Clinic.Domain;
using Clinic.Domain.Configuration;

namespace Clinic.Api.Features.AdminConfig;

/// <summary>
/// Turns a domain refusal into the one code and status that name it
/// (docs/07-error-codes.md).
/// </summary>
/// <remarks>
/// The change-2 slices each catch <see cref="DomainRuleViolationException"/> and answer with a
/// code they already know, because each of those endpoints could only refuse for one reason.
/// The catalog cannot do that: reactivation can fail on a taken name <em>or</em> on an inactive
/// reference, and those are different codes and different statuses. So the reason travels with
/// the exception and the mapping lives here, once, instead of being re-derived in twenty
/// handlers.
/// </remarks>
internal static class CatalogRefusals
{
    /// <summary>Maps a catalog refusal to its response.</summary>
    internal static IResult ToResult(this CatalogRuleViolationException refusal) => refusal.Reason switch
    {
        // The count travels with the refusal so the screen can say how much is in the way,
        // rather than only that something is.
        CatalogRefusal.InUse =>
            ApiError.Result(
                ErrorCodes.ConfigInUse,
                StatusCodes.Status409Conflict,
                refusal.BlockingRecords is { } blocking
                    ? new Dictionary<string, object?> { ["records"] = blocking }
                    : null),

        CatalogRefusal.DuplicateName =>
            ApiError.Result(ErrorCodes.ConfigDuplicateName, StatusCodes.Status409Conflict),

        CatalogRefusal.ReferenceInactive =>
            ApiError.Result(ErrorCodes.ConfigNotFound, StatusCodes.Status404NotFound),

        // 422 rather than 409: the request is well-formed and the data exists, it just breaks a
        // business rule — the same status the booking codes use for that shape of refusal.
        CatalogRefusal.SpecialtyNotHeld =>
            ApiError.Result(ErrorCodes.ConfigSpecialtyNotHeld, StatusCodes.Status422UnprocessableEntity),

        CatalogRefusal.WorkingHoursOverlap =>
            ApiError.Result(ErrorCodes.ConfigWorkingHoursOverlap, StatusCodes.Status409Conflict),

        CatalogRefusal.WorkingHoursInvalid =>
            ApiError.Result(ErrorCodes.ConfigWorkingHoursInvalid, StatusCodes.Status422UnprocessableEntity),

        // Unreachable while the enum and this switch agree; if a value is added without a
        // mapping, failing loudly in development beats emitting a code the frontend cannot
        // translate.
        _ => throw new InvalidOperationException($"Unmapped catalog refusal: {refusal.Reason}."),
    };

    /// <summary>The catalog entity the caller named does not exist, or is not active.</summary>
    internal static IResult NotFound() =>
        ApiError.Result(ErrorCodes.ConfigNotFound, StatusCodes.Status404NotFound);

    /// <summary>A required field was missing.</summary>
    internal static IResult Required(string field) =>
        ApiError.Result(
            ErrorCodes.ValidationRequired,
            StatusCodes.Status400BadRequest,
            new Dictionary<string, object?> { ["field"] = field });

    /// <summary>
    /// A field was present but unusable — a name past the column width, a negative buffer.
    /// </summary>
    /// <remarks>
    /// This is where <see cref="DomainRuleViolationException"/> lands. The domain refuses what
    /// is structurally impossible and the API names the field, which keeps the prose out of the
    /// API (Decision I) while still telling the frontend what to highlight.
    /// </remarks>
    internal static IResult Invalid(string field) =>
        ApiError.Result(
            ErrorCodes.ValidationInvalidFormat,
            StatusCodes.Status400BadRequest,
            new Dictionary<string, object?> { ["field"] = field });
}
