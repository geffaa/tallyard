using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Tallyard.Api.Infrastructure;

/// <summary>
/// Every time EF opens a connection, it sets <c>app.tenant_id</c> for that session. The RLS
/// policies read it, so a query without a tenant sees no tenant rows at all, and a bug that
/// forgets a WHERE clause still cannot read another tenant's data. Setting it on every open
/// (including to empty) means a pooled connection never carries a previous request's tenant.
/// </summary>
public sealed class TenantConnectionInterceptor(CurrentUser user) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData data, CancellationToken ct = default)
    {
        await using var cmd = Command(connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData data)
    {
        using var cmd = Command(connection);
        cmd.ExecuteNonQuery();
    }

    DbCommand Command(DbConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.tenant_id', @tenant, false)";
        var p = cmd.CreateParameter();
        p.ParameterName = "tenant";
        p.Value = user.TenantId?.ToString() ?? "";
        cmd.Parameters.Add(p);
        return cmd;
    }
}
