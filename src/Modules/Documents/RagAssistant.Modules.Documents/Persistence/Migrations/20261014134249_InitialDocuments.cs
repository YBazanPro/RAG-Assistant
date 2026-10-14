using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace RagAssistant.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "documents");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "document_versions",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    embedding_dimensions = table.Column<int>(type: "integer", nullable: true),
                    passage_count = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    indexed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    owner_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    tags = table.Column<string[]>(type: "text[]", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: true),
                    source = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    active_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    latest_content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.id);
                    table.ForeignKey(
                        name: "FK_documents_document_versions_active_version_id",
                        column: x => x.active_version_id,
                        principalSchema: "documents",
                        principalTable: "document_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_runs",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    current_stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_ingestion_runs_document_versions_version_id",
                        column: x => x.version_id,
                        principalSchema: "documents",
                        principalTable: "document_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "passages",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    section_path = table.Column<string[]>(type: "text[]", nullable: false),
                    section_ordinal = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    char_count = table.Column<int>(type: "integer", nullable: false),
                    token_estimate = table.Column<int>(type: "integer", nullable: false),
                    page_start = table.Column<int>(type: "integer", nullable: true),
                    page_end = table.Column<int>(type: "integer", nullable: true),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_passages", x => x.id);
                    table.ForeignKey(
                        name: "FK_passages_document_versions_version_id",
                        column: x => x.version_id,
                        principalSchema: "documents",
                        principalTable: "document_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_events",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_ingestion_events_ingestion_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "documents",
                        principalTable: "ingestion_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_versions_document_id_number",
                schema: "documents",
                table: "document_versions",
                columns: new[] { "document_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_documents_active_version_id",
                schema: "documents",
                table: "documents",
                column: "active_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_documents_tags",
                schema: "documents",
                table: "documents",
                column: "tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ux_documents_active_content_hash",
                schema: "documents",
                table: "documents",
                column: "latest_content_hash",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_events_run_id_id",
                schema: "documents",
                table: "ingestion_events",
                columns: new[] { "run_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_runs_status",
                schema: "documents",
                table: "ingestion_runs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_runs_version_id_attempt",
                schema: "documents",
                table: "ingestion_runs",
                columns: new[] { "version_id", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_passages_embedding",
                schema: "documents",
                table: "passages",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_passages_version_id_position",
                schema: "documents",
                table: "passages",
                columns: new[] { "version_id", "position" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_document_versions_documents_document_id",
                schema: "documents",
                table: "document_versions",
                column: "document_id",
                principalSchema: "documents",
                principalTable: "documents",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_document_versions_documents_document_id",
                schema: "documents",
                table: "document_versions");

            migrationBuilder.DropTable(
                name: "ingestion_events",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "passages",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "ingestion_runs",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "documents",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "document_versions",
                schema: "documents");
        }
    }
}
