using System.Net;
using System.Security.Cryptography;
using System.Text;
using Clinic.Api.Infrastructure.Auth.Google;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// Stands in for Google, without standing in for the validation of what Google returns
/// (design A4).
/// </summary>
/// <remarks>
/// <para>
/// Two seams, and it matters that they are exactly these two. The token EXCHANGE is stubbed,
/// because it is an HTTP call to a host the test cannot reach. The signing KEYS are replaced,
/// so tokens can be minted locally. Everything in between — signature verification, the
/// issuer, audience and expiry checks, the nonce comparison, the <c>email_verified</c>
/// insistence — runs for real, against a token this class produced.
/// </para>
/// <para>
/// Swapping <see cref="IGoogleIdTokenValidator"/> itself would have been easier and would have
/// tested nothing: the validation logic is the part most worth exercising, precisely because
/// its failure mode is "accepts a token it should have refused".
/// </para>
/// </remarks>
public sealed class GoogleTestDouble : IDisposable
{
    /// <summary>Values the fixture also feeds to the app as configuration, so both agree.</summary>
    public const string Issuer = "https://accounts.google.test";

    public const string ClientId = "clinic-test-client.apps.googleusercontent.test";

    private readonly RSA _key = RSA.Create(2048);

    /// <summary>The next ID token the stubbed exchange will return.</summary>
    /// <remarks>
    /// Mutable state shared through the fixture. Safe because xunit runs the tests in a
    /// collection one at a time; a test that needs isolation from this owns its own host.
    /// </remarks>
    public string? NextIdToken { get; set; }

    /// <summary>Set to make the exchange itself fail, as a dead or refusing token endpoint would.</summary>
    public bool FailExchange { get; set; }

    public SecurityKey PublicKey => new RsaSecurityKey(_key.ExportParameters(includePrivateParameters: false));

    /// <summary>
    /// Mints an ID token. Every claim is a parameter so a test can make exactly one thing
    /// wrong — which is how the negative cases prove the validator is doing its job.
    /// </summary>
    public string MintIdToken(
        string subject,
        string email,
        string nonce,
        bool emailVerified = true,
        string? fullName = null,
        string? issuer = null,
        string? audience = null,
        DateTime? expiresAtUtc = null,
        RSA? signingKey = null)
    {
        var credentials = new SigningCredentials(
            new RsaSecurityKey(signingKey ?? _key),
            SecurityAlgorithms.RsaSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? ClientId,
            Expires = expiresAtUtc ?? DateTime.UtcNow.AddMinutes(5),
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["email"] = email,
                ["email_verified"] = emailVerified,
                ["nonce"] = nonce,
                ["name"] = fullName ?? string.Empty,
            },
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>An RSA key that is NOT the one the app trusts, for the bad-signature case.</summary>
    public static RSA UntrustedKey() => RSA.Create(2048);

    public void Dispose() => _key.Dispose();

    /// <summary>Feeds the app this double's public key instead of Google's JWKS.</summary>
    internal sealed class SigningKeys(GoogleTestDouble google) : IGoogleSigningKeys
    {
        public Task<IReadOnlyCollection<SecurityKey>> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<SecurityKey>>([google.PublicKey]);
    }

    /// <summary>
    /// Stands in for Google's token endpoint: returns whatever ID token the test staged.
    /// </summary>
    internal sealed class TokenEndpointHandler(GoogleTestDouble google) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (google.FailExchange || google.NextIdToken is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
                });
            }

            var body = $$"""{"id_token":"{{google.NextIdToken}}","token_type":"Bearer","expires_in":3599}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
