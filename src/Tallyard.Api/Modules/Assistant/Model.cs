using Tallyard.Api.Common;

namespace Tallyard.Api.Modules.Assistant;

public enum DocumentStatus { Ready, Failed, Converted }

/// <summary>
/// A document a supplier sent (invoice, quotation, order confirmation) and what was read from it.
/// The file is kept so the review screen can show it beside the extracted lines; nothing becomes
/// a purchase order until a person has checked it.
/// </summary>
public class SupplierDocument : TenantEntity
{
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public byte[] Content { get; set; } = [];
    public DocumentStatus Status { get; set; }
    public string? ErrorCode { get; set; }
    /// <summary>How the text was obtained: "pdf-text" or "vision".</summary>
    public string? Reader { get; set; }
    /// <summary>The extraction and matching result (JSON, see <see cref="Extraction"/>).</summary>
    public string? Result { get; set; }
    public int Tokens { get; set; }
    public int DurationMs { get; set; }
    public Guid? UploadedBy { get; set; }
    public Guid? PurchaseOrderId { get; set; }
}
