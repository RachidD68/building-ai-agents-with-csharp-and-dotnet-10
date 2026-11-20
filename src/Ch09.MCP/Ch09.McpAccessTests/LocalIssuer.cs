using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ch09.McpAccessTests;

/// <summary>
/// A stand-in authorization server that only signs tokens: a key generated for
/// this run, never written anywhere, and no identity provider. Tokens are never
/// printed.
/// </summary>
public sealed class LocalIssuer
{
    public const string Name = "https://issuer.invalid/local-test";   // .invalid: can never resolve

    public SymmetricSecurityKey Key { get; } = new(RandomNumberGenerator.GetBytes(32));

    public string Mint(string audience, string? scopes, TimeSpan? lifetime = null,
        SecurityKey? signWith = null, string issuer = Name)
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan life = lifetime ?? TimeSpan.FromMinutes(5);
        DateTime expires = now + life;
        // An expired token: issued and expired well before now (past the 30 s clock skew).
        DateTime issued = life > TimeSpan.Zero ? now : expires - TimeSpan.FromMinutes(5);

        Dictionary<string, object> claims = new() { ["sub"] = "team-b-agent" };
        if (scopes is not null) claims["scope"] = scopes;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = issued,
            NotBefore = issued,
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(signWith ?? Key, SecurityAlgorithms.HmacSha256),
        });
    }
}
