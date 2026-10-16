using Microsoft.EntityFrameworkCore.Migrations;

namespace Tallyard.Api.Infrastructure;

/// <summary>SQL that migrations use to protect new tables.</summary>
public static class SecuritySql
{
    /// <summary>Rows visible and writable only for the tenant in <c>app.tenant_id</c>.</summary>
    public static void TenantIsolation(this MigrationBuilder mb, params string[] tables)
    {
        foreach (var t in tables)
            mb.Sql($"""
                ALTER TABLE {t} ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON {t}
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
    }

    /// <summary>History tables: the app may only add and read rows, and a trigger rejects
    /// UPDATE/DELETE/TRUNCATE for everyone, including the owner. Corrections are new rows.</summary>
    public static void AppendOnly(this MigrationBuilder mb, params string[] tables)
    {
        foreach (var t in tables)
            mb.Sql($"""
                REVOKE UPDATE, DELETE, TRUNCATE ON {t} FROM tallyard_app;
                CREATE TRIGGER {t}_append_only BEFORE UPDATE OR DELETE ON {t}
                    FOR EACH ROW EXECUTE FUNCTION reject_history_change();
                CREATE TRIGGER {t}_no_truncate BEFORE TRUNCATE ON {t}
                    FOR EACH STATEMENT EXECUTE FUNCTION reject_history_change();
                """);
    }
}
