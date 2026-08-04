using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Domains;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>
/// Formulário de criação e edição de um Diretório de Domínio.
/// </summary>
/// <remarks>
/// A validação do domínio é delegada a <see cref="EmailDomain.TryParse"/>, a mesma que o
/// caso de uso aplica. O formulário a chama enquanto o usuário digita apenas para mostrar o
/// erro cedo — não para decidir nada: quem recusa é a camada de Aplicação.
/// </remarks>
public sealed partial class DomainDirectoryEditorViewModel : ObservableObject
{
    private readonly IDomainDirectoryRepository _directories;
    private readonly CreateDomainDirectoryHandler _create;
    private readonly UpdateDomainDirectoryHandler _update;
    private readonly RemoveDomainDirectoryHandler _remove;

    public DomainDirectoryEditorViewModel(
        IDomainDirectoryRepository directories,
        CreateDomainDirectoryHandler create,
        UpdateDomainDirectoryHandler update,
        RemoveDomainDirectoryHandler remove)
    {
        _directories = directories;
        _create = create;
        _update = update;
        _remove = remove;
    }

    /// <summary>Diretório em edição, ou <see langword="null"/> quando é criação.</summary>
    [ObservableProperty]
    private Guid? _domainDirectoryId;

    /// <summary>Domínio representado.</summary>
    [ObservableProperty]
    private string _domainName = string.Empty;

    /// <summary>
    /// Descrição livre.
    /// </summary>
    /// <remarks>
    /// Vazia, não nula: a caixa de texto do WinUI recusa <see langword="null"/>, e o caso de
    /// uso já converte vazio em ausente antes de gravar.
    /// </remarks>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>Quais participantes contam na avaliação de pertencimento.</summary>
    [ObservableProperty]
    private DomainValidationMode _validationMode = DomainValidationMode.AnyParticipant;

    /// <summary>O que fazer com mensagem incompatível em pasta restrita.</summary>
    [ObservableProperty]
    private InvalidEmailAction _invalidEmailAction = InvalidEmailAction.Block;

    /// <summary>Se subdomínios são aceitos.</summary>
    [ObservableProperty]
    private bool _allowSubdomains;

    /// <summary>Se o diretório aparece entre os favoritos.</summary>
    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>Se o diretório está ativo.</summary>
    [ObservableProperty]
    private bool _isActive = true;

    /// <summary>Mensagem de erro ou aviso.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Se há operação em andamento.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Impacto medido da remoção, exibido no pedido de confirmação.</summary>
    [ObservableProperty]
    private RemoveDomainDirectoryImpact? _pendingRemovalImpact;

    /// <summary>Domínios adicionais aceitos.</summary>
    public ObservableCollection<string> Aliases { get; } = [];

    /// <summary>Modos de validação oferecidos na lista.</summary>
    public IReadOnlyList<ValidationModeOption> ValidationModes => SelectionOptions.ValidationModes;

    /// <summary>Ações oferecidas para mensagem incompatível.</summary>
    public IReadOnlyList<InvalidEmailActionOption> InvalidEmailActions => SelectionOptions.InvalidEmailActions;

    /// <summary>Modo de validação selecionado na lista.</summary>
    public ValidationModeOption? SelectedValidationMode
    {
        get => SelectionOptions.ValidationModes.FirstOrDefault(o => o.Value == ValidationMode);
        set
        {
            if (value is not null)
            {
                ValidationMode = value.Value;
            }
        }
    }

    /// <summary>Ação selecionada para mensagem incompatível.</summary>
    public InvalidEmailActionOption? SelectedInvalidEmailAction
    {
        get => SelectionOptions.InvalidEmailActions.FirstOrDefault(o => o.Value == InvalidEmailAction);
        set
        {
            if (value is not null)
            {
                InvalidEmailAction = value.Value;
            }
        }
    }

    /// <summary>Se há mensagem a exibir na faixa de aviso.</summary>
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Se há um impacto medido aguardando confirmação.</summary>
    public bool HasPendingRemoval => PendingRemovalImpact is not null;

    /// <summary>Resumo do que a remoção levaria junto, para exibição.</summary>
    public string RemovalSummary => PendingRemovalImpact is { } impact
        ? $"Remover '{impact.DomainName}' apagará {impact.AccountCount} conta(s), " +
          $"{impact.FolderCount} pasta(s) e {impact.MessageCount} mensagem(ns) deste computador."
        : string.Empty;

    /// <summary>Se o formulário está editando um diretório já existente.</summary>
    public bool IsEditing => DomainDirectoryId is not null;

    /// <summary>
    /// Erro de validação do domínio digitado, exibido enquanto se digita.
    /// </summary>
    /// <remarks>
    /// Vazio quando não há erro — e não nulo — porque o destino é um <c>TextBlock</c>, que
    /// recusa <see langword="null"/> em tempo de execução.
    /// </remarks>
    public string DomainNameError
        => string.IsNullOrWhiteSpace(DomainName) || EmailDomain.TryParse(DomainName, out _, out var error)
            ? string.Empty
            : error!;

    /// <summary>Se o formulário está em condição de ser gravado.</summary>
    public bool CanSave => !IsBusy && !string.IsNullOrWhiteSpace(DomainName) && DomainNameError.Length == 0;

    /// <summary>Carrega um diretório existente para edição.</summary>
    public async Task LoadAsync(Guid domainDirectoryId, CancellationToken cancellationToken = default)
    {
        var directory = await _directories.GetByIdAsync(domainDirectoryId, cancellationToken).ConfigureAwait(true);

        if (directory is null)
        {
            StatusMessage = "O Diretório de Domínio informado não existe.";
            return;
        }

        DomainDirectoryId = directory.Id;
        DomainName = directory.DomainName.Value;
        Description = directory.Description ?? string.Empty;
        ValidationMode = directory.ValidationMode;
        InvalidEmailAction = directory.InvalidEmailAction;
        AllowSubdomains = directory.AllowSubdomains;
        IsFavorite = directory.IsFavorite;
        IsActive = directory.IsActive;

        Aliases.Clear();

        foreach (var alias in directory.Aliases)
        {
            Aliases.Add(alias.DomainName.Value);
        }
    }

    /// <summary>Acrescenta um domínio adicional à lista, validando-o antes.</summary>
    public void AddAlias(string alias)
    {
        if (!EmailDomain.TryParse(alias, out var parsed, out var error))
        {
            StatusMessage = error;
            return;
        }

        if (Aliases.Contains(parsed.Value, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Aliases.Add(parsed.Value);
        StatusMessage = null;
    }

    /// <summary>Remove um domínio adicional da lista.</summary>
    public void RemoveAlias(string alias) => Aliases.Remove(alias);

    /// <summary>Grava a criação ou a alteração.</summary>
    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = null;
        IsBusy = true;

        try
        {
            if (DomainDirectoryId is { } existingId)
            {
                var updated = await _update.HandleAsync(
                    new UpdateDomainDirectoryCommand
                    {
                        DomainDirectoryId = existingId,
                        Description = Description,
                        IsFavorite = IsFavorite,
                        ValidationMode = ValidationMode,
                        InvalidEmailAction = InvalidEmailAction,
                        AllowSubdomains = AllowSubdomains,
                        IsActive = IsActive,
                        Aliases = [.. Aliases],
                    },
                    cancellationToken).ConfigureAwait(true);

                StatusMessage = updated.Succeeded ? null : updated.ErrorMessage;
                return;
            }

            var created = await _create.HandleAsync(
                new CreateDomainDirectoryCommand
                {
                    DomainName = DomainName,
                    Description = Description,
                    ValidationMode = ValidationMode,
                    InvalidEmailAction = InvalidEmailAction,
                    AllowSubdomains = AllowSubdomains,
                    Aliases = [.. Aliases],
                },
                cancellationToken).ConfigureAwait(true);

            if (!created.Succeeded)
            {
                StatusMessage = created.ErrorMessage;
                return;
            }

            DomainDirectoryId = created.DomainDirectoryId;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Mede o que a remoção levaria junto e guarda o resultado para a tela de confirmação.
    /// </summary>
    [RelayCommand]
    public async Task PrepareRemovalAsync(CancellationToken cancellationToken = default)
    {
        if (DomainDirectoryId is not { } id)
        {
            return;
        }

        PendingRemovalImpact = await _remove.AnalyzeAsync(id, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Executa a remoção já confirmada.
    /// </summary>
    /// <remarks>
    /// Exige que <see cref="PrepareRemovalAsync"/> tenha rodado: confirmar sem ter visto o
    /// impacto é justamente o que a especificação proíbe, e deixar o caminho aberto aqui
    /// permitiria que uma tela futura o usasse por engano.
    /// </remarks>
    [RelayCommand]
    public async Task<bool> ConfirmRemovalAsync(CancellationToken cancellationToken = default)
    {
        if (DomainDirectoryId is not { } id)
        {
            return false;
        }

        if (PendingRemovalImpact is null)
        {
            StatusMessage = "Verifique o que será removido antes de confirmar.";
            return false;
        }

        IsBusy = true;

        try
        {
            var result = await _remove.HandleAsync(id, confirmed: true, cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                StatusMessage = result.ErrorMessage;
                return false;
            }

            PendingRemovalImpact = null;
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnDomainNameChanged(string value)
    {
        OnPropertyChanged(nameof(DomainNameError));
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnDomainDirectoryIdChanged(Guid? value) => OnPropertyChanged(nameof(IsEditing));

    partial void OnPendingRemovalImpactChanged(RemoveDomainDirectoryImpact? value)
    {
        OnPropertyChanged(nameof(HasPendingRemoval));
        OnPropertyChanged(nameof(RemovalSummary));
    }

    partial void OnValidationModeChanged(DomainValidationMode value)
        => OnPropertyChanged(nameof(SelectedValidationMode));

    partial void OnInvalidEmailActionChanged(InvalidEmailAction value)
        => OnPropertyChanged(nameof(SelectedInvalidEmailAction));
}
