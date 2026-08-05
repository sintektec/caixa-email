using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Folders;
using Sintek.Mail.Domain.Exceptions;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Opção "sem vínculo" ou um Diretório de Domínio, na lista de restrição.</summary>
/// <param name="Id">Diretório, ou <see langword="null"/> para remover o vínculo.</param>
/// <param name="Label">Texto exibido.</param>
public sealed record RestrictionChoice(Guid? Id, string Label);

/// <summary>
/// Ações sobre uma pasta: criar, renomear, excluir, favoritar e vincular a um Diretório de
/// Domínio.
/// </summary>
/// <remarks>
/// <para>
/// A exclusão é sempre em duas etapas quando a pasta não está vazia — a especificação manda
/// excluir "pastas vazias ou com confirmação", e o impacto medido é o que dá conteúdo à
/// confirmação: quantas subpastas, quantas mensagens, aqui e no servidor.
/// </para>
/// <para>
/// O vínculo de restrição delega a <c>SetFolderRestrictionHandler</c>, que propaga a herança
/// pela subárvore e recusa pasta que responderia a dois diretórios. A mensagem de recusa da
/// hierarquia chega pronta do domínio.
/// </para>
/// </remarks>
public sealed partial class FolderActionsViewModel : ObservableObject
{
    private readonly ManageFolderHandler _manage;
    private readonly SetFolderRestrictionHandler _restriction;
    private readonly IDomainDirectoryRepository _directories;
    private readonly IFolderRepository _folders;

    public FolderActionsViewModel(
        ManageFolderHandler manage,
        SetFolderRestrictionHandler restriction,
        IDomainDirectoryRepository directories,
        IFolderRepository folders)
    {
        _manage = manage;
        _restriction = restriction;
        _directories = directories;
        _folders = folders;
    }

    /// <summary>Conta dona da pasta.</summary>
    [ObservableProperty]
    private Guid? _accountId;

    /// <summary>Pasta em edição. Nulo quando é criação.</summary>
    [ObservableProperty]
    private Guid? _folderId;

    /// <summary>Pasta-mãe da criação, quando houver.</summary>
    [ObservableProperty]
    private Guid? _parentFolderId;

    /// <summary>Nome da pasta.</summary>
    [ObservableProperty]
    private string _folderName = string.Empty;

    /// <summary>Vínculo de restrição escolhido.</summary>
    [ObservableProperty]
    private RestrictionChoice? _selectedRestriction;

    /// <summary>Impacto medido da exclusão, exibido na confirmação.</summary>
    [ObservableProperty]
    private DeleteFolderImpact? _pendingDeleteImpact;

    /// <summary>Mensagem de erro ou aviso.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Se há operação em andamento.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Opções de vínculo: "sem vínculo" mais os diretórios existentes.</summary>
    public ObservableCollection<RestrictionChoice> RestrictionChoices { get; } = [];

    /// <summary>Se há mensagem a exibir.</summary>
    public bool HasStatusMessage => StatusMessage.Length > 0;

    /// <summary>Se há um impacto de exclusão aguardando confirmação.</summary>
    public bool HasPendingDelete => PendingDeleteImpact is not null;

    /// <summary>Resumo do que a exclusão levaria junto.</summary>
    public string DeleteSummary => PendingDeleteImpact is { } impact
        ? $"Excluir '{impact.FolderName}' apagará {impact.SubfolderCount} subpasta(s) e " +
          $"{impact.MessageCount} mensagem(ns), neste computador e no servidor."
        : string.Empty;

    /// <summary>Carrega as opções de vínculo e, quando é edição, o estado da pasta.</summary>
    public async Task InitializeAsync(
        Guid accountId,
        Guid? folderId = null,
        Guid? parentFolderId = null,
        CancellationToken cancellationToken = default)
    {
        AccountId = accountId;
        FolderId = folderId;
        ParentFolderId = parentFolderId;

        RestrictionChoices.Clear();
        RestrictionChoices.Add(new RestrictionChoice(null, "Sem vínculo — herda do ramo, se houver"));

        foreach (var directory in await _directories.ListAsync(cancellationToken).ConfigureAwait(true))
        {
            RestrictionChoices.Add(new RestrictionChoice(directory.Id, directory.DomainName.Value));
        }

        SelectedRestriction = RestrictionChoices[0];

        if (folderId is { } id)
        {
            var folder = await _folders.GetByIdAsync(id, cancellationToken).ConfigureAwait(true);

            if (folder is not null)
            {
                FolderName = folder.DisplayName;
                SelectedRestriction = RestrictionChoices
                    .FirstOrDefault(c => c.Id == folder.RestrictedToDomainDirectoryId)
                    ?? RestrictionChoices[0];
            }
        }
    }

    /// <summary>Grava a criação ou a renomeação, e o vínculo de restrição.</summary>
    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (AccountId is not { } accountId)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            Guid targetFolderId;

            if (FolderId is { } existing)
            {
                var renamed = await _manage.RenameAsync(existing, FolderName, cancellationToken)
                    .ConfigureAwait(true);

                if (!renamed.Succeeded)
                {
                    StatusMessage = renamed.ErrorMessage ?? string.Empty;
                    return;
                }

                targetFolderId = existing;
            }
            else
            {
                var created = await _manage.CreateAsync(accountId, FolderName, ParentFolderId, cancellationToken)
                    .ConfigureAwait(true);

                if (!created.Succeeded)
                {
                    StatusMessage = created.ErrorMessage ?? string.Empty;
                    return;
                }

                targetFolderId = created.FolderId!.Value;
                FolderId = targetFolderId;
            }

            try
            {
                await _restriction.HandleAsync(
                    new SetFolderRestrictionCommand(targetFolderId, SelectedRestriction?.Id),
                    cancellationToken).ConfigureAwait(true);
            }
            catch (InvalidFolderHierarchyException ex)
            {
                // A pasta já existe (ou foi renomeada); só o vínculo foi recusado. A
                // mensagem do domínio explica o porquê — uma pasta não responde a dois
                // Diretórios de Domínio.
                StatusMessage = ex.Message;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Mede o que a exclusão levaria junto.</summary>
    [RelayCommand]
    public async Task PrepareDeleteAsync(CancellationToken cancellationToken = default)
    {
        if (FolderId is not { } id)
        {
            return;
        }

        PendingDeleteImpact = await _manage.AnalyzeDeleteAsync(id, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Executa a exclusão já confirmada.</summary>
    [RelayCommand]
    public async Task<bool> ConfirmDeleteAsync(CancellationToken cancellationToken = default)
    {
        if (FolderId is not { } id)
        {
            return false;
        }

        if (PendingDeleteImpact is null)
        {
            StatusMessage = "Verifique o que será excluído antes de confirmar.";
            return false;
        }

        IsBusy = true;

        try
        {
            var result = await _manage.DeleteAsync(id, confirmed: true, cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                StatusMessage = result.ErrorMessage ?? string.Empty;
                return false;
            }

            PendingDeleteImpact = null;
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Alterna o favorito de uma pasta, direto do menu de contexto.</summary>
    public async Task<bool> ToggleFavoriteAsync(
        Guid folderId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        var result = await _manage.SetFavoriteAsync(folderId, isFavorite, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            StatusMessage = result.ErrorMessage ?? string.Empty;
        }

        return result.Succeeded;
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnPendingDeleteImpactChanged(DeleteFolderImpact? value)
    {
        OnPropertyChanged(nameof(HasPendingDelete));
        OnPropertyChanged(nameof(DeleteSummary));
    }
}
