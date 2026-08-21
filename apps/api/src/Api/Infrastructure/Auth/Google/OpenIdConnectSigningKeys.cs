using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Clinic.Api.Infrastructure.Auth.Google;

/// <summary>
/// Google's signing keys, discovered from its OpenID configuration and refreshed as they
/// rotate.
/// </summary>
/// <remarks>
/// <see cref="ConfigurationManager{T}"/> rather than fetching the JWKS by hand: key rotation
/// is exactly the kind of thing that works for months and then breaks at 3am, and this type
/// already handles caching, refresh intervals, and recovery from a failed fetch. Writing that
/// again would be re-implementing a solved problem in the one place where being subtly wrong
/// means nobody can sign in.
///
/// Registered as a singleton so the cache is shared and Google is not asked for its keys once
/// per request.
/// </remarks>
internal sealed class OpenIdConnectSigningKeys : IGoogleSigningKeys
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configuration;

    public OpenIdConnectSigningKeys(IOptions<AuthOptions> options, IHttpClientFactory httpClientFactory)
    {
        _configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
            options.Value.Google.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(httpClientFactory.CreateClient(nameof(OpenIdConnectSigningKeys))));
    }

    public async Task<IReadOnlyCollection<SecurityKey>> GetAsync(CancellationToken cancellationToken)
    {
        var configuration = await _configuration.GetConfigurationAsync(cancellationToken);

        return [.. configuration.SigningKeys];
    }
}
