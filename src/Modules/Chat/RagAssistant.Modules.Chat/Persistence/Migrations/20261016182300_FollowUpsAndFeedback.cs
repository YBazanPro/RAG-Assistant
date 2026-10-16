using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Chat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FollowUpsAndFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "retrieval_query",
                schema: "chat",
                table: "request_traces",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rewrite_fallback_reason",
                schema: "chat",
                table: "request_traces",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "rewrite_input_tokens",
                schema: "chat",
                table: "request_traces",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "rewrite_ms",
                schema: "chat",
                table: "request_traces",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "rewrite_output_tokens",
                schema: "chat",
                table: "request_traces",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rewrite_prompt_version",
                schema: "chat",
                table: "request_traces",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "rewrite_used",
                schema: "chat",
                table: "request_traces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "retrieval_query",
                schema: "chat",
                table: "messages",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "issue_reports",
                schema: "chat",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issue_reports", x => x.id);
                    table.ForeignKey(
                        name: "FK_issue_reports_messages_message_id",
                        column: x => x.message_id,
                        principalSchema: "chat",
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_ratings",
                schema: "chat",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rating = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_ratings", x => x.message_id);
                    table.ForeignKey(
                        name: "FK_message_ratings_messages_message_id",
                        column: x => x.message_id,
                        principalSchema: "chat",
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_issue_reports_message_id",
                schema: "chat",
                table: "issue_reports",
                column: "message_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "issue_reports",
                schema: "chat");

            migrationBuilder.DropTable(
                name: "message_ratings",
                schema: "chat");

            migrationBuilder.DropColumn(
                name: "retrieval_query",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "rewrite_fallback_reason",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "rewrite_input_tokens",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "rewrite_ms",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "rewrite_output_tokens",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "rewrite_prompt_version",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "rewrite_used",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "retrieval_query",
                schema: "chat",
                table: "messages");
        }
    }
}
