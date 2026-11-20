using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;

namespace Ch09.McpConsumer;

/// <summary>
/// Client side of the trust boundary: a bearer token is sent only to an MCP
/// endpoint on the allow-list, and only after that server's protected resource
/// metadata names the same endpoint. A tool list, a redirect or a model's
/// suggestion can never add a destination.
/// </summary>
public sealed class McpDestinationGuard(IEnumerable<Uri> allowedEndpoints)
{
    private readonly HashSet<string> _allowed =
        [.. allowedEndpoints.Select(Normalize)];

    // An endpoint with a query string or a fragment is never allowed.
    public bool IsAllowed(Uri endpoint) =>
        endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0 && _allowed.Contains(Normalize(endpoint));

    /// <summary>Transport options that carry the token, or an exception before any request is sent.</summary>
    public HttpClientTransportOptions CreateTransportOptions(Uri endpoint, string accessToken)
    {
        if (!IsAllowed(endpoint))
            throw new InvalidOperationException($"MCP endpoint {endpoint} is not on the allow-list; the token is not sent.");
        return new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {accessToken}" },
        };
    }

    /// <summary>The consumer's path: the allow-list, then the server's metadata, and only then the token.</summary>
    public async Task<HttpClientTransportOptions> CreateCheckedTransportOptionsAsync(
        HttpClient http, Uri endpoint, string accessToken, CancellationToken ct = default)
    {
        await CheckResourceMetadataAsync(http, endpoint, ct);   // throws before the token is used
        return CreateTransportOptions(endpoint, accessToken);
    }

    /// <summary>
    /// Reads /.well-known/oauth-protected-resource/&lt;path&gt; (no token) and checks that
    /// its "resource" is the endpoint we meant to call.
    /// </summary>
    public async Task<string> CheckResourceMetadataAsync(HttpClient http, Uri endpoint, CancellationToken ct = default)
    {
        if (!IsAllowed(endpoint))
            throw new InvalidOperationException($"MCP endpoint {endpoint} is not on the allow-list.");
        Uri metadataUri = new(endpoint, "/.well-known/oauth-protected-resource" + endpoint.AbsolutePath);
        ResourceMetadata? metadata = await http.GetFromJsonAsync<ResourceMetadata>(metadataUri, ct);
        if (metadata?.Resource is null || Normalize(new Uri(metadata.Resource)) != Normalize(endpoint))
            throw new InvalidOperationException(
                $"Resource metadata names '{metadata?.Resource}', not {endpoint}; refusing to request a token for it.");
        return metadata.Resource;
    }

    // Scheme, host and port compare case-insensitively; the path compares as written.
    private static string Normalize(Uri uri) =>
        uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/');

    private sealed record ResourceMetadata([property: JsonPropertyName("resource")] string? Resource);
}
