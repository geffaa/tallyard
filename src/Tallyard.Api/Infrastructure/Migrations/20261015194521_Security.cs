using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tallyard.Api.Infrastructure.Migrations
{
    /// <summary>
    /// Database-level guarantees that application code cannot bypass:
    /// tenant isolation by row-level security, and an audit log that can only grow.
    /// </summary>
    public partial class Security : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            // Audit log, partitioned by month (EF created a plain table; nothing is in it yet).
            mb.Sql("""
                DROP TABLE audit_logs;
                CREATE TABLE audit_logs (
                    id uuid NOT NULL,
                    created_at timestamptz NOT NULL,
                    tenant_id uuid NOT NULL,
                    user_id uuid,
                    action text NOT NULL,
                    entity_type text NOT NULL,
                    entity_id uuid NOT NULL,
                    changes jsonb NOT NULL,
                    CONSTRAINT pk_audit_logs PRIMARY KEY (id, created_at)
                ) PARTITION BY RANGE (created_at);
                CREATE INDEX ix_audit_logs_tenant_id_created_at ON audit_logs (tenant_id, created_at);
                CREATE INDEX ix_audit_logs_tenant_id_entity_type_entity_id ON audit_logs (tenant_id, entity_type, entity_id);
                CREATE TABLE audit_logs_default PARTITION OF audit_logs DEFAULT;
                DO $$
                DECLARE m date := date '2026-01-01';
                BEGIN
                    WHILE m < date '2028-01-01' LOOP
                        EXECUTE format('CREATE TABLE audit_logs_%s PARTITION OF audit_logs FOR VALUES FROM (%L) TO (%L)',
                                       to_char(m, 'YYYY_MM'), m, m + interval '1 month');
                        m := m + interval '1 month';
                    END LOOP;
                END $$;

                CREATE FUNCTION reject_audit_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_logs is append-only';
                END $$;
                CREATE TRIGGER audit_logs_append_only BEFORE UPDATE OR DELETE ON audit_logs
                    FOR EACH ROW EXECUTE FUNCTION reject_audit_change();
                CREATE TRIGGER audit_logs_no_truncate BEFORE TRUNCATE ON audit_logs
                    FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_change();
                """);

            // Row-level security: rows are visible and writable only for the tenant in app.tenant_id.
            // Not FORCEd, so the owner role (migrations, seeding) is unaffected; the app role is not the owner.
            foreach (var table in AppDbContext.TenantTables)
                mb.Sql($"""
                    ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                    CREATE POLICY tenant_isolation ON {table}
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                    """);

            // Privileges of the app role: tenants are read-only, the audit log is insert/read only,
            // and audit partitions can't be reached directly (that would skip the parent's RLS).
            mb.Sql("""
                REVOKE INSERT, UPDATE, DELETE ON tenants FROM tallyard_app;
                GRANT SELECT, INSERT ON audit_logs TO tallyard_app;
                REVOKE UPDATE, DELETE, TRUNCATE ON audit_logs FROM tallyard_app;
                DO $$
                DECLARE p text;
                BEGIN
                    FOR p IN SELECT inhrelid::regclass::text FROM pg_inherits WHERE inhparent = 'audit_logs'::regclass LOOP
                        EXECUTE format('REVOKE ALL ON %s FROM tallyard_app', p);
                    END LOOP;
                END $$;
                """);
        }

        protected override void Down(MigrationBuilder mb)
        {
            foreach (var table in AppDbContext.TenantTables)
                mb.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table}; ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            mb.Sql("""
                DROP TABLE audit_logs;
                DROP FUNCTION reject_audit_change();
                CREATE TABLE audit_logs (
                    id uuid NOT NULL, created_at timestamptz NOT NULL, tenant_id uuid NOT NULL, user_id uuid,
                    action text NOT NULL, entity_type text NOT NULL, entity_id uuid NOT NULL, changes jsonb NOT NULL,
                    CONSTRAINT pk_audit_logs PRIMARY KEY (id, created_at));
                GRANT SELECT, INSERT, UPDATE, DELETE ON tenants TO tallyard_app;
                """);
        }
    }
}
