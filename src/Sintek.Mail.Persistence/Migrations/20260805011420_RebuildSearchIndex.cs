using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sintek.Mail.Persistence.Migrations
{
    /// <summary>
    /// Reconstrói o índice de pesquisa para cobrir corpo, participantes e nomes de anexo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O índice original usava FTS5 em modo "contentless" (<c>content=''</c>), que exige
    /// reapresentar os valores antigos ao excluir uma entrada. Isso funciona para colunas
    /// de <c>Messages</c> — o gatilho as lê de <c>OLD</c> —, mas é impossível para corpo,
    /// participantes e anexos: eles vivem em outras tabelas, e um gatilho de
    /// <c>MessageBodies</c> não tem como saber o que estava indexado antes. Resultado: as
    /// três colunas existiam no índice e ficavam permanentemente vazias.
    /// </para>
    /// <para>
    /// A reconstrução usa "external content": uma tabela física <c>MessagesSearch</c>
    /// espelha o texto pesquisável de cada mensagem, e o FTS5 a indexa via
    /// <c>content='MessagesSearch'</c>. Os gatilhos das tabelas de origem apenas mantêm o
    /// espelho; os gatilhos do espelho mantêm o índice, com <c>OLD</c> e <c>NEW</c>
    /// disponíveis — que é exatamente o que o modo contentless não oferecia.
    /// </para>
    /// <para>
    /// O corpo é truncado em 64 mil caracteres no espelho para limitar a duplicação de
    /// texto no banco; nenhum termo de busca razoável vive além desse ponto.
    /// </para>
    /// </remarks>
    public partial class RebuildSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // O índice antigo sai por inteiro; o novo é reconstruído do zero logo abaixo.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Messages_Fts_Delete\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Messages_Fts_Update\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Messages_Fts_Insert\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MessagesFts\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MessagesFtsMap\";");

            // O espelho: uma linha por mensagem, só com o texto pesquisável. O FromAddress
            // inclui o nome exibido para que "João" encontre a mensagem tanto quanto o
            // endereço encontraria.
            migrationBuilder.Sql("""
                CREATE TABLE "MessagesSearch" (
                    "Rowid" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "MessageId" TEXT NOT NULL UNIQUE,
                    "Subject" TEXT NOT NULL DEFAULT '',
                    "Preview" TEXT NOT NULL DEFAULT '',
                    "TextBody" TEXT NOT NULL DEFAULT '',
                    "FromAddress" TEXT NOT NULL DEFAULT '',
                    "Participants" TEXT NOT NULL DEFAULT '',
                    "AttachmentNames" TEXT NOT NULL DEFAULT ''
                );
                """);

            // 'remove_diacritics 2' faz "orcamento" encontrar "Orçamento" — indispensável
            // em português.
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE "MessagesFts" USING fts5(
                    "Subject",
                    "Preview",
                    "TextBody",
                    "FromAddress",
                    "Participants",
                    "AttachmentNames",
                    content='MessagesSearch',
                    content_rowid='Rowid',
                    tokenize='unicode61 remove_diacritics 2'
                );
                """);

            // Gatilhos do espelho para o índice: o padrão canônico de external content do
            // FTS5. Aqui OLD e NEW existem, então o 'delete' sempre recebe os valores que
            // estavam indexados.
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_MessagesSearch_Insert" AFTER INSERT ON "MessagesSearch"
                BEGIN
                    INSERT INTO "MessagesFts"(
                        "rowid", "Subject", "Preview", "TextBody", "FromAddress", "Participants", "AttachmentNames")
                    VALUES (
                        NEW."Rowid", NEW."Subject", NEW."Preview", NEW."TextBody",
                        NEW."FromAddress", NEW."Participants", NEW."AttachmentNames");
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_MessagesSearch_Delete" AFTER DELETE ON "MessagesSearch"
                BEGIN
                    INSERT INTO "MessagesFts"(
                        "MessagesFts", "rowid", "Subject", "Preview", "TextBody", "FromAddress",
                        "Participants", "AttachmentNames")
                    VALUES (
                        'delete', OLD."Rowid", OLD."Subject", OLD."Preview", OLD."TextBody",
                        OLD."FromAddress", OLD."Participants", OLD."AttachmentNames");
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_MessagesSearch_Update" AFTER UPDATE ON "MessagesSearch"
                BEGIN
                    INSERT INTO "MessagesFts"(
                        "MessagesFts", "rowid", "Subject", "Preview", "TextBody", "FromAddress",
                        "Participants", "AttachmentNames")
                    VALUES (
                        'delete', OLD."Rowid", OLD."Subject", OLD."Preview", OLD."TextBody",
                        OLD."FromAddress", OLD."Participants", OLD."AttachmentNames");

                    INSERT INTO "MessagesFts"(
                        "rowid", "Subject", "Preview", "TextBody", "FromAddress", "Participants", "AttachmentNames")
                    VALUES (
                        NEW."Rowid", NEW."Subject", NEW."Preview", NEW."TextBody",
                        NEW."FromAddress", NEW."Participants", NEW."AttachmentNames");
                END;
                """);

            // Gatilhos das tabelas de origem para o espelho. Mensagem nova entra com o que
            // os cabeçalhos têm; corpo e anexos chegam depois, quando forem baixados.
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Messages_Search_Insert" AFTER INSERT ON "Messages"
                BEGIN
                    INSERT INTO "MessagesSearch" ("MessageId", "Subject", "Preview", "FromAddress")
                    VALUES (
                        NEW."Id",
                        COALESCE(NEW."Subject", ''),
                        COALESCE(NEW."Preview", ''),
                        COALESCE(NEW."FromAddress", '') || ' ' || COALESCE(NEW."FromDisplayName", ''));
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Messages_Search_Update"
                AFTER UPDATE OF "Subject", "Preview", "FromAddress", "FromDisplayName" ON "Messages"
                BEGIN
                    UPDATE "MessagesSearch" SET
                        "Subject" = COALESCE(NEW."Subject", ''),
                        "Preview" = COALESCE(NEW."Preview", ''),
                        "FromAddress" = COALESCE(NEW."FromAddress", '') || ' ' || COALESCE(NEW."FromDisplayName", '')
                    WHERE "MessageId" = NEW."Id";
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Messages_Search_Delete" AFTER DELETE ON "Messages"
                BEGIN
                    DELETE FROM "MessagesSearch" WHERE "MessageId" = OLD."Id";
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_MessageBodies_Search_Insert" AFTER INSERT ON "MessageBodies"
                BEGIN
                    UPDATE "MessagesSearch"
                    SET "TextBody" = substr(COALESCE(NEW."TextBody", ''), 1, 65536)
                    WHERE "MessageId" = NEW."MessageId";
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_MessageBodies_Search_Update"
                AFTER UPDATE OF "TextBody" ON "MessageBodies"
                BEGIN
                    UPDATE "MessagesSearch"
                    SET "TextBody" = substr(COALESCE(NEW."TextBody", ''), 1, 65536)
                    WHERE "MessageId" = NEW."MessageId";
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_MessageBodies_Search_Delete" AFTER DELETE ON "MessageBodies"
                BEGIN
                    UPDATE "MessagesSearch" SET "TextBody" = '' WHERE "MessageId" = OLD."MessageId";
                END;
                """);

            // Participantes e anexos são agregados: cada alteração recalcula a lista
            // inteira da mensagem. É mais simples que manter diffs e o volume por mensagem
            // é pequeno — dezenas de participantes, não milhares.
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_MessageAddresses_Search_Insert" AFTER INSERT ON "MessageAddresses"
                BEGIN
                    UPDATE "MessagesSearch" SET "Participants" = (
                        SELECT COALESCE(group_concat(
                            COALESCE(a."DisplayName", '') || ' ' || a."Address", ' '), '')
                        FROM "MessageAddresses" a
                        WHERE a."MessageId" = NEW."MessageId")
                    WHERE "MessageId" = NEW."MessageId";
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_MessageAddresses_Search_Update"
                AFTER UPDATE OF "Address", "DisplayName" ON "MessageAddresses"
                BEGIN
                    UPDATE "MessagesSearch" SET "Participants" = (
                        SELECT COALESCE(group_concat(
                            COALESCE(a."DisplayName", '') || ' ' || a."Address", ' '), '')
                        FROM "MessageAddresses" a
                        WHERE a."MessageId" = NEW."MessageId")
                    WHERE "MessageId" = NEW."MessageId";
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_MessageAddresses_Search_Delete" AFTER DELETE ON "MessageAddresses"
                BEGIN
                    UPDATE "MessagesSearch" SET "Participants" = (
                        SELECT COALESCE(group_concat(
                            COALESCE(a."DisplayName", '') || ' ' || a."Address", ' '), '')
                        FROM "MessageAddresses" a
                        WHERE a."MessageId" = OLD."MessageId")
                    WHERE "MessageId" = OLD."MessageId";
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Attachments_Search_Insert" AFTER INSERT ON "Attachments"
                BEGIN
                    UPDATE "MessagesSearch" SET "AttachmentNames" = (
                        SELECT COALESCE(group_concat(t."FileName", ' '), '')
                        FROM "Attachments" t
                        WHERE t."MessageId" = NEW."MessageId")
                    WHERE "MessageId" = NEW."MessageId";
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Attachments_Search_Update"
                AFTER UPDATE OF "FileName" ON "Attachments"
                BEGIN
                    UPDATE "MessagesSearch" SET "AttachmentNames" = (
                        SELECT COALESCE(group_concat(t."FileName", ' '), '')
                        FROM "Attachments" t
                        WHERE t."MessageId" = NEW."MessageId")
                    WHERE "MessageId" = NEW."MessageId";
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Attachments_Search_Delete" AFTER DELETE ON "Attachments"
                BEGIN
                    UPDATE "MessagesSearch" SET "AttachmentNames" = (
                        SELECT COALESCE(group_concat(t."FileName", ' '), '')
                        FROM "Attachments" t
                        WHERE t."MessageId" = OLD."MessageId")
                    WHERE "MessageId" = OLD."MessageId";
                END;
                """);

            // Repovoa o espelho com o que já existe no banco. Os gatilhos do espelho já
            // estão ativos, então cada linha inserida aqui entra no índice na sequência.
            migrationBuilder.Sql("""
                INSERT INTO "MessagesSearch" (
                    "MessageId", "Subject", "Preview", "TextBody", "FromAddress", "Participants", "AttachmentNames")
                SELECT
                    m."Id",
                    COALESCE(m."Subject", ''),
                    COALESCE(m."Preview", ''),
                    COALESCE((
                        SELECT substr(COALESCE(b."TextBody", ''), 1, 65536)
                        FROM "MessageBodies" b WHERE b."MessageId" = m."Id"), ''),
                    COALESCE(m."FromAddress", '') || ' ' || COALESCE(m."FromDisplayName", ''),
                    COALESCE((
                        SELECT group_concat(COALESCE(a."DisplayName", '') || ' ' || a."Address", ' ')
                        FROM "MessageAddresses" a WHERE a."MessageId" = m."Id"), ''),
                    COALESCE((
                        SELECT group_concat(t."FileName", ' ')
                        FROM "Attachments" t WHERE t."MessageId" = m."Id"), '')
                FROM "Messages" m;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Attachments_Search_Delete\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Attachments_Search_Update\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Attachments_Search_Insert\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_MessageAddresses_Search_Delete\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_MessageAddresses_Search_Update\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_MessageAddresses_Search_Insert\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_MessageBodies_Search_Delete\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_MessageBodies_Search_Update\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_MessageBodies_Search_Insert\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Messages_Search_Delete\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Messages_Search_Update\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Messages_Search_Insert\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_MessagesSearch_Update\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_MessagesSearch_Delete\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_MessagesSearch_Insert\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MessagesFts\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MessagesSearch\";");

            // Restaura o índice contentless original, limitado a assunto, prévia e
            // remetente — o estado da migração FullTextSearchIndex.
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE "MessagesFts" USING fts5(
                    "Subject",
                    "Preview",
                    "TextBody",
                    "FromAddress",
                    "Participants",
                    "AttachmentNames",
                    content='',
                    tokenize='unicode61 remove_diacritics 2'
                );
                """);

            migrationBuilder.Sql("""
                CREATE TABLE "MessagesFtsMap" (
                    "Rowid" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "MessageId" TEXT NOT NULL UNIQUE
                );
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Messages_Fts_Insert" AFTER INSERT ON "Messages"
                BEGIN
                    INSERT INTO "MessagesFtsMap" ("MessageId") VALUES (NEW."Id");

                    INSERT INTO "MessagesFts"(
                        "rowid", "Subject", "Preview", "TextBody", "FromAddress", "Participants", "AttachmentNames")
                    VALUES (
                        (SELECT "Rowid" FROM "MessagesFtsMap" WHERE "MessageId" = NEW."Id"),
                        COALESCE(NEW."Subject", ''),
                        COALESCE(NEW."Preview", ''),
                        '',
                        COALESCE(NEW."FromAddress", ''),
                        '',
                        '');
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Messages_Fts_Update"
                AFTER UPDATE OF "Subject", "Preview", "FromAddress" ON "Messages"
                BEGIN
                    INSERT INTO "MessagesFts"("MessagesFts", "rowid", "Subject", "Preview", "TextBody",
                        "FromAddress", "Participants", "AttachmentNames")
                    VALUES ('delete',
                        (SELECT "Rowid" FROM "MessagesFtsMap" WHERE "MessageId" = OLD."Id"),
                        COALESCE(OLD."Subject", ''), COALESCE(OLD."Preview", ''), '',
                        COALESCE(OLD."FromAddress", ''), '', '');

                    INSERT INTO "MessagesFts"(
                        "rowid", "Subject", "Preview", "TextBody", "FromAddress", "Participants", "AttachmentNames")
                    VALUES (
                        (SELECT "Rowid" FROM "MessagesFtsMap" WHERE "MessageId" = NEW."Id"),
                        COALESCE(NEW."Subject", ''),
                        COALESCE(NEW."Preview", ''),
                        '',
                        COALESCE(NEW."FromAddress", ''),
                        '',
                        '');
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Messages_Fts_Delete" AFTER DELETE ON "Messages"
                BEGIN
                    INSERT INTO "MessagesFts"("MessagesFts", "rowid", "Subject", "Preview", "TextBody",
                        "FromAddress", "Participants", "AttachmentNames")
                    VALUES ('delete',
                        (SELECT "Rowid" FROM "MessagesFtsMap" WHERE "MessageId" = OLD."Id"),
                        COALESCE(OLD."Subject", ''), COALESCE(OLD."Preview", ''), '',
                        COALESCE(OLD."FromAddress", ''), '', '');

                    DELETE FROM "MessagesFtsMap" WHERE "MessageId" = OLD."Id";
                END;
                """);
        }
    }
}
