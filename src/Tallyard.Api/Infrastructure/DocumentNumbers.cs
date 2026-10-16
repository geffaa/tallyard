using Microsoft.EntityFrameworkCore;

namespace Tallyard.Api.Infrastructure;

public class DocumentSequence
{
    public Guid TenantId { get; set; }
    public required string Prefix { get; set; }
    public int Year { get; set; }
    public int LastValue { get; set; }
}

/// <summary>
/// Gap-free document numbers (ADJ-2026-000042), per tenant, prefix and year. The counter row is
/// incremented with an upsert that holds its row lock until the caller's transaction ends: two
/// concurrent documents wait for each other, and a rolled-back document gives its number back.
/// Must be called inside a transaction.
/// </summary>
public sealed class DocumentNumbers(AppDbContext db, CurrentUser user)
{
    public async Task<string> NextAsync(string prefix, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Document numbers must be taken inside a transaction");
        var year = DateTime.UtcNow.Year;
        var values = await db.Database.SqlQuery<int>($"""
            INSERT INTO document_sequences (tenant_id, prefix, year, last_value)
            VALUES ({user.RequiredTenantId}, {prefix}, {year}, 1)
            ON CONFLICT (tenant_id, prefix, year) DO UPDATE SET last_value = document_sequences.last_value + 1
            RETURNING last_value AS "Value"
            """).ToListAsync(ct); // not composable: an INSERT can't be wrapped in a SELECT
        return $"{prefix}-{year}-{values[0]:D6}";
    }
}
