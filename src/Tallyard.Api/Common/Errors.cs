using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;

namespace Tallyard.Api.Common;

/// <summary>
/// A user-facing error with a stable code. The handler renders the message in the request's
/// language (<c>Accept-Language</c>, English by default) and returns the code as well, so the
/// client can react without parsing text.
/// </summary>
public sealed class AppException(int status, string code, params (string Key, object Value)[] vars) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, object> Vars { get; } = vars.ToDictionary(v => v.Key, v => v.Value);

    public static AppException NotFound(string code) => new(404, code);
    public static AppException Conflict(string code, params (string, object)[] vars) => new(409, code, vars);
    public static AppException Forbidden(string code = "forbidden") => new(403, code);
}

public static class Messages
{
    public const string DefaultLang = "en";

    static readonly Dictionary<string, (string En, string Id)> All = new()
    {
        ["invalid_login"] = ("Wrong workspace, email, or password", "Workspace, email, atau password salah"),
        ["forbidden"] = ("You don't have access to this", "Anda tidak punya akses ke sini"),
        ["validation_failed"] = ("Some fields need attention", "Beberapa isian perlu diperbaiki"),
        ["user_not_found"] = ("User not found", "Pengguna tidak ditemukan"),
        ["email_taken"] = ("That email is already registered", "Email sudah terdaftar"),
        ["last_owner"] = ("There must be at least one Owner", "Harus ada minimal satu Owner"),
        ["warehouse_not_found"] = ("Warehouse not found", "Gudang tidak ditemukan"),
        ["warehouse_code_taken"] = ("Warehouse code {code} is already used", "Kode gudang {code} sudah dipakai"),
        ["product_not_found"] = ("Product not found", "Produk tidak ditemukan"),
        ["sku_taken"] = ("SKU {sku} is already used", "SKU {sku} sudah dipakai"),
        ["barcode_taken"] = ("Barcode {barcode} is already used by {sku}", "Barcode {barcode} sudah dipakai oleh {sku}"),
        ["category_not_found"] = ("Category not found", "Kategori tidak ditemukan"),
        ["supplier_not_found"] = ("Supplier not found", "Supplier tidak ditemukan"),
        ["customer_not_found"] = ("Customer not found", "Pelanggan tidak ditemukan"),
        ["code_taken"] = ("Code {code} is already used", "Kode {code} sudah dipakai"),
        ["stale_record"] = ("Someone else changed this record. Reload and try again.",
                            "Data ini baru diubah orang lain. Muat ulang lalu coba lagi."),
        ["insufficient_stock"] = ("Not enough {sku} in {warehouse}: {available} {unit} available",
                                  "Stok {sku} di {warehouse} tidak cukup: tersedia {available} {unit}"),
        ["batch_required"] = ("{sku} is batch-tracked: enter a batch number", "{sku} dilacak per batch: isi nomor batch"),
        ["batch_not_tracked"] = ("{sku} is not batch-tracked", "{sku} tidak dilacak per batch"),
        ["batch_not_found"] = ("Batch {batch} not found", "Batch {batch} tidak ditemukan"),
        ["batch_expiry_mismatch"] = ("Batch {batch} already exists with expiry {expiry}", "Batch {batch} sudah ada dengan kedaluwarsa {expiry}"),
        ["unknown_unit"] = ("{sku} has no unit \"{unit}\"", "{sku} tidak punya satuan \"{unit}\""),
        ["transfer_not_found"] = ("Transfer not found", "Transfer tidak ditemukan"),
        ["transfer_not_in_transit"] = ("This transfer was already received", "Transfer ini sudah diterima"),
        ["po_not_found"] = ("Purchase order not found", "Purchase order tidak ditemukan"),
        ["po_line_not_found"] = ("Order line not found", "Baris order tidak ditemukan"),
        ["po_not_draft"] = ("Only a draft order can be changed", "Hanya order draft yang bisa diubah"),
        ["po_no_lines"] = ("Add at least one product", "Tambahkan minimal satu produk"),
        ["po_not_pending"] = ("This order is not waiting for approval", "Order ini tidak sedang menunggu approval"),
        ["po_self_approval"] = ("You can't approve an order you created", "Anda tidak bisa menyetujui order yang Anda buat sendiri"),
        ["po_wrong_approver"] = ("This step needs approval by {role}", "Tahap ini perlu disetujui oleh {role}"),
        ["po_already_approved_by_you"] = ("You already approved an earlier step; another person must approve this one",
                                          "Anda sudah menyetujui tahap sebelumnya; tahap ini harus disetujui orang lain"),
        ["po_not_approved"] = ("The order must be approved first", "Order harus disetujui terlebih dahulu"),
        ["po_not_receivable"] = ("This order can't receive goods in its current status", "Order ini tidak bisa menerima barang pada status sekarang"),
        ["po_not_partial"] = ("Only a partly received order can be closed", "Hanya order yang diterima sebagian yang bisa ditutup"),
        ["po_not_cancellable"] = ("An order with received goods can't be cancelled", "Order yang sudah menerima barang tidak bisa dibatalkan"),
        ["over_receipt"] = ("Too much {sku}: {remaining} {unit} left to receive", "{sku} terlalu banyak: sisa yang bisa diterima {remaining} {unit}"),
        ["receipt_empty"] = ("Enter a quantity for at least one line", "Isi jumlah untuk minimal satu baris"),
        ["reject_reason_required"] = ("Say why the order is rejected", "Isi alasan penolakan"),
        ["reorder_stale"] = ("Suggestions changed; reload and try again", "Saran berubah; muat ulang lalu coba lagi"),
        ["so_not_found"] = ("Sales order not found", "Sales order tidak ditemukan"),
        ["so_line_not_found"] = ("Order line not found", "Baris order tidak ditemukan"),
        ["so_not_draft"] = ("Only a draft order can be changed or confirmed", "Hanya order draft yang bisa diubah atau dikonfirmasi"),
        ["so_no_lines"] = ("Add at least one product", "Tambahkan minimal satu produk"),
        ["so_not_on_hold"] = ("This order is not on hold", "Order ini tidak sedang ditahan"),
        ["so_not_confirmed"] = ("The order must be confirmed first", "Order harus dikonfirmasi terlebih dahulu"),
        ["so_not_shippable"] = ("This order can't ship in its current status", "Order ini tidak bisa dikirim pada status sekarang"),
        ["so_not_partial"] = ("Only a partly shipped order can be closed", "Hanya order yang terkirim sebagian yang bisa ditutup"),
        ["so_not_cancellable"] = ("An order that has shipped can't be cancelled", "Order yang sudah dikirim tidak bisa dibatalkan"),
        ["ship_more_than_reserved"] = ("Only {reserved} {unit} of {sku} is reserved for this order", "Hanya {reserved} {unit} {sku} yang direservasi untuk order ini"),
        ["shipment_empty"] = ("Enter a quantity for at least one line", "Isi jumlah untuk minimal satu baris"),
        ["shipment_not_found"] = ("Shipment not found", "Pengiriman tidak ditemukan"),
        ["shipment_already_invoiced"] = ("This shipment has already been invoiced", "Pengiriman ini sudah dibuatkan invoice"),
        ["invoice_not_found"] = ("Invoice not found", "Invoice tidak ditemukan"),
        ["invoice_not_open"] = ("That invoice is not open for this customer", "Invoice tersebut tidak terbuka untuk pelanggan ini"),
        ["payment_exceeds_balance"] = ("The payment is more than {number} still owes", "Pembayaran melebihi sisa tagihan {number}"),
        ["payment_unallocated"] = ("The payment must be fully applied to open invoices ({open} open)", "Pembayaran harus dialokasikan penuh ke invoice terbuka ({open} terbuka)"),
        ["release_note_required"] = ("Say why the order may go ahead", "Isi alasan order boleh dilanjutkan"),
        ["ai_not_configured"] = ("The AI service is not configured (set Ai:GroqApiKey)", "Layanan AI belum dikonfigurasi (isi Ai:GroqApiKey)"),
        ["ai_unavailable"] = ("The AI service is unavailable right now. Try again shortly.", "Layanan AI sedang tidak tersedia. Coba lagi sebentar."),
        ["ai_gave_up"] = ("I couldn't answer that with the data I can look up. Try asking more specifically.",
                          "Saya tidak bisa menjawab itu dengan data yang bisa saya cari. Coba tanyakan lebih spesifik."),
        ["pdf_no_text"] = ("This PDF is a scan without text. Upload a photo or image of it instead.",
                           "PDF ini hasil scan tanpa teks. Unggah foto atau gambarnya."),
        ["pdf_unreadable"] = ("This PDF can't be opened (damaged or password-protected)", "PDF ini tidak bisa dibuka (rusak atau dilindungi password)"),
        ["unsupported_document"] = ("Supported files: PDF, PNG, JPEG, WebP", "Format didukung: PDF, PNG, JPEG, WebP"),
        ["file_empty"] = ("The file is empty", "File kosong"),
        ["file_too_large"] = ("Maximum {mb} MB", "Maksimal {mb} MB"),
        ["document_not_found"] = ("Document not found", "Dokumen tidak ditemukan"),
        ["document_not_ready"] = ("This document was not read successfully or is already converted", "Dokumen ini gagal dibaca atau sudah dikonversi"),
        ["server_error"] = ("Something went wrong. Try again.", "Terjadi kesalahan. Coba lagi."),
    };

    public static string LangOf(HttpContext? http)
    {
        var header = http?.Request.Headers.AcceptLanguage.ToString() ?? "";
        foreach (var part in header.Split(','))
        {
            var tag = part.Split(';')[0].Trim().ToLowerInvariant();
            if (tag.StartsWith("id")) return "id";
            if (tag.StartsWith("en")) return "en";
        }
        return DefaultLang;
    }

    public static string Render(string code, string lang, IReadOnlyDictionary<string, object>? vars = null)
    {
        if (!All.TryGetValue(code, out var entry)) return code;
        var text = lang == "id" ? entry.Id : entry.En;
        foreach (var (k, v) in vars ?? new Dictionary<string, object>())
            text = text.Replace("{" + k + "}", v?.ToString());
        return text;
    }
}

public sealed class ErrorHandler(ILogger<ErrorHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        var lang = Messages.LangOf(http);
        (int status, object body) = ex switch
        {
            AppException e => (e.Status, new { code = e.Code, detail = Messages.Render(e.Code, lang, e.Vars) }),
            ValidationException v => (422, (object)new
            {
                code = "validation_failed",
                detail = Messages.Render("validation_failed", lang),
                errors = v.Errors.GroupBy(e => Camel(e.PropertyName))
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()),
            }),
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException =>
                (409, new { code = "stale_record", detail = Messages.Render("stale_record", lang) }),
            _ => (500, new { code = "server_error", detail = Messages.Render("server_error", lang) }),
        };
        if (status == 500) log.LogError(ex, "Unhandled error");
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(body, ct);
        return true;
    }

    // "Lines[0].Quantity" -> "lines[0].quantity", matching the JSON the client sent.
    static string Camel(string path) =>
        string.Join('.', path.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
}
