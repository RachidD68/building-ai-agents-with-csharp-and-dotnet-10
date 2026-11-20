using System.Security.Claims;

namespace Ch09.ProtectedMcpServer;

/// <summary>
/// One OAuth scope per tool, and one authorization policy per scope (same name).
/// </summary>
public static class CatalogScopes
{
    public const string Read = "catalog.read";          // ProductLookup
    public const string Inventory = "inventory.read";   // InventoryCheck

    public static readonly string[] All = [Read, Inventory];

    /// <summary>
    /// True when the token grants <paramref name="scope"/>. OAuth puts scopes in one
    /// space-separated "scope" claim; Microsoft Entra ID uses "scp". Read both.
    /// </summary>
    public static bool Has(ClaimsPrincipal user, string scope) =>
        user.FindAll("scope").Concat(user.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}
