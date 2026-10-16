using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Chat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TraceBestScore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "best_score",
                schema: "chat",
                table: "request_traces",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "best_score",
                schema: "chat",
                table: "request_traces");
        }
    }
}
