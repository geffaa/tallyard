using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Tallyard.Api.Infrastructure;

#nullable disable

namespace Tallyard.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StockCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stock_counts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "text", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    started_by = table.Column<Guid>(type: "uuid", nullable: true),
                    posted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    adjustment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_counts", x => x.id);
                    table.ForeignKey(
                        name: "fk_stock_counts_warehouses_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_count_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_count_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expected = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    counted = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    counted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_count_lines", x => x.id);
                    table.CheckConstraint("ck_stock_count_lines_counted", "counted IS NULL OR counted >= 0");
                    table.ForeignKey(
                        name: "fk_stock_count_lines_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_count_lines_stock_counts_stock_count_id",
                        column: x => x.stock_count_id,
                        principalTable: "stock_counts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_count_lines_product_id",
                table: "stock_count_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_count_lines_stock_count_id_product_id",
                table: "stock_count_lines",
                columns: new[] { "stock_count_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_counts_tenant_id_number",
                table: "stock_counts",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_counts_warehouse_id",
                table: "stock_counts",
                column: "warehouse_id");

            migrationBuilder.TenantIsolation("stock_counts", "stock_count_lines");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stock_count_lines");

            migrationBuilder.DropTable(
                name: "stock_counts");
        }
    }
}
