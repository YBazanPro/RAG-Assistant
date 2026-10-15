using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "revision",
                schema: "documents",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "revision",
                schema: "documents",
                table: "documents");
        }
    }
}
