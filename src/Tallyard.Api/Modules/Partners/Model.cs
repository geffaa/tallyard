using Tallyard.Api.Common;

namespace Tallyard.Api.Modules.Partners;

public abstract class Partner : TenantEntity, IAudited
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? TaxId { get; set; }
    public int PaymentTermsDays { get; set; } = 30;
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

public class Supplier : Partner
{
    /// <summary>Days from order to delivery; feeds reorder-point suggestions.</summary>
    public int LeadTimeDays { get; set; } = 7;
}

public class Customer : Partner
{
    /// <summary>Open receivables plus a new order may not exceed this (0 = cash only).</summary>
    public decimal CreditLimit { get; set; }
}
