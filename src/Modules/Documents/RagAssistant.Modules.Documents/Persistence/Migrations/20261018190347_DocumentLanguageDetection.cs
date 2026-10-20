using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentLanguageDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "language_source",
                schema: "documents",
                table: "documents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Detect");

            migrationBuilder.AddColumn<string>(
                name: "detected_language",
                schema: "documents",
                table: "document_versions",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "language_detection",
                schema: "documents",
                table: "document_versions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "language_source",
                schema: "documents",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "detected_language",
                schema: "documents",
                table: "document_versions");

            migrationBuilder.DropColumn(
                name: "language_detection",
                schema: "documents",
                table: "document_versions");
        }
    }
}
