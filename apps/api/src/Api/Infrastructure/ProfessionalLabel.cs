using System.Globalization;

namespace Clinic.Api.Infrastructure;

/// <summary>
/// How a professional is named to a person, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Four surfaces ask this question — the patient portal's booking options, a professional's own
/// schedule, the day view, and the staff booking surface — and they must not disagree. A patient
/// told "Dra Helena" on P2 and a receptionist told "helena.souza@..." on S4 would be looking at two
/// different people as far as either could tell.
/// </para>
/// <para>
/// <b>The stored name wins; the derived label is the fallback, not a policy.</b>
/// <c>booking-desk</c> put <c>full_name</c> on the <c>Professional</c> record (P-5,
/// <c>02-domain-model.md</c> §10) and it is nullable by decision (design N10): the record is born
/// on first configuration and S7 lists invited professionals who have none. So a professional
/// nobody has named yet still needs a label, and <c>booking-core</c>'s derivation is retained for
/// exactly that case.
/// </para>
/// <para>
/// The derivation turns <c>dra.helena@clinic.local</c> into "Dra Helena", which reads correctly for
/// the way clinics issue addresses and oddly for a generated one. The alternative was showing the
/// address, which reads worse and hands staff email addresses to patients for no reason — this
/// project stores the minimum patient data it can, and the courtesy runs both ways.
/// </para>
/// </remarks>
internal static class ProfessionalLabel
{
    /// <summary>The professional's stored name, or a label derived from their account address.</summary>
    public static string For(string? fullName, string email) =>
        string.IsNullOrWhiteSpace(fullName) ? Derive(email) : fullName;

    private static string Derive(string email)
    {
        var localPart = email.Split('@')[0];

        var words = localPart
            .Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word));

        var label = string.Join(' ', words);

        return string.IsNullOrWhiteSpace(label) ? email : label;
    }
}
