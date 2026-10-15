using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPdfSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "current",
                schema: "documents",
                table: "ingestion_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "total",
                schema: "documents",
                table: "ingestion_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "content",
                schema: "documents",
                table: "document_versions",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<int[]>(
                name: "pages_without_text",
                schema: "documents",
                table: "document_versions",
                type: "integer[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "storage_key",
                schema: "documents",
                table: "document_versions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_versions_source",
                schema: "documents",
                table: "document_versions",
                sql: "(source_format = 'Pdf' AND storage_key IS NOT NULL AND content IS NULL) OR (source_format <> 'Pdf' AND content IS NOT NULL AND storage_key IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_document_versions_source",
                schema: "documents",
                table: "document_versions");

            migrationBuilder.DropColumn(
                name: "current",
                schema: "documents",
                table: "ingestion_events");

            migrationBuilder.DropColumn(
                name: "total",
                schema: "documents",
                table: "ingestion_events");

            migrationBuilder.DropColumn(
                name: "pages_without_text",
                schema: "documents",
                table: "document_versions");

            migrationBuilder.DropColumn(
                name: "storage_key",
                schema: "documents",
                table: "document_versions");

            migrationBuilder.AlterColumn<string>(
                name: "content",
                schema: "documents",
                table: "document_versions",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
