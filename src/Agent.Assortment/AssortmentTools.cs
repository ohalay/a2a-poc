using System.ComponentModel;

namespace Agent.Assortment;

/// <summary>
/// Simulated private data source for the Assortment domain.
/// In production this would be backed by the domain's own MCP server / catalog DB.
/// Exposed to the local LLM as tool-calling functions.
/// </summary>
public sealed class AssortmentTools
{
    private static readonly Dictionary<string, ProductInfo> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winter coat"] = new("SKU-WC-001", "Winter Coat", "Apparel", IsActive: true, StoreCount: 42),
        ["organic soap"] = new("SKU-OS-014", "Organic Soap", "Health & Beauty", IsActive: true, StoreCount: 128),
        ["running shoes"] = new("SKU-RS-220", "Running Shoes", "Footwear", IsActive: false, StoreCount: 0),
    };

    [Description("Look up a product in the store catalog by its name. Returns SKU, category, whether it is currently active in the assortment, and how many stores list it.")]
    public ProductLookupResult GetProduct(
        [Description("The product name to search for, e.g. 'winter coat'")] string productName)
    {
        if (Catalog.TryGetValue(productName.Trim(), out var p))
        {
            return new ProductLookupResult(Found: true, p);
        }

        return new ProductLookupResult(Found: false, null);
    }

    [Description("List all products currently active in the store assortment.")]
    public IReadOnlyList<ProductInfo> GetActiveCatalog()
        => Catalog.Values.Where(p => p.IsActive).ToList();
}

public record ProductInfo(string Sku, string Name, string Category, bool IsActive, int StoreCount);

public record ProductLookupResult(bool Found, ProductInfo? Product);
