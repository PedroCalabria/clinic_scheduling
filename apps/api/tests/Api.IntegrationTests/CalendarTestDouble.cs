using System.Net;
using System.Text;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// Stands in for Google's token and revocation endpoints for the calendar flow (design K14).
/// </summary>
/// <remarks>
/// <para>
/// The same seam <see cref="GoogleTestDouble"/> uses, applied to the other flow: what is
/// substituted is Google's <em>transport</em>. The envelope, the state check, the scope
/// verification and the whole connection state machine run for real against whatever this class
/// returns — which is what makes CI able to cover this change with no Google credentials.
/// </para>
/// <para>
/// <b>What it can be made to do is the interesting part</b>, because each setting corresponds to
/// a real Google behaviour this change exists to survive:
/// <list type="bullet">
/// <item><see cref="NextRefreshToken"/> null — a successful authorization that carries no
/// credential, which is what Google does on any grant after the first (design K6).</item>
/// <item><see cref="NextGrantedScope"/> narrowed — the professional unticked calendar access on
/// a granular consent screen while approving the rest (design K5).</item>
/// <item><see cref="RefreshOutcome"/> — the three answers a check can get, including the one that
/// must NOT be recorded as a revocation (design K8).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class CalendarTestDouble
{
    /// <summary>The scope the app is configured to ask for, and the one a test can withhold.</summary>
    public const string CalendarScope = "https://www.googleapis.com/auth/calendar.events";

    /// <summary>What the next code exchange returns as its long-lived credential.</summary>
    public string? NextRefreshToken { get; set; } = "google-refresh-token-1//initial";

    /// <summary>
    /// What the next code exchange reports as granted. Set to something that does not include
    /// <see cref="CalendarScope"/> to reproduce a declined tickbox.
    /// </summary>
    public string NextGrantedScope { get; set; } = $"openid email profile {CalendarScope}";

    /// <summary>Set to make the code exchange itself fail, as a refusing token endpoint would.</summary>
    public bool FailExchange { get; set; }

    /// <summary>What a refresh-token grant (the connection check) answers with.</summary>
    public RefreshResult RefreshOutcome { get; set; } = RefreshResult.Valid;

    /// <summary>What the revocation endpoint answers with.</summary>
    public RevokeResult RevokeOutcome { get; set; } = RevokeResult.Accepted;

    /// <summary>Every refresh token this double was asked to revoke, in order.</summary>
    public List<string> Revoked { get; } = [];

    public enum RefreshResult
    {
        /// <summary>The credential still works.</summary>
        Valid,

        /// <summary>Google's answer for a revoked or expired refresh token: 400 invalid_grant.</summary>
        InvalidGrant,

        /// <summary>The endpoint could not be reached at all.</summary>
        Unreachable,

        /// <summary>A 400 that is NOT invalid_grant — must not be read as a revocation.</summary>
        OtherBadRequest,
    }

    public enum RevokeResult
    {
        Accepted,

        /// <summary>Already revoked — Google answers 400, and it means success.</summary>
        AlreadyInvalid,

        Unreachable,
    }

    /// <summary>
    /// Routes by endpoint, because one typed client serves the exchange, the check and the
    /// revocation — the same way one Google client serves them in production.
    /// </summary>
    internal sealed class Handler(CalendarTestDouble calendar) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var form = await ReadFormAsync(request, cancellationToken);

            if (url.Contains("revoke", StringComparison.OrdinalIgnoreCase))
            {
                return calendar.Revoke(form);
            }

            return form.TryGetValue("grant_type", out var grantType) && grantType == "refresh_token"
                ? calendar.Refresh()
                : calendar.Exchange();
        }

        private static async Task<Dictionary<string, string>> ReadFormAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is null)
            {
                return [];
            }

            var body = await request.Content.ReadAsStringAsync(cancellationToken);

            return body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(
                    parts => Uri.UnescapeDataString(parts[0]),
                    parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')));
        }
    }

    private HttpResponseMessage Exchange()
    {
        if (FailExchange)
        {
            return Json(HttpStatusCode.BadRequest, """{"error":"invalid_request"}""");
        }

        var refresh = NextRefreshToken is null
            ? string.Empty
            : $"""
               "refresh_token":"{NextRefreshToken}",
               """;

        return Json(HttpStatusCode.OK, $$"""
            {{{refresh}}"access_token":"access-token","scope":"{{NextGrantedScope}}","token_type":"Bearer","expires_in":3599}
            """);
    }

    private HttpResponseMessage Refresh() => RefreshOutcome switch
    {
        RefreshResult.Valid => Json(
            HttpStatusCode.OK,
            """{"access_token":"access-token","token_type":"Bearer","expires_in":3599}"""),

        RefreshResult.InvalidGrant => Json(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""),

        RefreshResult.OtherBadRequest => Json(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_client"}"""),

        _ => throw new HttpRequestException("The calendar token endpoint is unreachable in this test."),
    };

    private HttpResponseMessage Revoke(Dictionary<string, string> form)
    {
        if (form.TryGetValue("token", out var token))
        {
            Revoked.Add(token);
        }

        return RevokeOutcome switch
        {
            RevokeResult.Accepted => new HttpResponseMessage(HttpStatusCode.OK),
            RevokeResult.AlreadyInvalid => Json(HttpStatusCode.BadRequest, """{"error":"invalid_token"}"""),
            _ => throw new HttpRequestException("The revocation endpoint is unreachable in this test."),
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
