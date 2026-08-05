using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Presentation.ViewModels;
using Windows.System;

namespace Sintek.Mail.App.Dialogs;

/// <summary>Agenda: grade do período, resposta a convites e remarcação.</summary>
/// <remarks>
/// O code-behind traduz cliques em chamadas ao ViewModel e abre o navegador para a reunião
/// on-line. Quem decide se um compromisso pode ser movido é o domínio, e a explicação da
/// recusa chega pronta para exibir.
/// </remarks>
public sealed partial class CalendarDialog : ContentDialog
{
    public CalendarDialog(CalendarViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    /// <summary>ViewModel da agenda.</summary>
    public CalendarViewModel ViewModel { get; }

    /// <summary>Carrega o período atual antes de exibir.</summary>
    public Task InitializeAsync(Guid? accountId) => ViewModel.InitializeAsync(accountId);

    private async void OnDayViewClick(object sender, RoutedEventArgs e)
        => await ViewModel.SetViewModeAsync(CalendarViewMode.Day).ConfigureAwait(true);

    private async void OnWeekViewClick(object sender, RoutedEventArgs e)
        => await ViewModel.SetViewModeAsync(CalendarViewMode.Week).ConfigureAwait(true);

    private async void OnMonthViewClick(object sender, RoutedEventArgs e)
        => await ViewModel.SetViewModeAsync(CalendarViewMode.Month).ConfigureAwait(true);

    private async void OnAcceptClick(object sender, RoutedEventArgs e)
        => await RespondAsync(sender, AttendeeResponse.Accepted).ConfigureAwait(true);

    private async void OnDeclineClick(object sender, RoutedEventArgs e)
        => await RespondAsync(sender, AttendeeResponse.Declined).ConfigureAwait(true);

    private async Task RespondAsync(object sender, AttendeeResponse response)
    {
        if ((sender as FrameworkElement)?.Tag is CalendarOccurrenceItem item)
        {
            await ViewModel.RespondAsync(item.EventId, response).ConfigureAwait(true);
        }
    }

    private async void OnKeepLocalClick(object sender, RoutedEventArgs e)
        => await ResolveConflictAsync(sender, keepLocal: true).ConfigureAwait(true);

    private async void OnKeepServerClick(object sender, RoutedEventArgs e)
        => await ResolveConflictAsync(sender, keepLocal: false).ConfigureAwait(true);

    private async Task ResolveConflictAsync(object sender, bool keepLocal)
    {
        if ((sender as FrameworkElement)?.Tag is not CalendarConflictItem item)
        {
            return;
        }

        var command = keepLocal
            ? ViewModel.KeepLocalVersionAsync(item.EventId)
            : ViewModel.KeepServerVersionAsync(item.EventId);

        await command.ConfigureAwait(true);
    }

    private async void OnJoinMeetingClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CalendarOccurrenceItem item
            || !item.HasMeetingUrl)
        {
            return;
        }

        // Só HTTPS: o endereço veio de um convite escolhido por quem enviou a mensagem, e
        // um esquema arbitrário aqui viraria execução de programa por convite recebido.
        if (Uri.TryCreate(item.MeetingUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    /// <summary>
    /// Remarca o compromisso selecionado para a data e a hora escolhidas.
    /// </summary>
    /// <remarks>
    /// Data e hora vêm de controles separados porque o WinUI não tem um seletor único —
    /// mesma composição do envio agendado no compositor.
    /// </remarks>
    private async void OnRescheduleClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedOccurrence is not { } selected)
        {
            ViewModel.StatusMessage = "Escolha um compromisso na lista antes de remarcar.";
            return;
        }

        if (RescheduleDate.Date is not { } date)
        {
            ViewModel.StatusMessage = "Escolha a nova data.";
            return;
        }

        var newStart = new DateTimeOffset(date.Date, date.Offset).Add(RescheduleTime.Time);

        await ViewModel.MoveAsync(selected.EventId, newStart).ConfigureAwait(true);
    }

    /// <summary>Cria o diálogo com as dependências do contêiner.</summary>
    public static CalendarDialog Create(XamlRoot xamlRoot)
        => new(App.Services.GetRequiredService<CalendarViewModel>())
        {
            XamlRoot = xamlRoot,
        };
}
