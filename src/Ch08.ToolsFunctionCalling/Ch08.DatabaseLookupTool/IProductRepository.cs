namespace Ch08.DatabaseLookupTool;

/// <summary>
/// Defines the contract for product data access.
/// Injected into tool classes via dependency injection.
/// </summary>
public interface IProductRepository
{
    /// <summary>
    /// Searches for products whose name or category matches the query.
    /// </summary>
    /// <param name="query">The search term to match against product names and categories.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A list of matching products.</returns>
    Task<IReadOnlyList<Product>> SearchProductsAsync(string query, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a single product by its unique identifier.
    /// </summary>
    /// <param name="id">The product identifier.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The matching product, or null if not found.</returns>
    Task<Product?> GetProductByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Places an order for the given product and quantity.
    /// </summary>
    /// <param name="productId">The product identifier to order.</param>
    /// <param name="quantity">The number of units to order.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An order confirmation with details.</returns>
    Task<OrderConfirmation> PlaceOrderAsync(int productId, int quantity, CancellationToken cancellationToken);
}

/// <summary>
/// Represents a product in the catalog.
/// </summary>
/// <param name="Id">Unique product identifier.</param>
/// <param name="Name">Product name.</param>
/// <param name="Category">Product category.</param>
/// <param name="Price">Unit price in USD.</param>
/// <param name="InStock">Number of units currently in stock.</param>
public sealed record Product(int Id, string Name, string Category, decimal Price, int InStock);

/// <summary>
/// Represents the result of placing an order. On failure, <paramref name="Error"/>
/// carries a model-readable explanation and the other fields are empty — the
/// serialized JSON is what the model sees, so the reason must live in a real
/// field, not in a sentinel value smuggled through an unrelated one.
/// </summary>
/// <param name="OrderId">The unique order identifier; empty when the order failed.</param>
/// <param name="ProductName">The name of the ordered product.</param>
/// <param name="Quantity">The quantity ordered.</param>
/// <param name="TotalPrice">The total price for the order.</param>
/// <param name="EstimatedDelivery">The estimated delivery date; empty when the order failed.</param>
/// <param name="Error">Why the order failed, or null on success.</param>
public sealed record OrderConfirmation(
    string OrderId,
    string ProductName,
    int Quantity,
    decimal TotalPrice,
    string EstimatedDelivery,
    string? Error = null);
