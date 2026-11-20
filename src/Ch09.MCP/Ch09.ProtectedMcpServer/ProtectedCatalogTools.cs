using System.ComponentModel;
using System.Text.Json;
using Ch09.McpServer;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace Ch09.ProtectedMcpServer;

/// <summary>
/// The same two catalog tools, each behind its own scope, over the catalog of
/// <see cref="ProductCatalogTools"/>. AddAuthorizationFilters() makes the SDK honour
/// [Authorize] on tools/list (the tool is hidden) and on tools/call (refused).
/// A scope decides which tools a caller may run, not which fields it sees, so
/// ProductLookup returns a projection without the stock level: catalog.read is a
/// price grant, and stock needs inventory.read through InventoryCheck.
/// </summary>
[McpServerToolType]
public static class ProtectedCatalogTools
{
    [McpServerTool(Name = "ProductLookup")]
    [Authorize(Policy = CatalogScopes.Read)]
    [Description("Looks up product information by product ID. Returns the product name, SKU, category and price; the stock level needs InventoryCheck.")]
    public static string ProductLookup(
        [Description("The unique product identifier (integer, e.g. 1001)")] int productId) =>
        ProductCatalogTools.Find(productId) is { } p
            ? JsonSerializer.Serialize(new PriceInfo(p.Id, p.Name, p.Sku, p.Category, p.Price), JsonSerializerOptions.Web)
            : ProductCatalogTools.ProductLookup(productId);   // the not-found message

    [McpServerTool(Name = "InventoryCheck")]
    [Authorize(Policy = CatalogScopes.Inventory)]
    [Description("Checks the current inventory level for a product by its SKU (stock-keeping unit). Returns the stock count and availability status.")]
    public static string InventoryCheck(
        [Description("The product SKU to check inventory for (e.g., 'KB-ERG-001', 'MS-WL-002')")] string sku) =>
        ProductCatalogTools.InventoryCheck(sku);
}

/// <summary>What catalog.read may see of a product: everything but the stock level.</summary>
public sealed record PriceInfo(int Id, string Name, string Sku, string Category, decimal Price);
