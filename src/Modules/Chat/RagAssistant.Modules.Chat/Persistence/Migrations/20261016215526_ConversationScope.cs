using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Chat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConversationScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid[]>(
                name: "filter_document_ids",
                schema: "chat",
                table: "request_traces",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<Guid[]>(
                name: "unavailable_document_ids",
                schema: "chat",
                table: "request_traces",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<Guid[]>(
                name: "filter_document_ids",
                schema: "chat",
                table: "messages",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<Guid[]>(
                name: "scope_document_ids",
                schema: "chat",
                table: "conversations",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<string[]>(
                name: "scope_tags",
                schema: "chat",
                table: "conversations",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "filter_document_ids",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "unavailable_document_ids",
                schema: "chat",
                table: "request_traces");

            migrationBuilder.DropColumn(
                name: "filter_document_ids",
                schema: "chat",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "scope_document_ids",
                schema: "chat",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "scope_tags",
                schema: "chat",
                table: "conversations");
        }
    }
}
