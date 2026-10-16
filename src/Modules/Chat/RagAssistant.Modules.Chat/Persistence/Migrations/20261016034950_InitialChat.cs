using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Chat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "chat");

            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "chat",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_sub = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    message_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "chat",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    filter_language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    filter_tags = table.Column<string[]>(type: "text[]", nullable: false),
                    reply_to_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    refusal_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    pending_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_messages_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalSchema: "chat",
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "request_traces",
                schema: "chat",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assistant_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    owner_sub = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    question_text = table.Column<string>(type: "text", nullable: false),
                    filter_language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    filter_tags = table.Column<string[]>(type: "text[]", nullable: false),
                    otel_trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    evidence_outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    min_score = table.Column<double>(type: "double precision", nullable: true),
                    confident_score = table.Column<double>(type: "double precision", nullable: true),
                    duplicate_overlap = table.Column<double>(type: "double precision", nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    retrieved_passages = table.Column<string>(type: "jsonb", nullable: false),
                    chat_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    prompt_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    sentinel_hit = table.Column<bool>(type: "boolean", nullable: false),
                    stripped_marker_count = table.Column<int>(type: "integer", nullable: false),
                    final_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    refusal_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retrieval_ms = table.Column<int>(type: "integer", nullable: true),
                    first_token_ms = table.Column<int>(type: "integer", nullable: true),
                    total_ms = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_traces", x => x.id);
                    table.ForeignKey(
                        name: "FK_request_traces_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalSchema: "chat",
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "citation_snapshots",
                schema: "chat",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    marker = table.Column<int>(type: "integer", nullable: false),
                    passage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    document_title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    section_path = table.Column<string[]>(type: "text[]", nullable: false),
                    page_start = table.Column<int>(type: "integer", nullable: true),
                    page_end = table.Column<int>(type: "integer", nullable: true),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_citation_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_citation_snapshots_messages_message_id",
                        column: x => x.message_id,
                        principalSchema: "chat",
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_citation_snapshots_message_id",
                schema: "chat",
                table: "citation_snapshots",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_owner_sub_last_activity_at_id",
                schema: "chat",
                table: "conversations",
                columns: new[] { "owner_sub", "last_activity_at", "id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_messages_conversation_id_sequence",
                schema: "chat",
                table: "messages",
                columns: new[] { "conversation_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_one_pending_per_conversation",
                schema: "chat",
                table: "messages",
                column: "conversation_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_messages_pending",
                schema: "chat",
                table: "messages",
                column: "status",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_request_traces_assistant_message_id_attempt",
                schema: "chat",
                table: "request_traces",
                columns: new[] { "assistant_message_id", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_request_traces_conversation_id",
                schema: "chat",
                table: "request_traces",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "IX_request_traces_started_at",
                schema: "chat",
                table: "request_traces",
                column: "started_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "citation_snapshots",
                schema: "chat");

            migrationBuilder.DropTable(
                name: "request_traces",
                schema: "chat");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "chat");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "chat");
        }
    }
}
