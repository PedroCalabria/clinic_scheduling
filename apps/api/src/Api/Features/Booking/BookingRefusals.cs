using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence.Configurations;
using Clinic.Domain.Scheduling;
using Npgsql;

namespace Clinic.Api.Features.Booking;

/// <summary>
/// Turns a booking refusal into the one code and status that name it
/// (docs/07-error-codes.md).
/// </summary>
/// <remarks>
/// The same shape as <c>CatalogRefusals</c>, and here for the same reason: one endpoint refuses
/// for eight distinct causes carrying different codes and different statuses, so the mapping lives
/// in one place instead of being re-derived where each refusal is raised. Two producers reach it —
/// the solver's <c>Explain</c> and the aggregate's factory — and they share the enum precisely so
/// that a caller cannot tell which layer said no, nor need to.
/// </remarks>
internal static class BookingRefusals
{
    /// <summary>Maps a refusal to its response.</summary>
    internal static IResult ToResult(this BookingRefusal refusal) => refusal switch
    {
        // 422 — well-formed, the data exists, a business rule says no. The remedy is to pick a
        // different time or a different professional.
        BookingRefusal.OutsideWorkingHours =>
            ApiError.Result(ErrorCodes.BookingOutsideWorkingHours, StatusCodes.Status422UnprocessableEntity),

        BookingRefusal.LeadTimeViolation =>
            ApiError.Result(ErrorCodes.BookingLeadTimeViolation, StatusCodes.Status422UnprocessableEntity),

        BookingRefusal.HorizonExceeded =>
            ApiError.Result(ErrorCodes.BookingHorizonExceeded, StatusCodes.Status422UnprocessableEntity),

        BookingRefusal.SpecialtyMismatch =>
            ApiError.Result(ErrorCodes.BookingSpecialtyMismatch, StatusCodes.Status422UnprocessableEntity),

        // 409 — a conflict with state that exists. The three that a race can also produce, which
        // is why the constraint mapping below answers with exactly these.
        BookingRefusal.SlotBlocked =>
            ApiError.Result(ErrorCodes.BookingSlotBlocked, StatusCodes.Status409Conflict),

        BookingRefusal.SlotTaken =>
            ApiError.Result(ErrorCodes.BookingSlotTaken, StatusCodes.Status409Conflict),

        BookingRefusal.PatientBusy =>
            ApiError.Result(ErrorCodes.BookingPatientBusy, StatusCodes.Status409Conflict),

        BookingRefusal.ResourceUnavailable =>
            ApiError.Result(ErrorCodes.BookingResourceUnavailable, StatusCodes.Status409Conflict),

        // Unreachable while the enum and this switch agree; if a value is added without a
        // mapping, failing loudly in development beats emitting a code the frontend cannot
        // translate.
        _ => throw new InvalidOperationException($"Unmapped booking refusal: {refusal}."),
    };

    /// <summary>
    /// The PostgreSQL error code for an exclusion-constraint violation.
    /// </summary>
    /// <remarks>
    /// <c>23P01</c>, <c>exclusion_violation</c>. Matched on the code rather than the message,
    /// because the message is localised by the server's own configuration.
    /// </remarks>
    private const string ExclusionViolation = "23P01";

    /// <summary>
    /// Which invariant a racing insert broke, or null if the failure was not an exclusion
    /// violation at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Mapped by constraint name, which makes the names part of the contract</b> — so they are
    /// constants shared with the migration and asserted by an integration test. A rename would
    /// otherwise silently degrade three specific, actionable answers into <c>server.unexpected</c>,
    /// and nothing in a functional test would notice.
    /// </para>
    /// <para>
    /// Reaching here at all means the pre-commit check passed and another transaction committed in
    /// between, so this is the genuine race — the case the constraints exist for and the
    /// application check cannot close. The codes are deliberately the same ones the pre-commit
    /// refusals use: the caller sees the same failure and has the same remedy, and one code per
    /// user-meaningful failure is the catalogue's own rule.
    /// </para>
    /// </remarks>
    internal static BookingRefusal? RacedOn(this Exception exception)
    {
        var postgres = exception.Postgres();

        if (postgres is null || postgres.SqlState != ExclusionViolation)
        {
            return null;
        }

        return postgres.ConstraintName switch
        {
            AppointmentConfiguration.ProfessionalExclusion => BookingRefusal.SlotTaken,

            // Not retried with the next candidate room (design B8). The server chose a free one a
            // moment earlier, so this means another professional's booking took it in between —
            // rare in one clinic, and a savepoint loop is the recorded fix if it stops being rare.
            AppointmentConfiguration.ResourceExclusion => BookingRefusal.ResourceUnavailable,

            AppointmentConfiguration.PatientExclusion => BookingRefusal.PatientBusy,

            // An exclusion violation on a constraint nobody named. Better reported as an
            // unexpected failure than guessed at.
            _ => null,
        };
    }

    /// <summary>Deadlock detected.</summary>
    private const string Deadlock = "40P01";

    /// <summary>Serialization failure.</summary>
    private const string SerializationFailure = "40001";

    /// <summary>
    /// Whether the database rolled the transaction back for a concurrency reason that retrying
    /// resolves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Discovered while writing the concurrency tests, and it is not a rare edge (design B8).</b>
    /// Two bookings for the same patient with two different professionals in the same room conflict
    /// on <em>two</em> exclusion constraints at once. Each transaction inserts its heap tuple before
    /// either finishes checking indexes, so each ends up waiting on the other's tuple and
    /// PostgreSQL breaks the cycle by killing one with <c>40P01</c>. The professional-scoped lock
    /// cannot prevent it: the two transactions hold <em>different</em> professionals' locks, which is
    /// exactly the concurrency that lock is designed to allow.
    /// </para>
    /// <para>
    /// A deadlock is not a business outcome, so it must not become a business code. The victim's
    /// transaction was rolled back entirely, so retrying re-reads committed state and produces the
    /// <em>correct specific</em> answer — the winner's row is now visible, so the retry either
    /// refuses with <c>slot_taken</c> / <c>resource_unavailable</c> / <c>patient_busy</c>, or
    /// succeeds because the conflict was never real. Guessing a code from the deadlock itself would
    /// sometimes lie, since it does not say which constraint would have refused.
    /// </para>
    /// <para>
    /// <c>40001</c> is included for completeness. Nothing in this change runs at
    /// <c>SERIALIZABLE</c> — domain-model G1 chose the professional lock over it precisely to avoid
    /// imposing retries on the hot path — but the remedy is identical if anything ever does.
    /// </para>
    /// </remarks>
    internal static bool IsConcurrencyRollback(this Exception exception) =>
        exception.Postgres()?.SqlState is Deadlock or SerializationFailure;

    /// <summary>
    /// The PostgreSQL error anywhere in the exception chain.
    /// </summary>
    /// <remarks>
    /// <b>The whole chain, not one level.</b> EF Core classifies some Npgsql failures as transient
    /// and wraps them again: a deadlock arrives as
    /// <c>InvalidOperationException("...likely due to a transient failure") -> DbUpdateException ->
    /// PostgresException</c>, three deep. A one-level unwrap misses it, and the symptom is a
    /// <c>500</c> where a specific 409 belonged — which is what the concurrency tests caught.
    /// </remarks>
    private static PostgresException? Postgres(this Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
