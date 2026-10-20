using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SuggestedQuestionsAndPassageHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "text_hash",
                schema: "documents",
                table: "passages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Spec 020: existing passages get the hash the ingestion now computes, PassageTextHash written out in SQL (spelled out,
            // not called, so this migration never changes); the empty default only existed to add the column to a filled table.
            migrationBuilder.Sql(
                """
                UPDATE documents.passages
                SET text_hash = encode(sha256(convert_to(btrim(regexp_replace(normalize(text, NFC), '[ \t\n\r\f\v]+', ' ', 'g'), ' '), 'UTF8')), 'hex');
                """);
            migrationBuilder.Sql("ALTER TABLE documents.passages ALTER COLUMN text_hash DROP DEFAULT;");

            migrationBuilder.AddColumn<string[]>(
                name: "suggested_questions",
                schema: "documents",
                table: "document_versions",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "suggestions_error",
                schema: "documents",
                table: "document_versions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suggestions_status",
                schema: "documents",
                table: "document_versions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_passages_version_id_text_hash",
                schema: "documents",
                table: "passages",
                columns: new[] { "version_id", "text_hash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_passages_version_id_text_hash",
                schema: "documents",
                table: "passages");

            migrationBuilder.DropColumn(
                name: "text_hash",
                schema: "documents",
                table: "passages");

            migrationBuilder.DropColumn(
                name: "suggested_questions",
                schema: "documents",
                table: "document_versions");

            migrationBuilder.DropColumn(
                name: "suggestions_error",
                schema: "documents",
                table: "document_versions");

            migrationBuilder.DropColumn(
                name: "suggestions_status",
                schema: "documents",
                table: "document_versions");
        }
    }
}
