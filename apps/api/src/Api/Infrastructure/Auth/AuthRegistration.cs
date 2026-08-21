using Clinic.Api.Infrastructure.Auth.Google;
using Clinic.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DomainPasswordHasher = Clinic.Domain.Identity.IPasswordHasher;

namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// Registers everything the session mechanism needs, in one place so the pipeline in
/// Program.cs stays readable (design A1).
/// </summary>
internal static class AuthRegistration
{
    internal static IServiceCollection AddClinicAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // TimeProvider rather than a hand-rolled IClock: it is in the framework, and tests
        // substitute a fake one without this project owning an abstraction for it.
        services.TryAddSingletonTimeProvider();

        services.AddScoped<SessionStore>();
        services.AddScoped<PatientDataGuard>();

        // The hasher is stateless and thread-safe, so one instance serves every request.
        services.AddSingleton<PasswordHasher<User>>();
        services.AddSingleton<DomainPasswordHasher, PasswordHasherAdapter>();

        // --- The federated path's two seams (design A4) ---------------------------------
        // Both are substituted in tests: the token exchange by replacing this typed client's
        // message handler, and the signing keys by replacing IGoogleSigningKeys. The validation
        // logic itself is never substituted, because it is the part worth testing.
        services.AddHttpClient<GoogleTokenExchange>();
        services.AddHttpClient(nameof(OpenIdConnectSigningKeys));
        services.TryAddSingleton<IGoogleSigningKeys, OpenIdConnectSigningKeys>();
        services.AddScoped<IGoogleIdTokenValidator, GoogleIdTokenValidator>();

        services
            .AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
                SessionAuthenticationHandler.SchemeName,
                configureOptions: null);

        services.AddAuthorizationBuilder().AddClinicAuthorization();

        return services;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(descriptor => descriptor.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }

    /// <summary>
    /// Applies the role policies and the authenticated-by-default fallback.
    /// </summary>
    /// <remarks>
    /// Wrapped so the builder-style registration stays a single expression above, and so the
    /// policy set has exactly one definition site (design A8).
    /// </remarks>
    private static AuthorizationBuilder AddClinicAuthorization(this AuthorizationBuilder builder)
    {
        builder.SetFallbackPolicy(
            new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        builder.AddPolicy(AuthorizationPolicies.Administrator,
            policy => policy.RequireRole(nameof(Role.Administrator)));

        builder.AddPolicy(AuthorizationPolicies.FrontDesk,
            policy => policy.RequireRole(nameof(Role.FrontDesk)));

        builder.AddPolicy(AuthorizationPolicies.ClinicStaff,
            policy => policy.RequireRole(nameof(Role.FrontDesk), nameof(Role.Administrator)));

        builder.AddPolicy(AuthorizationPolicies.Professional,
            policy => policy.RequireRole(nameof(Role.Professional)));

        builder.AddPolicy(AuthorizationPolicies.Patient,
            policy => policy.RequireRole(nameof(Role.Patient)));

        return builder;
    }
}
