using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Tallyard.Api.Infrastructure;

#nullable disable

namespace Tallyard.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SupplierDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supplier_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    reader = table.Column<string>(type: "text", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    tokens = table.Column<int>(type: "integer", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_supplier_documents_purchase_orders_purchase_order_id",
                        column: x => x.purchase_order_id,
                        principalTable: "purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_documents_purchase_order_id",
                table: "supplier_documents",
                column: "purchase_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_documents_tenant_id_created_at",
                table: "supplier_documents",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.TenantIsolation("supplier_documents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supplier_documents");
        }
    }
}
