using System.Globalization;
using System.Text;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Services;

/// <summary>Um contato lido de um arquivo vCard, ainda sem vínculo com conta.</summary>
/// <param name="Uid">Identificador do contato na origem, quando declarado.</param>
/// <param name="DisplayName">Nome exibido (<c>FN</c>).</param>
/// <param name="GivenName">Primeiro nome (<c>N</c>).</param>
/// <param name="FamilyName">Sobrenome (<c>N</c>).</param>
/// <param name="Organization">Empresa (<c>ORG</c>).</param>
/// <param name="JobTitle">Cargo (<c>TITLE</c>).</param>
/// <param name="PhoneNumber">Telefone (<c>TEL</c>).</param>
/// <param name="Notes">Anotações (<c>NOTE</c>).</param>
/// <param name="Emails">Endereços, o principal primeiro.</param>
public sealed record VCardContact(
    string? Uid,
    string DisplayName,
    string? GivenName,
    string? FamilyName,
    string? Organization,
    string? JobTitle,
    string? PhoneNumber,
    string? Notes,
    IReadOnlyList<VCardEmail> Emails);

/// <summary>Um endereço de e-mail declarado em um vCard.</summary>
/// <param name="Address">Endereço.</param>
/// <param name="Label">Rótulo do parâmetro <c>TYPE</c>, quando houver.</param>
/// <param name="IsPreferred">Se veio marcado como preferencial (<c>PREF</c>).</param>
public readonly record struct VCardEmail(EmailAddress Address, string? Label, bool IsPreferred);

/// <summary>
/// Lê e escreve vCard 3.0 e 4.0 (RFC 2426 e RFC 6350).
/// </summary>
/// <remarks>
/// <para>
/// Escrito à mão em vez de importar uma biblioteca porque o subconjunto que interessa é
/// pequeno — sete propriedades — e porque o formato exige tratar coisas que uma biblioteca
/// genérica resolveria de modo diferente do que este produto precisa: linha dobrada,
/// escape de vírgula e ponto e vírgula, e a diferença de sintaxe do <c>PREF</c> entre as
/// duas versões.
/// </para>
/// <para>
/// <b>A leitura nunca lança por conteúdo malformado.</b> Um arquivo exportado de outro
/// cliente costuma trazer propriedades desconhecidas, versões antigas e endereços
/// inválidos; abortar a importação inteira por causa de um cartão ruim faria o usuário
/// perder os outros duzentos. O que não dá para entender é ignorado, e
/// <see cref="VCardReadResult.SkippedCards"/> informa quantos ficaram de fora.
/// </para>
/// <para>
/// A escrita emite vCard 3.0 — é a versão que o Outlook, o Gmail e o catálogo do Windows
/// importam sem reclamar. O 4.0 é mais correto e menos aceito.
/// </para>
/// </remarks>
public static class VCardSerializer
{
    /// <summary>Resultado da leitura de um arquivo.</summary>
    /// <param name="Contacts">Contatos entendidos.</param>
    /// <param name="SkippedCards">Cartões descartados por não terem nome nem endereço.</param>
    public readonly record struct VCardReadResult(
        IReadOnlyList<VCardContact> Contacts, int SkippedCards);

    /// <summary>Lê todos os cartões de um texto vCard.</summary>
    public static VCardReadResult Read(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new VCardReadResult([], 0);
        }

        var contacts = new List<VCardContact>();
        var skipped = 0;
        List<VCardLine>? current = null;

        foreach (var line in Unfold(content))
        {
            var parsed = VCardLine.Parse(line);

            if (parsed is null)
            {
                continue;
            }

            if (string.Equals(parsed.Value.Name, "BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                current = [];
                continue;
            }

            if (string.Equals(parsed.Value.Name, "END", StringComparison.OrdinalIgnoreCase))
            {
                if (current is null)
                {
                    continue;
                }

                if (Build(current) is { } contact)
                {
                    contacts.Add(contact);
                }
                else
                {
                    skipped++;
                }

                current = null;
                continue;
            }

            current?.Add(parsed.Value);
        }

        return new VCardReadResult(contacts, skipped);
    }

    /// <summary>Escreve os contatos como vCard 3.0.</summary>
    public static string Write(IReadOnlyCollection<VCardContact> contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);

        var builder = new StringBuilder();

        foreach (var contact in contacts)
        {
            builder.Append("BEGIN:VCARD\r\n");
            builder.Append("VERSION:3.0\r\n");

            AppendProperty(builder, "FN", contact.DisplayName);

            // N é obrigatório no 3.0 e tem cinco componentes fixos, mesmo vazios: quem lê
            // conta os ponto e vírgulas.
            builder.Append("N:")
                .Append(Escape(contact.FamilyName ?? string.Empty))
                .Append(';')
                .Append(Escape(contact.GivenName ?? string.Empty))
                .Append(";;;\r\n");

            AppendProperty(builder, "ORG", contact.Organization);
            AppendProperty(builder, "TITLE", contact.JobTitle);
            AppendProperty(builder, "TEL;TYPE=VOICE", contact.PhoneNumber);
            AppendProperty(builder, "NOTE", contact.Notes);
            AppendProperty(builder, "UID", contact.Uid);

            foreach (var email in contact.Emails)
            {
                var type = email.IsPreferred ? "TYPE=INTERNET,PREF" : "TYPE=INTERNET";
                builder.Append("EMAIL;").Append(type).Append(':')
                    .Append(email.Address.Value).Append("\r\n");
            }

            builder.Append("END:VCARD\r\n");
        }

        return builder.ToString();
    }

    private static void AppendProperty(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append(name).Append(':').Append(Escape(value)).Append("\r\n");
    }

    private static VCardContact? Build(List<VCardLine> lines)
    {
        string? uid = null, displayName = null, given = null, family = null;
        string? organization = null, jobTitle = null, phone = null, notes = null;
        var emails = new List<VCardEmail>();

        foreach (var line in lines)
        {
            switch (line.Name.ToUpperInvariant())
            {
                case "UID":
                    uid = Unescape(line.Value);
                    break;

                case "FN":
                    displayName = Unescape(line.Value);
                    break;

                case "N":
                    // N é "sobrenome;nome;meio;prefixo;sufixo" — os dois primeiros bastam.
                    var parts = SplitComponents(line.Value);
                    family = parts.Length > 0 ? Unescape(parts[0]) : null;
                    given = parts.Length > 1 ? Unescape(parts[1]) : null;
                    break;

                case "ORG":
                    // ORG também é composta; o primeiro componente é o nome da empresa.
                    organization = Unescape(SplitComponents(line.Value).FirstOrDefault() ?? line.Value);
                    break;

                case "TITLE":
                    jobTitle = Unescape(line.Value);
                    break;

                case "TEL":
                    phone ??= Unescape(line.Value);
                    break;

                case "NOTE":
                    notes = Unescape(line.Value);
                    break;

                case "EMAIL":
                    if (EmailAddress.TryParse(Unescape(line.Value).Trim(), out var address)
                        && !emails.Any(e => e.Address == address))
                    {
                        emails.Add(new VCardEmail(address, LabelOf(line), IsPreferred(line)));
                    }

                    break;
            }
        }

        // Sem nome e sem endereço não há contato: é o cartão que não vale a pena importar.
        if (string.IsNullOrWhiteSpace(displayName) && emails.Count == 0)
        {
            return null;
        }

        // Nome ausente cai para o composto de N e, em último caso, para o endereço — um
        // contato sem rótulo é inutilizável na lista.
        displayName = FirstNonEmpty(
            displayName,
            JoinName(given, family),
            emails.Count > 0 ? emails[0].Address.Value : null);

        if (displayName is null)
        {
            return null;
        }

        // O preferencial vem primeiro: é ele que entra no campo Para.
        var ordered = emails
            .OrderByDescending(e => e.IsPreferred)
            .ToList();

        return new VCardContact(
            NullIfBlank(uid),
            displayName,
            NullIfBlank(given),
            NullIfBlank(family),
            NullIfBlank(organization),
            NullIfBlank(jobTitle),
            NullIfBlank(phone),
            NullIfBlank(notes),
            ordered);
    }

    private static string? LabelOf(VCardLine line)
    {
        var types = line.ParameterValues("TYPE")
            .Where(t => !string.Equals(t, "INTERNET", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(t, "PREF", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return types.Count > 0 ? types[0] : null;
    }

    /// <summary>
    /// Se o endereço veio marcado como preferencial.
    /// </summary>
    /// <remarks>
    /// A marca mudou entre versões: no 3.0 é <c>TYPE=PREF</c>, no 4.0 é <c>PREF=1</c>.
    /// Aceitar as duas é o que faz um arquivo exportado de qualquer cliente preservar
    /// qual endereço é o principal.
    /// </remarks>
    private static bool IsPreferred(VCardLine line)
        => line.ParameterValues("TYPE")
                .Any(t => string.Equals(t, "PREF", StringComparison.OrdinalIgnoreCase))
            || line.ParameterValues("PREF").Contains("1", StringComparer.Ordinal);

    /// <summary>
    /// Desdobra as linhas continuadas.
    /// </summary>
    /// <remarks>
    /// O vCard quebra linha aos 75 octetos e marca a continuação com um espaço ou tabulação
    /// no início da linha seguinte. Sem juntar antes de interpretar, um endereço longo
    /// chegaria partido ao meio e seria descartado como inválido.
    /// </remarks>
    private static IEnumerable<string> Unfold(string content)
    {
        var builder = new StringBuilder();

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                builder.Append(line[1..]);
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }

            builder.Append(line);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    /// <summary>Separa os componentes de uma propriedade composta, respeitando o escape.</summary>
    private static string[] SplitComponents(string value)
    {
        var parts = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                current.Append(value[i]).Append(value[i + 1]);
                i++;
                continue;
            }

            if (value[i] == ';')
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(value[i]);
        }

        parts.Add(current.ToString());
        return [.. parts];
    }

    private static string Escape(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .ReplaceLineEndings("\\n");

    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            builder.Append(value[++i] switch
            {
                'n' or 'N' => "\n",
                var other => other.ToString(CultureInfo.InvariantCulture),
            });
        }

        return builder.ToString();
    }

    private static string? FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();

    private static string? JoinName(string? given, string? family)
    {
        var joined = string.Join(' ', new[] { given, family }
            .Where(p => !string.IsNullOrWhiteSpace(p)));

        return joined.Length > 0 ? joined : null;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Uma linha já desdobrada: nome, parâmetros e valor.</summary>
    private readonly record struct VCardLine(
        string Name, IReadOnlyList<(string Key, string Value)> Parameters, string Value)
    {
        public static VCardLine? Parse(string line)
        {
            var separator = IndexOfValueSeparator(line);

            if (separator < 0)
            {
                return null;
            }

            var head = line[..separator];
            var value = line[(separator + 1)..];
            var segments = head.Split(';');

            // O nome pode vir com grupo ("item1.EMAIL"), que não interessa aqui.
            var name = segments[0];
            var dot = name.LastIndexOf('.');

            if (dot >= 0 && dot + 1 < name.Length)
            {
                name = name[(dot + 1)..];
            }

            var parameters = new List<(string, string)>();

            foreach (var segment in segments.Skip(1))
            {
                var equals = segment.IndexOf('=', StringComparison.Ordinal);

                parameters.Add(equals < 0
                    // Parâmetro sem chave é a forma abreviada do vCard 2.1 ("EMAIL;PREF:").
                    ? ("TYPE", segment)
                    : (segment[..equals], segment[(equals + 1)..]));
            }

            return new VCardLine(name.Trim(), parameters, value);
        }

        /// <summary>
        /// Acha os dois-pontos que separam o valor, ignorando os que estão entre aspas.
        /// </summary>
        /// <remarks>
        /// Um parâmetro pode carregar dois-pontos entre aspas — o caso real é
        /// <c>TYPE="work:main"</c>. Procurar o primeiro dois-pontos cortaria a linha no
        /// lugar errado.
        /// </remarks>
        private static int IndexOfValueSeparator(string line)
        {
            var quoted = false;

            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '"')
                {
                    quoted = !quoted;
                }
                else if (line[i] == ':' && !quoted)
                {
                    return i;
                }
            }

            return -1;
        }

        public IEnumerable<string> ParameterValues(string key)
            => Parameters
                .Where(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(v => v.Trim('"').Trim());
    }
}
