using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Evaluation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "evaluation");

            migrationBuilder.CreateTable(
                name: "runs",
                schema: "evaluation",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    chat_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    judge_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    retrieval_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    rerank = table.Column<bool>(type: "boolean", nullable: false),
                    dataset_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    corpus_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    git_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    valid = table.Column<bool>(type: "boolean", nullable: false),
                    case_count = table.Column<int>(type: "integer", nullable: false),
                    configuration = table.Column<string>(type: "jsonb", nullable: false),
                    metrics = table.Column<string>(type: "jsonb", nullable: false),
                    judge = table.Column<string>(type: "jsonb", nullable: true),
                    cases = table.Column<string>(type: "jsonb", nullable: false),
                    uploaded_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runs_chat_model",
                schema: "evaluation",
                table: "runs",
                column: "chat_model");

            migrationBuilder.CreateIndex(
                name: "IX_runs_started_at_id",
                schema: "evaluation",
                table: "runs",
                columns: new[] { "started_at", "id" },
                descending: new[] { true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runs",
                schema: "evaluation");
        }
    }
}
