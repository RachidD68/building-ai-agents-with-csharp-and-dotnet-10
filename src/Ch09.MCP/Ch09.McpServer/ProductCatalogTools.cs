using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Ch09.McpServer;

/// <summary>
/// MCP server tools for product catalog operations.
/// Methods decorated with <see cref="McpServerToolAttribute"/> are automatically
/// discovered and exposed to MCP clients.
/// </summary>
[McpServerToolType]
public static class ProductCatalogTools
{
    private static readonly Dictionary<int, ProductInfo> Products = new()
    {
        [1001] = new(1001, "Contoso Ergonomic Keyboard", "KB-ERG-001", "Electronics", 79.99m, 150),
        [1002] = new(1002, "Contoso Wireless Mouse", "MS-WL-002", "Electronics", 34.99m, 200),
        [1003] = new(1003, "Contoso 27\" 4K Monitor", "MON-4K-003", "Electronics", 449.99m, 45),
        [1004] = new(1004, "Contoso Standing Desk", "DSK-STD-004", "Furniture", 599.99m, 30),
        [1005] = new(1005, "Contoso Mesh Office Chair", "CHR-MSH-005", "Furniture", 349.99m, 60),
        [1006] = new(1006, "Contoso USB-C Hub", "HUB-USC-006", "Accessories", 49.99m, 300),
        [1007] = new(1007, "Contoso Webcam Pro", "CAM-PRO-007", "Electronics", 89.99m, 120),
        [1008] = new(1008, "Contoso Noise-Cancelling Headphones", "HP-NC-008", "Electronics", 199.99m, 80),
    };

    /// <summary>
    /// Looks up a product by its unique identifier.
    /// </summary>
    /// <param name="productId">The product identifier to look up.</param>
    /// <returns>A JSON string with product details, or an error message.</returns>
    [McpServerTool(Name = "ProductLookup")]
    [Description("Looks up detailed product information by product ID. Returns the product name, SKU, category, price, and stock level.")]
    public static string ProductLookup(
        [Description("The unique product identifier (integer, e.g. 1001)")] int productId)
    {
        if (Products.TryGetValue(productId, out ProductInfo? product))
        {
            return JsonSerializer.Serialize(product, JsonSerializerOptions.Web);
        }

        return $"Product with ID {productId} not found. Valid IDs range from 1001 to 1008.";
    }

    /// <summary>
    /// Checks inventory levels for a product by SKU.
    /// </summary>
    /// <param name="sku">The stock-keeping unit identifier.</param>
    /// <returns>A formatted string describing the inventory status.</returns>
    [McpServerTool(Name = "InventoryCheck")]
    [Description("Checks the current inventory level for a product by its SKU (stock-keeping unit). Returns the stock count and availability status.")]
    public static string InventoryCheck(
        [Description("The product SKU to check inventory for (e.g., 'KB-ERG-001', 'MS-WL-002')")] string sku)
    {
        ProductInfo? product = Products.Values.FirstOrDefault(
            p => p.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase));

        if (product is null)
        {
            return $"No product found with SKU '{sku}'. " +
                   $"Available SKUs: {string.Join(", ", Products.Values.Select(p => p.Sku))}";
        }

        string status = product.InStock switch
        {
            0 => "OUT OF STOCK",
            < 50 => "LOW STOCK",
            _ => "IN STOCK"
        };

        return $"Product: {product.Name} (SKU: {product.Sku})\n" +
               $"Status: {status}\n" +
               $"Units Available: {product.InStock}\n" +
               $"Unit Price: ${product.Price:F2}";
    }
}

/// <summary>
/// Represents a product in the catalog.
/// </summary>
/// <param name="Id">Unique product identifier.</param>
/// <param name="Name">Product display name.</param>
/// <param name="Sku">Stock-keeping unit code.</param>
/// <param name="Category">Product category.</param>
/// <param name="Price">Unit price in USD.</param>
/// <param name="InStock">Current inventory count.</param>
public sealed record ProductInfo(int Id, string Name, string Sku, string Category, decimal Price, int InStock);
