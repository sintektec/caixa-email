using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sintek.Mail.Persistence.Migrations
{
    /// <summary>
    /// Cria o índice de texto completo (FTS5) que sustenta a pesquisa local.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A especificação exige pesquisa rápida disponível <b>mesmo offline</b>, cobrindo
    /// assunto, corpo, remetente, destinatários e nome de anexo. Um <c>LIKE '%termo%'</c>
    /// sobre a tabela de mensagens não atende: ele varre a caixa inteira a cada tecla
    /// digitada.
    /// </para>
    /// <para>
    /// O EF Core não modela tabelas virtuais, então a criação é feita em SQL puro. A
    /// tabela usa <c>content=''</c> (modo "contentless"): o FTS5 guarda apenas o índice
    /// invertido, sem duplicar o texto que já está em <c>Messages</c> e
    /// <c>MessageBodies</c> — o que evitaria praticamente dobrar o tamanho do banco.
    /// </para>
    /// <para>
    /// A sincronização do índice é feita por gatilhos, e não em código, para que ele
    /// permaneça correto mesmo quando as tabelas forem alteradas fora dos casos de uso.
    /// </para>
    /// </remarks>
    public partial class FullTextSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 'remove_diacritics 2' faz "orçamento" ser encontrado por "orcamento" —
            // indispensável em português.
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

            // O FTS5 indexa por rowid inteiro; esta tabela liga cada rowid ao Guid da
            // mensagem, já que a tabela virtual não aceita chave textual.
            migrationBuilder.Sql("""
                CREATE TABLE "MessagesFtsMap" (
                    "Rowid" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "MessageId" TEXT NOT NULL UNIQUE
                );
                """);

            // Inserção: registra o mapeamento e indexa assunto, prévia e remetente. Corpo
            // e nomes de anexo entram depois, quando são baixados.
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

            // Atualização: no modo contentless o FTS5 exige apagar a entrada antiga com o
            // comando 'delete' antes de reinserir. Um UPDATE direto corromperia o índice.
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Messages_Fts_Delete\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Messages_Fts_Update\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Messages_Fts_Insert\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MessagesFtsMap\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MessagesFts\";");
        }
    }
}
