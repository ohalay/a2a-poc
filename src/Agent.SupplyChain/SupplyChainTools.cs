using System.ComponentModel;

namespace Agent.SupplyChain;

/// <summary>
/// Simulated private data source for the Supply Chain domain.
/// In production this would query the domain's own MCP server / data warehouse (DWH).
/// Exposed to the local LLM as tool-calling functions.
/// </summary>
public sealed class SupplyChainTools
{
    private static readonly Dictionary<string, WarehouseStock> Warehouse = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winter coat"] = new("SKU-WC-001", OnHand: 1200, InTransit: 300, WeeklyVelocity: 210),
        ["organic soap"] = new("SKU-OS-014", OnHand: 8400, InTransit: 0, WeeklyVelocity: 1500),
        ["running shoes"] = new("SKU-RS-220", OnHand: 0, InTransit: 500, WeeklyVelocity: 0),
    };

    private static readonly List<Shipment> Shipments =
    [
        new("SHIP-402", "winter coat", "In Customs", ExpectedDays: 5),
        new("SHIP-511", "running shoes", "In Transit", ExpectedDays: 2),
    ];

    [Description("Get warehouse stock for a product by name: units on hand, units in transit, and weekly sales velocity.")]
    public StockLookupResult GetStock(
        [Description("The product name, e.g. 'winter coat'")] string productName)
    {
        if (Warehouse.TryGetValue(productName.Trim(), out var s))
        {
            return new StockLookupResult(Found: true, s);
        }

        return new StockLookupResult(Found: false, null);
    }

    [Description("List active inbound shipments and their delivery status, optionally filtered by product name.")]
    public IReadOnlyList<Shipment> GetShipments(
        [Description("Optional product name filter. Leave empty to list all shipments.")] string? productName = null)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return Shipments;
        }

        return Shipments
            .Where(s => s.Product.Contains(productName.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}

public record WarehouseStock(string Sku, int OnHand, int InTransit, int WeeklyVelocity);

public record StockLookupResult(bool Found, WarehouseStock? Stock);

public record Shipment(string ShipmentId, string Product, string Status, int ExpectedDays);
