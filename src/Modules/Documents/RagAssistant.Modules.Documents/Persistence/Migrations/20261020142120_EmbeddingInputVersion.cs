using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmbeddingInputVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "embedding_input_version",
                schema: "documents",
                table: "document_versions",
                type: "integer",
                nullable: true);

            // Spec 021: every version embedded so far used input 1 (heading path and text, no title).
            migrationBuilder.Sql("UPDATE documents.document_versions SET embedding_input_version = 1 WHERE embedding_model IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "embedding_input_version",
                schema: "documents",
                table: "document_versions");
        }
    }
}
