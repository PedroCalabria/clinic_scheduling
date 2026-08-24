using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Auth.Google;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The surface classification that decides which provisioning rule a Google sign-in gets
/// (design D1).
/// </summary>
/// <remarks>
/// No database and no host — these are unit tests, and they live in this project only because
/// the type they cover is <c>internal</c> to <c>Api</c> and <c>Domain</c> is not allowed to know
/// about web surfaces. Deliberately outside the fixture's collection so they do not queue behind
/// a container they never touch.
/// </remarks>
public sealed class SignInSurfaceTests
{
    [Theory]
    [InlineData("/staff")]
    [InlineData("/staff/")]
    [InlineData("/staff/users")]
    [InlineData("/staff/admin/professionals")]
    // Case-insensitive on purpose: it would not match the staff router either way, so the only
    // effect is that the restrictive branch is harder to slip past.
    [InlineData("/Staff")]
    public void A_path_under_the_staff_base_path_is_the_staff_surface(string returnPath) =>
        Assert.Equal(SignInSurface.Staff, SignInSurfaces.FromReturnPath(returnPath));

    [Theory]
    [InlineData("/")]
    [InlineData("/profile")]
    [InlineData("/appointments")]
    // The prefix is matched as a whole segment, so a portal route that merely starts with the
    // same letters is not the console.
    [InlineData("/staffroom")]
    [InlineData("/staff-directory")]
    public void Anything_else_is_the_patient_portal(string returnPath) =>
        Assert.Equal(SignInSurface.PatientPortal, SignInSurfaces.FromReturnPath(returnPath));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//evil.example/staff")]
    [InlineData("https://evil.example/staff")]
    [InlineData("\\\\evil.example\\staff")]
    public void An_unusable_return_path_lands_on_the_portal(string? requested)
    {
        // The classification only ever sees sanitized paths, so the property that matters is the
        // composition: junk is reduced to "/" first, and "/" is the surface with no privileges
        // attached. Asserted through SafeReturnPath rather than around it, because classifying a
        // raw request value is exactly the mistake this would be hiding.
        var sanitized = GoogleOAuthState.SafeReturnPath(requested);

        Assert.Equal(GoogleOAuthState.DefaultReturnPath, sanitized);
        Assert.Equal(SignInSurface.PatientPortal, SignInSurfaces.FromReturnPath(sanitized));
    }

    [Fact]
    public void A_pending_sign_in_carries_the_surface_of_the_path_it_started_with()
    {
        // The property the callback relies on: the surface is decided at START and survives the
        // cookie round-trip, so nothing in the callback request or the ID token can change it.
        var staff = GoogleOAuthState.Start("/staff/users");
        var portal = GoogleOAuthState.Start("/profile");

        Assert.Equal(SignInSurface.Staff, staff.Surface);
        Assert.Equal(SignInSurface.PatientPortal, portal.Surface);

        Assert.Equal(SignInSurface.Staff, GoogleOAuthState.FromCookieValue(staff.ToCookieValue())!.Surface);
        Assert.Equal(
            SignInSurface.PatientPortal,
            GoogleOAuthState.FromCookieValue(portal.ToCookieValue())!.Surface);
    }

    [Fact]
    public void The_staff_base_path_matches_the_one_the_frontend_is_served_under()
    {
        // The API's half of the base-path contract (00-context.md §"Base paths"). If someone
        // moves the staff app without moving this, the classification silently starts calling
        // every staff sign-in a patient one — so it fails here instead.
        Assert.Equal("/staff", SignInSurfaces.StaffBasePath);
    }
}
