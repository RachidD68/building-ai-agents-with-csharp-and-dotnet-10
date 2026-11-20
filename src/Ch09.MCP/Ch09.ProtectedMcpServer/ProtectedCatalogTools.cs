using System.ComponentModel;
using Ch09.McpServer;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace Ch09.ProtectedMcpServer;

/// <summary>
/// The same two catalog tools, each behind its own scope. The bodies delegate to
/// <see cref="ProductCatalogTools"/>, so the protected server cannot drift from the
/// stdio and HTTP ones. AddAuthorizationFilters() makes the SDK honour
/// [Authorize] on tools/list (the tool is hidden) and on tools/call (refused).
/// </summary>
[McpServerToolType]
public static class ProtectedCatalogTools
{
    [McpServerTool(Name = "ProductLookup")]
    [Authorize(Policy = CatalogScopes.Read)]
    [Description("Looks up detailed product information by product ID. Returns the product name, SKU, category, price, and stock level.")]
    public static string ProductLookup(
        [Description("The unique product identifier (integer, e.g. 1001)")] int productId) =>
        ProductCatalogTools.ProductLookup(productId);

    [McpServerTool(Name = "InventoryCheck")]
    [Authorize(Policy = CatalogScopes.Inventory)]
    [Description("Checks the current inventory level for a product by its SKU (stock-keeping unit). Returns the stock count and availability status.")]
    public static string InventoryCheck(
        [Description("The product SKU to check inventory for (e.g., 'KB-ERG-001', 'MS-WL-002')")] string sku) =>
        ProductCatalogTools.InventoryCheck(sku);
}
