using Tallyard.Api.Common;

namespace Tallyard.Api.Modules.Catalog;

public class Category : TenantEntity, IAudited
{
    public required string Name { get; set; }
}

public class Product : TenantEntity, IAudited
{
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }
    /// <summary>Stock is always kept in this unit; other units convert to it.</summary>
    public string BaseUnit { get; set; } = "pcs";
    /// <summary>Receipts record batch number and expiry; issues pick earliest expiry first.</summary>
    public bool TrackBatches { get; set; }
    /// <summary>Percent, e.g. 11.00 for Indonesian VAT.</summary>
    public decimal TaxRate { get; set; }
    /// <summary>Default selling price per base unit.</summary>
    public decimal SalePrice { get; set; }
    public bool IsActive { get; set; } = true;
    public List<ProductUnit> Units { get; set; } = [];
    public List<ProductBarcode> Barcodes { get; set; } = [];
    public uint Version { get; set; }
}

/// <summary>An alternative unit: 1 <see cref="Name"/> = <see cref="Factor"/> base units (1 case = 24 pcs).</summary>
public class ProductUnit : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public required string Name { get; set; }
    public decimal Factor { get; set; }
}

/// <summary>A scannable code. It may stand for a pack (a case barcode), given by <see cref="Unit"/>.</summary>
public class ProductBarcode : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public required string Code { get; set; }
    public string? Unit { get; set; }
}
