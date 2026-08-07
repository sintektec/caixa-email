namespace Sintek.Mail.Application.Sync;

/// <summary>Em que etapa a sincronização está.</summary>
public enum SyncStage
{
    /// <summary>Conectando e autenticando.</summary>
    Connecting,

    /// <summary>Enviando ao servidor o que foi feito offline.</summary>
    DrainingOutbox,

    /// <summary>Lendo a lista de pastas.</summary>
    MirroringFolders,

    /// <summary>Baixando cabeçalhos de uma pasta.</summary>
    ReadingFolder,

    /// <summary>Sincronizando a agenda.</summary>
    Calendar,

    /// <summary>Terminou.</summary>
    Done,
}

/// <summary>
/// Um instantâneo do que a sincronização está fazendo agora.
/// </summary>
/// <remarks>
/// <para>
/// Existe porque uma sincronização silenciosa de dois minutos é indistinguível de uma
/// travada. O usuário clicava e ficava sem nenhum sinal do que estava acontecendo — nem qual
/// conta, nem qual pasta, nem quanto falta.
/// </para>
/// <para>
/// É um <b>registro imutável</b>, e não um objeto mutável observado de fora: ele atravessa a
/// fronteira entre a thread do laço de sincronização e a da interface, e um objeto mutável
/// ali seria lido no meio de uma alteração.
/// </para>
/// </remarks>
/// <param name="Stage">Etapa atual.</param>
/// <param name="AccountName">Conta sendo sincronizada, como o usuário a nomeou.</param>
/// <param name="FolderName">Pasta atual, quando a etapa tem uma.</param>
/// <param name="AccountIndex">Índice da conta atual, a partir de 1.</param>
/// <param name="AccountCount">Quantas contas serão sincronizadas nesta rodada.</param>
/// <param name="ProcessedMessages">Mensagens já processadas nesta pasta.</param>
public readonly record struct SyncProgressReport(
    SyncStage Stage,
    string AccountName,
    string? FolderName = null,
    int AccountIndex = 1,
    int AccountCount = 1,
    int ProcessedMessages = 0)
{
    /// <summary>
    /// Quanto da rodada já passou, de 0 a 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mede <b>contas</b>, e não mensagens, porque só a contagem de contas é conhecida de
    /// antemão. Quantas mensagens uma pasta vai trazer só se sabe ao terminar de lê-la — e uma
    /// barra que recua quando o total aumenta é pior que nenhuma: ela destrói a confiança em
    /// todas as outras barras da aplicação.
    /// </para>
    /// <para>
    /// O detalhe fino — pasta e contagem de mensagens — aparece no texto ao lado, que pode
    /// crescer sem mentir sobre o que falta.
    /// </para>
    /// </remarks>
    public double Fraction => AccountCount <= 0
        ? 0
        : Math.Clamp((AccountIndex - 1 + StageFraction) / AccountCount, 0, 1);

    /// <summary>Quanto da conta atual já passou, estimado pela etapa.</summary>
    private double StageFraction => Stage switch
    {
        SyncStage.Connecting => 0.05,
        SyncStage.DrainingOutbox => 0.2,
        SyncStage.MirroringFolders => 0.35,
        SyncStage.ReadingFolder => 0.6,
        SyncStage.Calendar => 0.9,
        _ => 1,
    };

    /// <summary>Descrição em português do que está acontecendo.</summary>
    public string Description => Stage switch
    {
        SyncStage.Connecting => $"{AccountName}: conectando…",
        SyncStage.DrainingOutbox => $"{AccountName}: enviando alterações pendentes…",
        SyncStage.MirroringFolders => $"{AccountName}: lendo as pastas…",
        SyncStage.ReadingFolder => ProcessedMessages > 0
            ? $"{AccountName} — {FolderName}: {ProcessedMessages} mensagem(ns)"
            : $"{AccountName} — {FolderName}: lendo…",
        SyncStage.Calendar => $"{AccountName}: sincronizando a agenda…",
        _ => $"{AccountName}: concluído.",
    };
}
