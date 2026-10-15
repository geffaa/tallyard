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
