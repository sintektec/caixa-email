using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Uma operação aguardando sincronização, como exibida ao usuário.</summary>
public sealed partial class OutboxItemViewModel : ObservableObject
{
    /// <summary>Identificador da operação.</summary>
    public required Guid OperationId { get; init; }

    /// <summary>Conta dona da operação.</summary>
    public required Guid AccountId { get; init; }

    /// <summary>Tipo da operação.</summary>
    public required OutboxOperationType OperationType { get; init; }

    /// <summary>Situação atual.</summary>
    public required OutboxOperationStatus Status { get; init; }

    /// <summary>Quantas tentativas já foram feitas.</summary>
    public int AttemptCount { get; init; }

    /// <summary>Último erro, quando houve.</summary>
    public string? LastError { get; init; }

    /// <summary>Quando será tentada de novo.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>
    /// Descrição da operação em português.
    /// </summary>
    /// <remarks>
    /// O nome do enum não serve à interface: "SetFolderSubscription" não diz nada a quem
    /// só quer saber por que a mensagem ainda não saiu.
    /// </remarks>
    public string Description => OperationType switch
    {
        OutboxOperationType.SendMessage => "Enviar mensagem",
        OutboxOperationType.MarkAsRead => "Marcar como lida",
        OutboxOperationType.MarkAsUnread => "Marcar como não lida",
        OutboxOperationType.SetFlag => "Sinalizar",
        OutboxOperationType.ClearFlag => "Remover sinalizador",
        OutboxOperationType.MoveMessage => "Mover mensagem",
        OutboxOperationType.CopyMessage => "Copiar mensagem",
        OutboxOperationType.DeleteMessage => "Excluir mensagem",
        OutboxOperationType.ExpungeFolder => "Esvaziar pasta",
        OutboxOperationType.CreateFolder => "Criar pasta",
        OutboxOperationType.RenameFolder => "Renomear pasta",
        OutboxOperationType.DeleteFolder => "Excluir pasta",
        OutboxOperationType.SetFolderSubscription => "Alterar assinatura da pasta",
        OutboxOperationType.AppendDraft => "Gravar rascunho no servidor",
        _ => "Operação de sincronização",
    };

    /// <summary>Situação em texto, para exibição e para leitores de tela.</summary>
    public string StatusDescription => Status switch
    {
        OutboxOperationStatus.Pending => "Aguardando conexão.",
        OutboxOperationStatus.InProgress => "Em andamento.",
        OutboxOperationStatus.Failed => $"Falhou {AttemptCount} vez(es). Será tentada de novo.",
        OutboxOperationStatus.Dead => "Falhou definitivamente. É preciso decidir o que fazer.",
        OutboxOperationStatus.Cancelled => "Cancelada.",
        _ => "Concluída.",
    };

    /// <summary>
    /// Se a operação exige decisão do usuário.
    /// </summary>
    /// <remarks>
    /// Só as definitivamente mortas. Uma que ainda vai ser tentada não é problema dele — e
    /// pedir atenção a cada falha temporária de rede transformaria a fila em ruído.
    /// </remarks>
    public bool NeedsAttention => Status == OutboxOperationStatus.Dead;
}

/// <summary>
/// Mostra a fila de operações aguardando sincronização.
/// </summary>
/// <remarks>
/// A especificação exige que a fila seja visível. O motivo é concreto: no modo
/// offline-first o usuário age sobre dados locais e o efeito no servidor acontece depois.
/// Sem essa tela, "já enviei" e "ainda não saiu daqui" seriam indistinguíveis para ele.
/// </remarks>
public sealed partial class OutboxQueueViewModel : ObservableObject
{
    private readonly IOutboxRepository _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public OutboxQueueViewModel(
        IOutboxRepository outbox, IUnitOfWork unitOfWork, TimeProvider timeProvider)
    {
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Operações pendentes.</summary>
    public ObservableCollection<OutboxItemViewModel> Operations { get; } = [];

    /// <summary>Conta cujas operações são exibidas. Nulo exibe todas.</summary>
    [ObservableProperty]
    private Guid? _accountId;

    /// <summary>Operação selecionada.</summary>
    [ObservableProperty]
    private OutboxItemViewModel? _selectedOperation;

    /// <summary>Mensagem de erro ou aviso.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Se há operação em andamento.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Quantas operações aguardam.</summary>
    public int PendingCount => Operations.Count;

    /// <summary>Quantas exigem decisão do usuário.</summary>
    public int NeedsAttentionCount => Operations.Count(o => o.NeedsAttention);

    /// <summary>Se a fila está vazia.</summary>
    public bool IsEmpty => Operations.Count == 0;

    /// <summary>Se há operação selecionada.</summary>
    public bool HasSelection => SelectedOperation is not null;

    /// <summary>Se há mensagem a exibir na faixa de aviso.</summary>
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Carrega a fila.</summary>
    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;

        try
        {
            Operations.Clear();

            var pending = await _outbox.ListPendingAsync(AccountId, cancellationToken).ConfigureAwait(true);

            foreach (var operation in pending)
            {
                Operations.Add(new OutboxItemViewModel
                {
                    OperationId = operation.Id,
                    AccountId = operation.AccountId,
                    OperationType = operation.OperationType,
                    Status = operation.Status,
                    AttemptCount = operation.AttemptCount,
                    LastError = operation.LastError,
                    NextAttemptAt = operation.NextAttemptAt,
                });
            }

            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(NeedsAttentionCount));
            OnPropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Cancela a operação selecionada.
    /// </summary>
    /// <remarks>
    /// Cancelar não desfaz o efeito local — a mensagem continua movida, o marcador continua
    /// alterado. O que se descarta é a tentativa de aplicar aquilo no servidor, e é isso que
    /// a interface precisa deixar claro antes de perguntar.
    /// </remarks>
    [RelayCommand]
    public async Task CancelSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedOperation is not { } item)
        {
            return;
        }

        var operation = await _outbox.GetByIdAsync(item.OperationId, cancellationToken).ConfigureAwait(true);

        if (operation is null)
        {
            StatusMessage = "A operação já não está mais na fila.";
            return;
        }

        operation.Cancel(_timeProvider.GetUtcNow());
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(true);

        Operations.Remove(item);
        SelectedOperation = null;
        StatusMessage = null;

        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(NeedsAttentionCount));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedOperationChanged(OutboxItemViewModel? value)
        => OnPropertyChanged(nameof(HasSelection));

    partial void OnStatusMessageChanged(string? value)
        => OnPropertyChanged(nameof(HasStatusMessage));
}
