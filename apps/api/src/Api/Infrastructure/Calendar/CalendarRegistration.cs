using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Infrastructure.Calendar;

/// <summary>
/// Registers what the calendar connection needs, in one place so the pipeline in Program.cs
/// stays readable — the same shape <c>AddClinicAuth</c> established.
/// </summary>
internal static class CalendarRegistration
{
    internal static IServiceCollection AddClinicCalendar(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<CalendarOptions>()
            .Bind(configuration.GetSection(CalendarOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The conditional rule (design K4): silence about the calendar is a supported
        // deployment; a configured calendar with no usable key is a misconfiguration, and it
        // stops the host rather than degrading into storing a credential in clear.
        services.AddSingleton<IValidateOptions<CalendarOptions>, CalendarOptionsValidator>();

        // Singleton: it holds the decoded key and nothing per-request. Registered
        // unconditionally, and its constructor throws if resolved while the feature is off —
        // which is a programming error, not a configuration one, and is reported as one.
        services.TryAddSingleton<CalendarTokenProtector>();

        // The seam tests substitute, exactly like the sign-in flow's token exchange: CI needs no
        // Google credentials, while the envelope, the scope check and the state machine above it
        // all run for real (00-context.md §6).
        services.AddHttpClient<GoogleCalendarTokens>();

        // Scoped, because it holds the request's DbContext. Shared by S2's disconnect and by the
        // two account actions that end a professional's access — one implementation, so they
        // cannot drift apart about what withdrawal means.
        services.AddScoped<CalendarWithdrawal>();

        return services;
    }
}
