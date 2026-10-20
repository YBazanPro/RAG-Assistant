using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Chat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetrievalStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "rerank_model",
                schema: "chat",
                table: "request_traces",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "rerank_ms",
                schema: "chat",
                table: "request_traces",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rerank_status",
                schema: "chat",
                table: "request_traces",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "retrieval_mode",
                schema: "chat",
                table: "request_traces",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rerank_model",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "rerank_ms",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "rerank_status",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "retrieval_mode",
                schema: "chat",
                table: "request_traces");
        }
    }
}
