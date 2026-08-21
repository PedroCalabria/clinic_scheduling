using System.Net;
using System.Net.Http.Json;
using Clinic.Api.Infrastructure.Auth;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// An <see cref="HttpClient"/> that satisfies the API's request-forgery requirement, so tests
/// read as the behaviour they are checking rather than as CSRF plumbing.
/// </summary>
/// <remarks>
/// <para>
/// Every unsafe request has to echo the CSRF cookie in a header (design A3). A test that had
/// to arrange that by hand each time would be mostly ceremony, and worse, the ceremony would
/// be easy to copy wrongly — so it lives here once.
/// </para>
/// <para>
/// The base address is <c>https://</c> on purpose. The API sets <c>Secure</c> cookies
/// unconditionally, and a cookie container will not send those over plain HTTP; against the
/// in-memory test server the scheme costs nothing and makes the cookie behaviour realistic.
/// Getting this wrong presents as "the session cookie is set but never comes back", which is
/// exactly the symptom design A2 warns about in production.
/// </para>
/// </remarks>
public sealed class TestClient(HttpClient http, CookieContainer cookies, Uri baseAddress) : IDisposable
{
    /// <summary>The underlying client, for tests that need to shape a request themselves.</summary>
    public HttpClient Raw => http;

    public Task<HttpResponseMessage> GetAsync(string url) => http.GetAsync(url);

    public async Task<HttpResponseMessage> PostAsync(string url, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        await AttachCsrfAsync(request);

        return await http.SendAsync(request);
    }

    public async Task<HttpResponseMessage> PutAsync(string url, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        await AttachCsrfAsync(request);

        return await http.SendAsync(request);
    }

    /// <summary>
    /// Sends an unsafe request WITHOUT the CSRF header — the negative case that proves the
    /// defence is actually on.
    /// </summary>
    public Task<HttpResponseMessage> PostWithoutCsrfAsync(string url, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return http.SendAsync(request);
    }

    public void Dispose() => http.Dispose();

    /// <summary>
    /// Obtains the CSRF token the way the frontend does — from a safe request — and echoes it.
    /// </summary>
    private async Task AttachCsrfAsync(HttpRequestMessage request)
    {
        var token = await ResolveCsrfTokenAsync();

        if (token is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, token);
        }
    }

    /// <summary>
    /// Reads the token out of the cookie jar, priming it with a safe request if the jar is empty.
    /// </summary>
    /// <remarks>
    /// The jar rather than the <c>Set-Cookie</c> header, which is the subtle part: the server
    /// only issues the cookie when the request arrives without one, so any earlier safe request
    /// in the test already consumed the one chance to see it in a header. Reading the jar works
    /// whether this call primed the cookie or something before it did — the same thing a browser
    /// does.
    /// </remarks>
    private async Task<string?> ResolveCsrfTokenAsync()
    {
        var token = ReadCsrfCookie();

        if (token is not null)
        {
            return token;
        }

        // Health is used to prime because it is anonymous, so this works before there is a
        // session.
        using var response = await http.GetAsync("/api/health");
        _ = response;

        return ReadCsrfCookie();
    }

    private string? ReadCsrfCookie()
    {
        foreach (Cookie cookie in cookies.GetCookies(baseAddress))
        {
            if (cookie.Name == AuthCookies.Csrf && !string.IsNullOrEmpty(cookie.Value))
            {
                return cookie.Value;
            }
        }

        return null;
    }
}
