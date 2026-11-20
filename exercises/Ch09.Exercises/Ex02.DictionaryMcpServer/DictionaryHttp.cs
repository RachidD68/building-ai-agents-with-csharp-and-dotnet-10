using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace Ex02.DictionaryMcpServer;

/// <summary>
/// The dictionary over streamable HTTP: open (intermediate exercise) or as an
/// OAuth resource server with a scope per tool (challenge).
/// </summary>
public static class DictionaryHttp
{
    public static IServiceCollection AddOpenDictionaryMcp(this IServiceCollection services)
    {
        services.AddMcpServer(o => o.ServerInfo = new() { Name = "DictionaryServer", Version = "1.0.0" })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools([typeof(DictionaryTools)]);
        return services;
    }

    /// <param name="resource">This endpoint's URL: the audience tokens must carry.</param>
    /// <param name="issuer">The authorization server.</param>
    /// <param name="signingKey">Null: keys from the issuer's metadata. The self-test passes a local key.</param>
    public static IServiceCollection AddProtectedDictionaryMcp(
        this IServiceCollection services, string resource, string issuer, SecurityKey? signingKey = null)
    {
        services.AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                if (signingKey is null) o.Authority = issuer;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = issuer,
                    ValidAudience = resource,
                    IssuerSigningKey = signingKey,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            })
            .AddMcp(o => o.ResourceMetadata = new ProtectedResourceMetadata
            {
                Resource = resource,
                AuthorizationServers = [issuer],
                ScopesSupported = [.. DictionaryScopes.All],
            });

        services.AddAuthorization(o =>
        {
            foreach (string scope in DictionaryScopes.All)
                o.AddPolicy(scope, p => p.RequireAuthenticatedUser()
                    .RequireAssertion(c => DictionaryScopes.Has(c.User, scope)));
        });

        services.AddMcpServer(o => o.ServerInfo = new() { Name = "DictionaryServer", Version = "1.0.0" })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .AddAuthorizationFilters()
            .WithTools([typeof(ProtectedDictionaryTools)]);
        return services;
    }

    public static WebApplication MapDictionaryMcp(this WebApplication app, bool requireToken)
    {
        if (requireToken)
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapMcp("/mcp").RequireAuthorization();
        }
        else
        {
            app.MapMcp("/mcp");
        }
        return app;
    }
}
