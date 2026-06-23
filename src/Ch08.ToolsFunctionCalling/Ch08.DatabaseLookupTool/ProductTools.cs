using System.ComponentModel;
using System.Text.Json;

namespace Ch08.DatabaseLookupTool;

/// <summary>
/// Tool class with methods that depend on an injected <see cref="IProductRepository"/>.
/// This demonstrates the DI pattern for tools that need external dependencies.
/// </summary>
public sealed class ProductTools
{
    private readonly IProductRepository _repository;

    /// <summary>
    /// Initializes a new instance with the given product repository.
    /// </summary>
    /// <param name="repository">The product repository for data access.</param>
    public ProductTools(IProductRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Searches for products matching a query string.
    /// </summary>
    /// <param name="query">The search term to match against product names and categories.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A JSON-formatted list of matching products.</returns>
    [Description("Searches the product catalog by name or category. Returns a list of matching products with their ID, name, category, price, and stock level.")]
    public async Task<string> SearchProducts(
        [Description("The search term to match against product names and categories (e.g., 'keyboard', 'electronics', 'chair')")] string query,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Product> results = await _repository.SearchProductsAsync(query, cancellationToken);

        if (results.Count == 0)
        {
            return $"No products found matching '{query}'.";
        }

        return JsonSerializer.Serialize(results, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// Retrieves detailed information about a specific product.
    /// </summary>
    /// <param name="id">The unique product identifier.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A JSON-formatted product description, or a not-found message.</returns>
    [Description("Gets detailed information about a specific product by its ID. Returns the product's name, category, price, and stock level.")]
    public async Task<string> GetProductById(
        [Description("The unique product identifier (integer)")] int id,
        CancellationToken cancellationToken)
    {
        Product? product = await _repository.GetProductByIdAsync(id, cancellationToken);

        if (product is null)
        {
            return $"No product found with ID {id}.";
        }

        return JsonSerializer.Serialize(product, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// Places an order for a product.
    /// </summary>
    /// <param name="productId">The product identifier to order.</param>
    /// <param name="quantity">The number of units to order.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A JSON-formatted order confirmation.</returns>
    [Description("Places an order for a product. Requires the product ID (from a previous search or lookup) and the desired quantity. Returns an order confirmation with the total price and estimated delivery date.")]
    public async Task<string> PlaceOrder(
        [Description("The product ID to order (use SearchProducts or GetProductById first to find the ID)")] int productId,
        [Description("The number of units to order (must be a positive integer)")] int quantity,
        CancellationToken cancellationToken)
    {
        OrderConfirmation confirmation = await _repository.PlaceOrderAsync(
            productId, quantity, cancellationToken);

        return JsonSerializer.Serialize(confirmation, JsonSerializerOptions.Web);
    }
}
