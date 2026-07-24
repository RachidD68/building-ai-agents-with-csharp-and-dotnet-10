namespace Ch08.DatabaseLookupTool;

/// <summary>
/// An in-memory product repository pre-loaded with sample data.
/// In production, this would back onto a real database.
/// </summary>
public sealed class InMemoryProductRepository : IProductRepository
{
    private static readonly List<Product> Products =
    [
        new(1, "Contoso Ergonomic Keyboard", "Electronics", 79.99m, 150),
        new(2, "Contoso Wireless Mouse", "Electronics", 34.99m, 200),
        new(3, "Contoso 27\" 4K Monitor", "Electronics", 449.99m, 45),
        new(4, "Contoso Standing Desk", "Furniture", 599.99m, 30),
        new(5, "Contoso Mesh Office Chair", "Furniture", 349.99m, 60),
        new(6, "Contoso USB-C Hub", "Accessories", 49.99m, 300),
        new(7, "Contoso Webcam Pro", "Electronics", 89.99m, 120),
        new(8, "Contoso Noise-Cancelling Headphones", "Electronics", 199.99m, 80),
        new(9, "Contoso Desk Lamp", "Accessories", 39.99m, 250),
        new(10, "Contoso Laptop Backpack", "Accessories", 59.99m, 175),
    ];

    private int _orderCounter;
    private readonly Lock _stockLock = new();

    /// <inheritdoc />
    public Task<IReadOnlyList<Product>> SearchProductsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Product> results = Products
            .Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || p.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Task.FromResult(results);
    }

    /// <inheritdoc />
    public Task<Product?> GetProductByIdAsync(
        int id,
        CancellationToken cancellationToken)
    {
        Product? product = Products.FirstOrDefault(p => p.Id == id);
        return Task.FromResult(product);
    }

    /// <inheritdoc />
    public Task<OrderConfirmation> PlaceOrderAsync(
        int productId,
        int quantity,
        CancellationToken cancellationToken)
    {
        // Check-then-decrement must be atomic: an agent can fire concurrent
        // tool calls (see the AllowConcurrentInvocation discussion in Ch08),
        // and two orders racing on the last units must not both succeed.
        lock (_stockLock)
        {
            int index = Products.FindIndex(p => p.Id == productId);

            if (index < 0)
            {
                return Task.FromResult(new OrderConfirmation(
                    "", "Unknown", quantity, 0, "",
                    Error: $"Product with ID {productId} not found."));
            }

            Product product = Products[index];

            if (quantity > product.InStock)
            {
                return Task.FromResult(new OrderConfirmation(
                    "", product.Name, quantity, 0, "",
                    Error: $"Insufficient stock for {product.Name}. Only {product.InStock} units available."));
            }

            // An order consumes inventory — the next InventoryCheck reflects it.
            Products[index] = product with { InStock = product.InStock - quantity };

            int orderId = Interlocked.Increment(ref _orderCounter);
            decimal total = product.Price * quantity;

            OrderConfirmation confirmation = new(
                OrderId: $"ORD-{orderId:D6}",
                ProductName: product.Name,
                Quantity: quantity,
                TotalPrice: total,
                EstimatedDelivery: DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd"));

            return Task.FromResult(confirmation);
        }
    }
}
