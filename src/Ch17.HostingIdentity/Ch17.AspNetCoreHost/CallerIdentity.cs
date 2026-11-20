using System.Security.Claims;
using Ch06.SessionOwnership;

namespace Ch17.AspNetCoreHost;

/// <summary>
/// The one place the host turns a request into an identity key. It reads the
/// principal the JWT bearer handler validated (signature, issuer, audience,
/// lifetime), never the body, a header or a query string. The handler runs with
/// MapInboundClaims = false, so the token's "sub" and "tid" keep their names.
/// </summary>
public static class CallerIdentity
{
    public const string TenantClaim = "tid";
    public const string SubjectClaim = "sub";

    /// <summary>The (tenant, subject) key, or null when the token lacks either claim.</summary>
    public static AuthenticatedCaller? From(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.FindFirst(TenantClaim)?.Value is { Length: > 0 } tenant
        && user.FindFirst(SubjectClaim)?.Value is { Length: > 0 } subject
            ? new AuthenticatedCaller(tenant, subject)
            : null;

    /// <summary>
    /// The rate-limit partition: tenant and subject together, so two users behind
    /// one IP get two quotas and the same subject in two tenants gets two quotas.
    /// Only a caller with no identity key falls back to the address.
    /// </summary>
    public static QuotaKey Partition(HttpContext http) =>
        From(http.User) is { } caller
            ? new QuotaKey(caller.TenantId, caller.SubjectId)
            : new QuotaKey(null, http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
}

/// <summary>A quota partition. Tenant is null only for a caller without an identity key.</summary>
public sealed record QuotaKey(string? Tenant, string Subject);
