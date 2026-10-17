using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_documents_active_content_hash",
                schema: "documents",
                table: "documents");

            migrationBuilder.AddColumn<string>(
                name: "visibility",
                schema: "documents",
                table: "documents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Shared");

            migrationBuilder.CreateIndex(
                name: "IX_documents_visibility_created_by",
                schema: "documents",
                table: "documents",
                columns: new[] { "visibility", "created_by" });

            migrationBuilder.CreateIndex(
                name: "ux_documents_active_content_hash",
                schema: "documents",
                table: "documents",
                columns: new[] { "latest_content_hash", "created_by" },
                unique: true,
                filter: "status = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_documents_visibility_created_by",
                schema: "documents",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "ux_documents_active_content_hash",
                schema: "documents",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "visibility",
                schema: "documents",
                table: "documents");

            migrationBuilder.CreateIndex(
                name: "ux_documents_active_content_hash",
                schema: "documents",
                table: "documents",
                column: "latest_content_hash",
                unique: true,
                filter: "status = 'Active'");
        }
    }
}
