using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceBusCoordinator.App.Services;
using ServiceBusCoordinator.Core;
using ServiceBusCoordinator.Core.Messages;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.App.ViewModels;

/// <summary>Right-hand pane: messages of the selected queue or subscription.</summary>
public sealed partial class EntityViewModel : ObservableObject
{
    private const int PageSize = 50;
    private const int TypeToConfirmThreshold = 100;

    private readonly EntityNodeViewModel _node;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public EntityViewModel(EntityNodeViewModel node, IDialogService dialogs)
    {
        _node = node;
        _dialogs = dialogs;
    }

    public EntityInfo Entity => _node.Entity;

    public string Title => Entity.Kind == EntityKind.Subscription ? $"{Entity.TopicName} / {Entity.Name}" : Entity.Name;

    public string KindLabel => Entity.Kind + (Entity.IsManual ? " (added manually)" : "") + (Entity.RequiresSession ? " · sessions" : "");

    public string CountsText => Entity.Counts is { } c
        ? $"Active {c.Active:N0}   ·   Dead-letter {c.DeadLetter:N0}   ·   Scheduled {c.Scheduled:N0}   ·   Transfer DLQ {c.TransferDeadLetter:N0}"
        : "Counts unavailable (no Manage rights)";

    public ObservableCollection<MessageRowViewModel> Messages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubQueueLabel))]
    public partial bool ShowDeadLetter { get; set; }

    public string SubQueueLabel => ShowDeadLetter ? "dead-letter queue" : "active messages";

    [ObservableProperty]
    public partial MessageRowViewModel? SelectedMessage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(LoadMoreCommand), nameof(PurgeCommand), nameof(ResubmitSelectedCommand), nameof(ResubmitAllCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool CanLoadMore { get; set; }

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    private bool NotBusy => !IsBusy;

    private bool CanLoadMoreNow => !IsBusy && CanLoadMore;

    private bool CanResubmit => !IsBusy && ShowDeadLetter;

    partial void OnShowDeadLetterChanged(bool value)
    {
        ResubmitSelectedCommand.NotifyCanExecuteChanged();
        ResubmitAllCommand.NotifyCanExecuteChanged();
        _ = RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    public async Task RefreshAsync()
    {
        Messages.Clear();
        SelectedMessage = null;
        CanLoadMore = false;
        await RefreshCountsAsync();
        await LoadPageAsync(fromSequenceNumber: null);
    }

    [RelayCommand(CanExecute = nameof(CanLoadMoreNow))]
    private Task LoadMoreAsync() => LoadPageAsync(Messages.Count == 0 ? null : Messages[^1].SequenceNumber + 1);

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void CheckAll(bool? value)
    {
        foreach (var m in Messages)
        {
            m.IsChecked = value ?? true;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task PurgeAsync()
    {
        var count = Entity.Counts is { } c ? (ShowDeadLetter ? c.DeadLetter : c.Active) : (long?)null;
        var countText = count is { } n ? $"{n:N0} message(s)" : "all messages";
        var message = $"Permanently delete {countText} from the {SubQueueLabel} of '{Entity.Path}'?\n\nThis cannot be undone.";
        var requireTyping = count is null or > TypeToConfirmThreshold ? Entity.Name : null;

        if (!await _dialogs.ConfirmAsync("Purge messages", message, "Purge", requireTyping))
        {
            return;
        }

        await RunAsync("Purging", async ct =>
        {
            var deleted = await Connection.Messages.PurgeAsync(
                Entity, ShowDeadLetter, new Progress<long>(d => StatusText = $"Purging… {d:N0} deleted"), ct);
            StatusText = $"Purged {deleted:N0} message(s).";
        });
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanResubmit))]
    private async Task ResubmitSelectedAsync()
    {
        var selected = Messages.Where(m => m.IsChecked).Select(m => m.SequenceNumber).ToHashSet();
        if (selected.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Resubmit", "Tick the messages to resubmit first.");
            return;
        }

        await ResubmitAsync($"{selected.Count:N0} selected message(s)", selected, null);
    }

    [RelayCommand(CanExecute = nameof(CanResubmit))]
    private async Task ResubmitAllAsync()
    {
        await RefreshCountsAsync();
        var max = Entity.Counts?.DeadLetter;
        await ResubmitAsync(max is { } n ? $"all {n:N0} dead-lettered message(s)" : "all dead-lettered messages", null, max);
    }

    private async Task ResubmitAsync(string scope, IReadOnlySet<long>? selection, long? max)
    {
        var choice = await _dialogs.ChooseResubmitAsync(Entity, scope, _node.Namespace.SendableEntities);
        if (choice is null)
        {
            return;
        }

        await RunAsync("Resubmitting", async ct =>
        {
            var options = new ResubmitOptions(
                choice.TargetEntityPath, choice.StripDeadLetterProperties, choice.StampResubmitHeaders, selection, max);
            var result = await Connection.Messages.ResubmitAsync(
                Entity, options, new Progress<int>(d => StatusText = $"Resubmitting… {d:N0} moved"), ct);

            StatusText = $"Moved {result.Resubmitted:N0} message(s) to '{choice.TargetEntityPath}'"
                + (result.UsedTransactions ? " (transactional)." : " (send-then-complete).")
                + (result.Failed > 0 ? $" {result.Failed:N0} failed." : "");

            if (result.Errors.Count > 0)
            {
                await _dialogs.ShowMessageAsync("Some messages were not resubmitted", string.Join("\n", result.Errors.Take(20)));
            }
        });
        await RefreshAsync();
    }

    private Core.Explorer.NamespaceConnection Connection =>
        _node.Namespace.Connection ?? throw new InvalidOperationException("Namespace is not connected.");

    private async Task RefreshCountsAsync()
    {
        try
        {
            _node.Entity = await Connection.RefreshAsync(_node.Entity);
            OnPropertyChanged(nameof(Entity));
            OnPropertyChanged(nameof(CountsText));
        }
        catch (Exception)
        {
            // Counts are best-effort.
        }
    }

    private Task LoadPageAsync(long? fromSequenceNumber) => RunAsync("Loading", async ct =>
    {
        var page = await Connection.Messages.PeekAsync(Entity, ShowDeadLetter, fromSequenceNumber, PageSize, ct);
        foreach (var m in page)
        {
            Messages.Add(new MessageRowViewModel(m));
        }

        CanLoadMore = page.Count > 0;
        StatusText = Messages.Count == 0 ? $"No {SubQueueLabel}." : $"Showing {Messages.Count:N0} peeked message(s).";
        SelectedMessage ??= Messages.FirstOrDefault();
    });

    private async Task RunAsync(string verb, Func<CancellationToken, Task> work)
    {
        IsBusy = true;
        Error = null;
        StatusText = verb + "…";
        _cts = new CancellationTokenSource();
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = verb + " cancelled.";
        }
        catch (Exception ex)
        {
            Error = SecretScrubber.Scrub(ex.Message);
            StatusText = null;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }
}
