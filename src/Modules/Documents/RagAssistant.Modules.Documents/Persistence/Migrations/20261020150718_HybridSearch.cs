using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagAssistant.Modules.Documents.Persistence.Migrations
{
    /// <summary>
    /// Keyword search for hybrid retrieval (spec 019). The two columns are not in the EF model: <c>search_config</c> is set by the
    /// database from the document's language (on insert, and whenever the language changes), and <c>search_vector</c> is generated
    /// from it, so every write path (import, new version, edit, language detection) stays consistent without code of its own.
    /// </summary>
    public partial class HybridSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            // French stemming after accent folding, so "conges payes" matches "congés payés".
            migrationBuilder.Sql("""
                CREATE TEXT SEARCH CONFIGURATION documents.french_unaccent (COPY = pg_catalog.french);
                ALTER TEXT SEARCH CONFIGURATION documents.french_unaccent
                    ALTER MAPPING FOR hword, hword_part, word WITH unaccent, french_stem;
                """);

            // The one mapping from a document language to its configuration; the keyword query uses it too.
            migrationBuilder.Sql("""
                CREATE FUNCTION documents.search_config_for(language text) RETURNS regconfig
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE
                    AS $$
                        SELECT CASE lower(language)
                            WHEN 'en' THEN 'pg_catalog.english'::regconfig
                            WHEN 'fr' THEN 'documents.french_unaccent'::regconfig
                            ELSE 'pg_catalog.simple'::regconfig
                        END
                    $$;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE documents.passages ADD COLUMN search_config regconfig;
                UPDATE documents.passages p
                    SET search_config = documents.search_config_for(d.language)
                    FROM documents.document_versions v
                    JOIN documents.documents d ON d.id = v.document_id
                    WHERE v.id = p.version_id;
                ALTER TABLE documents.passages ALTER COLUMN search_config SET NOT NULL;
                ALTER TABLE documents.passages
                    ADD COLUMN search_vector tsvector GENERATED ALWAYS AS (to_tsvector(search_config, text)) STORED;
                CREATE INDEX ix_passages_search_vector ON documents.passages USING gin (search_vector);
                """);

            // Generated columns are computed after BEFORE triggers, so the vector uses the configuration set here.
            migrationBuilder.Sql("""
                CREATE FUNCTION documents.passages_set_search_config() RETURNS trigger
                    LANGUAGE plpgsql
                    AS $$
                    BEGIN
                        SELECT documents.search_config_for(d.language) INTO NEW.search_config
                        FROM documents.document_versions v
                        JOIN documents.documents d ON d.id = v.document_id
                        WHERE v.id = NEW.version_id;
                        RETURN NEW;
                    END
                    $$;
                CREATE TRIGGER passages_set_search_config
                    BEFORE INSERT ON documents.passages
                    FOR EACH ROW EXECUTE FUNCTION documents.passages_set_search_config();
                """);

            // Same transaction as the language change, whoever makes it (an edit or detection at ingestion, spec 018).
            migrationBuilder.Sql("""
                CREATE FUNCTION documents.documents_sync_search_config() RETURNS trigger
                    LANGUAGE plpgsql
                    AS $$
                    BEGIN
                        UPDATE documents.passages p
                            SET search_config = documents.search_config_for(NEW.language)
                            FROM documents.document_versions v
                            WHERE v.document_id = NEW.id AND p.version_id = v.id;
                        RETURN NEW;
                    END
                    $$;
                CREATE TRIGGER documents_sync_search_config
                    AFTER UPDATE OF language ON documents.documents
                    FOR EACH ROW WHEN (OLD.language IS DISTINCT FROM NEW.language)
                    EXECUTE FUNCTION documents.documents_sync_search_config();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER documents_sync_search_config ON documents.documents;
                DROP FUNCTION documents.documents_sync_search_config();
                DROP TRIGGER passages_set_search_config ON documents.passages;
                DROP FUNCTION documents.passages_set_search_config();
                DROP INDEX documents.ix_passages_search_vector;
                ALTER TABLE documents.passages DROP COLUMN search_vector;
                ALTER TABLE documents.passages DROP COLUMN search_config;
                DROP FUNCTION documents.search_config_for(text);
                DROP TEXT SEARCH CONFIGURATION documents.french_unaccent;
                """);

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
