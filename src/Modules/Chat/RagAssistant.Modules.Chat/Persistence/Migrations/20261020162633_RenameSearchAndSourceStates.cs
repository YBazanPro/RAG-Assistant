using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Chat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameSearchAndSourceStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.AlterColumn<string>(
                name: "title",
                schema: "chat",
                table: "conversations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "title_edited",
                schema: "chat",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "text_hash",
                schema: "chat",
                table: "citation_snapshots",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Spec 020: snapshots written before it get the hash of their stored text, PassageTextHash spelled out in SQL as in the
            // Documents migration, so the citation source state works for old answers too.
            migrationBuilder.Sql(
                """
                UPDATE chat.citation_snapshots
                SET text_hash = encode(sha256(convert_to(btrim(regexp_replace(normalize(text, NFC), '[ \t\n\r\f\v]+', ' ', 'g'), ' '), 'UTF8')), 'hex');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "title_edited",
                schema: "chat",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "text_hash",
                schema: "chat",
                table: "citation_snapshots");

            // unaccent stays: the Documents schema's keyword search needs it too.
            migrationBuilder.AlterColumn<string>(
                name: "title",
                schema: "chat",
                table: "conversations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
