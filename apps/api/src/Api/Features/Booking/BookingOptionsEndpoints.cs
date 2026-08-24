using System.Globalization;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Api.Infrastructure.Time;
using Clinic.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Features.Booking;

/// <summary>
/// What a patient may choose from on P2 — specialties, kinds of visit, and who offers them.
/// </summary>
/// <param name="DisplayName">
/// <b>A seam, not a finished feature.</b> <c>Professional</c> carries no name: 3b created the
/// record with only a user reference, and S7 lists professionals by email because an administrator
/// already knows their own staff. A patient does not, and showing them an internal email address
/// would both read badly and hand out staff addresses for no reason — so the server derives a label
/// from the account's local part here.
/// <para>
/// The contract says <c>displayName</c> rather than <c>email</c> precisely so that when a real name
/// belongs on the <c>Professional</c> record, only the server changes and no client has to.
/// </para>
/// <para>
/// <b>That change has an owner:</b> <c>booking-lifecycle</c> (5b) adds <c>Professional.fullName</c>
/// with a field on S7 — recorded as P-5 in <c>02-domain-model.md</c> §10 and in the build order. It
/// lands there because 5b needs it three times over (S1, S4, S5) rather than because booking asked
/// for it. When it does, the only edit here is where the label comes from.
/// </para>
/// </param>
internal sealed record BookableProfessional(Guid ProfessionalId, string DisplayName);

/// <summary>One kind of visit, and who can deliver it.</summary>
internal sealed record BookableAppointmentType(
    Guid AppointmentTypeId,
    string Name,
    Guid SpecialtyId,
    IReadOnlyList<BookableProfessional> Professionals);

/// <summary>A specialty a patient can actually book something in.</summary>
internal sealed record BookableSpecialty(
    Guid SpecialtyId,
    string Name,
    IReadOnlyList<BookableAppointmentType> AppointmentTypes);

/// <summary>Everything the booking flow needs to render itself, in one answer.</summary>
/// <param name="Timezone">
/// The clinic's configured zone (Decision H).
/// <para>
/// Carried here as well as on the availability response, and that is not redundancy: <b>P3 renders
/// times without asking for availability again</b>, so without this it would have nothing to
/// convert against but the browser's own zone — which is the exact bug instants are stored to
/// avoid, reintroduced on the last screen before the commit. Every booking surface can now say
/// which clock it means from one request it already makes.
/// </para>
/// </param>
internal sealed record BookingOptionsResponse(
    string Timezone,
    IReadOnlyList<BookableSpecialty> Specialties);

/// <summary>
/// <c>GET /api/booking/options</c> — the catalogue as a patient sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added by <c>booking-core</c> because P2 could not exist without it, and no earlier change
/// needed it.</b> Every catalogue read so far is <c>/api/config/*</c> behind the administrator
/// policy, which is right for the screens that manage the catalogue and useless for the screen that
/// consumes it. A patient cannot be given the administrator policy, and widening those endpoints
/// would mean one route serving two audiences with different shapes.
/// </para>
/// <para>
/// <b>One endpoint rather than three.</b> P2 needs specialty, appointment type and professional at
/// once to render its first frame; three round trips on the flagship screen is a worse first
/// impression than one slightly larger answer, and the nesting is what lets the second and third
/// selects populate without another request.
/// </para>
/// <para>
/// <b>Only what is genuinely bookable.</b> An appointment type nobody holds a duration for, and a
/// specialty whose every type is like that, are filtered out — otherwise P2 offers a path that
/// always ends in an empty result, and the patient reasonably concludes the clinic is broken rather
/// than unstaffed. The filter reuses 3b's qualification gate, so it says nothing new: an active
/// <c>ProfessionalAppointmentType</c> is exactly "somebody can do this".
/// </para>
/// <para>
/// Readable by any authenticated caller, like availability itself: this is the clinic's service
/// catalogue, never patient data. Anonymous is refused by the app-wide default.
/// </para>
/// </remarks>
internal static class BookingOptionsEndpoints
{
    internal static IEndpointRouteBuilder MapBookingOptionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/booking/options", ListAsync)
            .RequireAuthorization()
            .WithName("ListBookingOptions");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ClinicDbContext database,
        ClinicTimezone timezone,
        CancellationToken cancellationToken)
    {
        // One query for the whole tree: active durations, joined out to the professional's account,
        // the appointment type, and its specialty. The join to ProfessionalAppointmentType is what
        // makes "bookable" mean "somebody is qualified" rather than "the row exists".
        var rows = await database.ProfessionalAppointmentTypes
            .AsNoTracking()
            .Where(duration => duration.DeactivatedAtUtc == null)
            .Join(
                database.Professionals.Where(professional => professional.DeactivatedAtUtc == null),
                duration => duration.ProfessionalId,
                professional => professional.Id,
                (duration, professional) => new { duration.AppointmentTypeId, professional.Id, professional.UserId })
            .Join(
                database.Users,
                entry => entry.UserId,
                user => user.Id,
                (entry, user) => new { entry.AppointmentTypeId, ProfessionalId = entry.Id, user.Email })
            .Join(
                database.AppointmentTypes.Where(type => type.DeactivatedAtUtc == null),
                entry => entry.AppointmentTypeId,
                type => type.Id,
                (entry, type) => new { entry.ProfessionalId, entry.Email, Type = type })
            .Join(
                database.Specialties.Where(specialty => specialty.DeactivatedAtUtc == null),
                entry => entry.Type.SpecialtyId,
                specialty => specialty.Id,
                (entry, specialty) => new
                {
                    entry.ProfessionalId,
                    entry.Email,
                    TypeId = entry.Type.Id,
                    TypeName = entry.Type.Name,
                    SpecialtyId = specialty.Id,
                    SpecialtyName = specialty.Name,
                })
            .ToListAsync(cancellationToken);

        var specialties = rows
            .GroupBy(row => new { row.SpecialtyId, row.SpecialtyName })
            .OrderBy(group => group.Key.SpecialtyName)
            .Select(group => new BookableSpecialty(
                group.Key.SpecialtyId,
                group.Key.SpecialtyName,
                group
                    .GroupBy(row => new { row.TypeId, row.TypeName })
                    .OrderBy(types => types.Key.TypeName)
                    .Select(types => new BookableAppointmentType(
                        types.Key.TypeId,
                        types.Key.TypeName,
                        group.Key.SpecialtyId,
                        types
                            .Select(row => new BookableProfessional(row.ProfessionalId, DisplayName(row.Email)))
                            .DistinctBy(professional => professional.ProfessionalId)
                            .OrderBy(professional => professional.DisplayName)
                            .ToList()))
                    .ToList()))
            .ToList();

        return Results.Ok(new BookingOptionsResponse(timezone.Id, specialties));
    }

    /// <summary>
    /// A patient-facing label for a professional, derived from their account address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Interim, and the shape of the interim matters.</b> <c>dra.helena@clinic.local</c> becomes
    /// "Dra Helena", which reads correctly for the way clinics actually issue addresses and oddly
    /// for a generated one. The alternative was showing the address itself, which reads worse and
    /// hands staff email addresses to patients for no reason at all — a small thing, but this
    /// project stores the minimum patient data it can and the courtesy runs both ways.
    /// </para>
    /// <para>
    /// The real fix is a name on the <c>Professional</c> record, entered in S7, and it is scheduled:
    /// <c>booking-lifecycle</c> (5b) owns it as P-5. This stays a derivation rather than a column
    /// added in passing by a booking change, and it is accepted as good enough <em>because</em> the
    /// replacement is on the board — not because deriving a name from an address is right.
    /// </para>
    /// </remarks>
    private static string DisplayName(string email)
    {
        var localPart = email.Split('@')[0];

        var words = localPart
            .Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word));

        var label = string.Join(' ', words);

        return string.IsNullOrWhiteSpace(label) ? email : label;
    }
}
