using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Chat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PromptBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "prompt_token_estimate",
                schema: "chat",
                table: "request_traces",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "trimmed_passage_count",
                schema: "chat",
                table: "request_traces",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "prompt_token_estimate",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "trimmed_passage_count",
                schema: "chat",
                table: "request_traces");
        }
    }
}
