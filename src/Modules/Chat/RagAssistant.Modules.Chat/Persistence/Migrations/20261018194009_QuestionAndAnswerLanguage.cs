using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Chat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QuestionAndAnswerLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "answer_language",
                schema: "chat",
                table: "request_traces",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "question_language",
                schema: "chat",
                table: "request_traces",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "question_language_source",
                schema: "chat",
                table: "request_traces",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_language",
                schema: "chat",
                table: "request_traces",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "question_language",
                schema: "chat",
                table: "messages",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "question_language_source",
                schema: "chat",
                table: "messages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_language",
                schema: "chat",
                table: "messages",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "language",
                schema: "chat",
                table: "citation_snapshots",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "answer_language",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "question_language",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "question_language_source",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "requested_language",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "question_language",
                schema: "chat",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "question_language_source",
                schema: "chat",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "requested_language",
                schema: "chat",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "language",
                schema: "chat",
                table: "citation_snapshots");
        }
    }
}
