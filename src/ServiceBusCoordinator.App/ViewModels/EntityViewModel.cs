using System.Collections.ObjectModel;
using System.Text;
using Azure.Messaging.ServiceBus;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceBusCoordinator.App.Services;
using ServiceBusCoordinator.Core;
using ServiceBusCoordinator.Core.Explorer;
using ServiceBusCoordinator.Core.Messages;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.App.ViewModels;

public sealed record SourceOption(MessageSource Value, string Label)
{
    public override string ToString() => Label;
}

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
        SelectedSource = SourceOptions[0];
        SelectedTabIndex = Entity.HasMessages ? 0 : 1;
        _ready = true;
    }

    private readonly bool _ready;

    public EntityInfo Entity => _node.Entity;

    public string Title => Entity.Kind == EntityKind.Subscription ? $"{Entity.TopicName} / {Entity.Name}" : Entity.Name;

    public string KindLabel => Entity.Kind + (Entity.IsManual ? " (added manually)" : "") + (Entity.RequiresSession ? " · sessions" : "");

    public bool HasMessages => Entity.HasMessages;

    public string KindName => Entity.Kind.ToString();

    public Avalonia.Media.Geometry Icon => Entity.Kind switch
    {
        EntityKind.Queue => Icons.Queue,
        EntityKind.Topic => Icons.Topic,
        _ => Icons.Subscription,
    };

    public bool IsQueue => Entity.Kind == EntityKind.Queue;

    public bool IsTopic => Entity.Kind == EntityKind.Topic;

    public bool IsSubscription => Entity.Kind == EntityKind.Subscription;

    public bool RequiresSession => Entity.RequiresSession;

    public bool IsManual => Entity.IsManual;

    /// <summary>Path shown under the title, e.g. <c>events/subscriptions/audit</c>.</summary>
    public string PathText => _node.Namespace.Host is { } host ? $"{host} / {Entity.Path}" : Entity.Path;

    public bool HasCounts => HasMessages && Entity.Counts is not null;

    public string ActiveCount => Entity.Counts?.Active.ToString("N0") ?? "—";

    public string DeadLetterCount => Entity.Counts?.DeadLetter.ToString("N0") ?? "—";

    public bool HasDeadLetters => Entity.Counts?.DeadLetter > 0;

    public string ScheduledCount => Entity.Counts?.Scheduled.ToString("N0") ?? "—";

    public string TransferDeadLetterCount => Entity.Counts?.TransferDeadLetter.ToString("N0") ?? "—";

    // ----------------------------------------------------------------- Properties tab

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailGroups), nameof(Rules), nameof(HasRules))]
    public partial EntityDetails? Details { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingDetails { get; set; }

    [ObservableProperty]
    public partial string? DetailsMessage { get; set; }

    public IReadOnlyList<DetailGroup> DetailGroups => Details?.Groups ?? [];

    public IReadOnlyList<RuleDetails> Rules => Details?.Rules ?? [];

    public bool HasRules => IsSubscription && Details is not null;

    private async Task LoadDetailsAsync()
    {
        IsLoadingDetails = true;
        DetailsMessage = null;
        try
        {
            Details = await Connection.GetDetailsAsync(Entity);
            if (Details is null)
            {
                DetailsMessage = "Properties need a connection string with Manage rights.";
            }
        }
        catch (Exception ex)
        {
            DetailsMessage = "Could not load properties: " + SecretScrubber.Scrub(ex.Message);
        }
        finally
        {
            IsLoadingDetails = false;
        }
    }

    public string CountsText => Entity.Counts is { } c
        ? $"Active {c.Active:N0}   ·   Dead-letter {c.DeadLetter:N0}   ·   Transfer DLQ {c.TransferDeadLetter:N0}"
            + (Entity.Kind == EntityKind.Queue ? $"   ·   Scheduled {c.Scheduled:N0}" : "")
        : "Counts unavailable (no Manage rights)";

    public IReadOnlyList<SourceOption> SourceOptions { get; } =
    [
        new(MessageSource.Active, "Active"),
        new(MessageSource.DeadLetter, "Dead-letter"),
        new(MessageSource.TransferDeadLetter, "Transfer dead-letter"),
    ];

    /// <summary>Messages shown (after the filter).</summary>
    public ObservableCollection<MessageRowViewModel> Messages { get; } = [];

    /// <summary>Every message loaded so far, before filtering.</summary>
    private readonly List<MessageRowViewModel> _loaded = [];

    public ObservableCollection<SessionSummary> Sessions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Source), nameof(SubQueueLabel), nameof(IsSessionMode), nameof(IsDeadLetterSource), nameof(IsActiveSource), nameof(CanCancelScheduledHere))]
    [NotifyCanExecuteChangedFor(nameof(ResubmitSelectedCommand), nameof(ResubmitAllCommand), nameof(CancelScheduledCommand))]
    public partial SourceOption SelectedSource { get; set; }

    public MessageSource Source => SelectedSource.Value;

    public string SubQueueLabel => Source.DisplayName();

    public bool IsDeadLetterSource => Source.IsDeadLetter();

    public bool IsActiveSource => Source == MessageSource.Active;

    /// <summary>Active messages of a session-enabled entity are reached one session at a time.</summary>
    public bool IsSessionMode => Entity.RequiresSession && Source == MessageSource.Active;

    public bool CanCancelScheduledHere => IsActiveSource && Entity.Kind == EntityKind.Queue;

    [ObservableProperty]
    public partial string? SessionId { get; set; }

    [ObservableProperty]
    public partial SessionSummary? SelectedSession { get; set; }

    [ObservableProperty]
    public partial string? SessionStateText { get; set; }

    [ObservableProperty]
    public partial MessageRowViewModel? SelectedMessage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(RefreshCommand), nameof(LoadMoreCommand), nameof(PurgeCommand), nameof(ResubmitSelectedCommand), nameof(ResubmitAllCommand),
        nameof(CancelScheduledCommand), nameof(DeleteDeferredCommand), nameof(FindSessionsCommand), nameof(OpenSessionCommand),
        nameof(NextSessionCommand), nameof(ClearSessionStateCommand), nameof(ExportCommand), nameof(ExportAllCommand),
        nameof(SendMessageCommand), nameof(ResendCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Filters the loaded messages (client-side). See <see cref="MessageFilter"/> for the syntax.</summary>
    [ObservableProperty]
    public partial string? FilterText { get; set; }

    [ObservableProperty]
    public partial string? FilterSummary { get; set; }

    partial void OnFilterTextChanged(string? value) => ApplyFilter();

    partial void OnSelectedMessageChanged(MessageRowViewModel? value) => ResendCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool CanLoadMore { get; set; }

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    private bool NotBusy => !IsBusy;

    private bool CanLoadMoreNow => !IsBusy && CanLoadMore;

    private bool CanResubmit => !IsBusy && IsDeadLetterSource;

    private bool CanCancelScheduled => !IsBusy && CanCancelScheduledHere;

    private bool HasSession => !IsBusy && !string.IsNullOrWhiteSpace(SessionId);

    private NamespaceConnection Connection =>
        _node.Namespace.Connection ?? throw new InvalidOperationException("Namespace is not connected.");

    partial void OnSelectedSourceChanged(SourceOption value)
    {
        if (!_ready || IsBusy)
        {
            return;
        }

        _ = RefreshAsync();
    }

    partial void OnSessionIdChanged(string? value)
    {
        OpenSessionCommand.NotifyCanExecuteChanged();
        ClearSessionStateCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSessionChanged(SessionSummary? value)
    {
        if (value is not null && value.SessionId != SessionId)
        {
            SessionId = value.SessionId;
            _ = RefreshAsync();
        }
    }

    // ----------------------------------------------------------------- Loading

    [RelayCommand(CanExecute = nameof(NotBusy))]
    public async Task RefreshAsync()
    {
        var details = LoadDetailsAsync();
        if (!HasMessages)
        {
            await details;
            return;
        }

        _loaded.Clear();
        Messages.Clear();
        SelectedMessage = null;
        CanLoadMore = false;
        await RefreshCountsAsync();
        await LoadPageAsync(fromSequenceNumber: null);
        await details;
    }

    [RelayCommand(CanExecute = nameof(CanLoadMoreNow))]
    private Task LoadMoreAsync() => LoadPageAsync(_loaded.Count == 0 ? null : _loaded[^1].SequenceNumber + 1);

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

    private Task LoadPageAsync(long? fromSequenceNumber) => RunAsync("Loading", async ct =>
    {
        var result = await Connection.Messages.PeekAsync(
            Entity, Source, fromSequenceNumber, PageSize, IsSessionMode ? SessionId : null, ct);

        if (IsSessionMode && result.SessionId is { } opened && opened != SessionId)
        {
            SessionId = opened;
        }

        foreach (var m in result.Messages)
        {
            var row = new MessageRowViewModel(m);
            _loaded.Add(row);
            if (MessageFilter.Matches(m, FilterText))
            {
                Messages.Add(row);
            }
        }

        CanLoadMore = result.Messages.Count > 0;
        StatusText = _loaded.Count switch
        {
            0 when IsSessionMode && result.SessionId is null => "No session with available messages was found.",
            0 => $"No {SubQueueLabel}.",
            _ => $"Loaded {_loaded.Count:N0} peeked message(s)" + (IsSessionMode ? $" in session '{SessionId}'." : "."),
        };
        UpdateFilterSummary();
        SelectedMessage ??= Messages.FirstOrDefault();
    });

    private void ApplyFilter()
    {
        var selected = SelectedMessage;
        Messages.Clear();
        foreach (var row in _loaded.Where(r => MessageFilter.Matches(r.View, FilterText)))
        {
            Messages.Add(row);
        }

        SelectedMessage = selected is not null && Messages.Contains(selected) ? selected : Messages.FirstOrDefault();
        UpdateFilterSummary();
    }

    private void UpdateFilterSummary() =>
        FilterSummary = string.IsNullOrWhiteSpace(FilterText) ? null : $"{Messages.Count:N0} of {_loaded.Count:N0} loaded match";

    // ----------------------------------------------------------------- Export

    private const int ExportAllLimit = 10_000;

    /// <summary>Exports the ticked messages, or every message currently shown when none are ticked.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ExportAsync()
    {
        var ticked = _loaded.Where(m => m.IsChecked).Select(m => m.View).ToList();
        var messages = ticked.Count > 0 ? ticked : Messages.Select(m => m.View).ToList();
        if (messages.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Export", "There are no messages to export.");
            return;
        }

        await ExportToFileAsync(messages, ticked.Count > 0 ? "selected" : "shown");
    }

    /// <summary>Peeks the whole sub-queue (or the open session) and exports it.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ExportAllAsync()
    {
        IReadOnlyList<MessageView>? all = null;
        await RunAsync("Reading messages", async ct =>
        {
            all = await Connection.Messages.PeekAllAsync(
                Entity, Source, IsSessionMode ? SessionId : null, ExportAllLimit, Progress("Reading", "read"), ct);
        });

        if (all is null)
        {
            return;
        }

        if (all.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Export", $"The {SubQueueLabel} is empty.");
            return;
        }

        await ExportToFileAsync(all, all.Count >= ExportAllLimit ? $"first {ExportAllLimit:N0}" : "all");
    }

    private async Task ExportToFileAsync(IReadOnlyList<MessageView> messages, string scope)
    {
        var sourcePath = Entity.Path + Source.PathSuffix();
        var safeName = string.Concat(sourcePath.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_')).Trim('_');
        var stream = await _dialogs.SaveJsonFileAsync("Export messages", $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        if (stream is null)
        {
            return;
        }

        await RunAsync("Exporting", async ct =>
        {
            await using (stream)
            {
                await MessageExporter.ExportAsync(stream, sourcePath, messages, DateTimeOffset.UtcNow, ct);
            }

            StatusText = $"Exported {messages.Count:N0} message(s) ({scope}).";
        });
    }

    // ----------------------------------------------------------------- Send

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task SendMessageAsync() =>
        ComposeAndSendAsync("Send message", new OutgoingMessage("", "application/json", SessionId: IsSessionMode ? SessionId : null));

    [RelayCommand(CanExecute = nameof(CanResend))]
    private Task ResendAsync() =>
        ComposeAndSendAsync("Edit & resend message", OutgoingMessage.From(SelectedMessage!.View));

    private bool CanResend => !IsBusy && SelectedMessage is not null;

    private async Task ComposeAndSendAsync(string title, OutgoingMessage prefill)
    {
        var composed = await _dialogs.ComposeMessageAsync(
            title, Entity.DefaultResubmitTarget ?? Entity.Path, _node.Namespace.SendableEntities, prefill);
        if (composed is null)
        {
            return;
        }

        await RunAsync("Sending", async ct =>
        {
            var scheduled = await Connection.Messages.SendAsync(composed.TargetEntityPath, composed.Message, ct);
            StatusText = scheduled is { } seq
                ? $"Scheduled message #{seq} on '{composed.TargetEntityPath}' for {composed.Message.ScheduledEnqueueTime!.Value.ToLocalTime():g}."
                : $"Sent message to '{composed.TargetEntityPath}'.";
        });
        await RefreshCountsAsync();
    }

    private async Task RefreshCountsAsync()
    {
        try
        {
            _node.Entity = await Connection.RefreshAsync(_node.Entity);
            OnPropertyChanged(nameof(Entity));
            OnPropertyChanged(nameof(CountsText));
            OnPropertyChanged(nameof(HasCounts));
            OnPropertyChanged(nameof(ActiveCount));
            OnPropertyChanged(nameof(DeadLetterCount));
            OnPropertyChanged(nameof(HasDeadLetters));
            OnPropertyChanged(nameof(ScheduledCount));
            OnPropertyChanged(nameof(TransferDeadLetterCount));
        }
        catch (Exception)
        {
            // Counts are best-effort.
        }
    }

    // ----------------------------------------------------------------- Sessions

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task FindSessionsAsync()
    {
        await RunAsync("Finding sessions", async ct =>
        {
            var sessions = await Connection.Messages.ListSessionsAsync(Entity, ct: ct);
            Sessions.Clear();
            foreach (var s in sessions)
            {
                Sessions.Add(s);
            }

            StatusText = sessions.Count == 0
                ? "No sessions with available messages."
                : $"Found {sessions.Count:N0} session(s) with available messages.";
        });
    }

    [RelayCommand(CanExecute = nameof(HasSession))]
    private async Task OpenSessionAsync()
    {
        await RefreshAsync();
        await LoadSessionStateAsync();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task NextSessionAsync()
    {
        SessionId = null;
        SessionStateText = null;
        await RefreshAsync();
        await LoadSessionStateAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSession))]
    private async Task ClearSessionStateAsync()
    {
        var sessionId = SessionId!;
        if (!await _dialogs.ConfirmAsync("Clear session state", $"Clear the stored state of session '{sessionId}'?", "Clear"))
        {
            return;
        }

        await RunAsync("Clearing session state", async ct =>
        {
            await Connection.Messages.ClearSessionStateAsync(Entity, sessionId, ct);
            SessionStateText = "(no state)";
            StatusText = $"Cleared state of session '{sessionId}'.";
        });
    }

    private async Task LoadSessionStateAsync()
    {
        if (string.IsNullOrWhiteSpace(SessionId))
        {
            SessionStateText = null;
            return;
        }

        try
        {
            var state = await Connection.Messages.GetSessionStateAsync(Entity, SessionId);
            SessionStateText = state is null || state.Length == 0 ? "(no state)" : BodyFormatter.Format(state).Text;
        }
        catch (Exception ex)
        {
            SessionStateText = "State unavailable: " + SecretScrubber.Scrub(ex.Message);
        }
    }

    // ----------------------------------------------------------------- Purge

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task PurgeAsync()
    {
        var count = Entity.Counts is { } c
            ? Source switch
            {
                MessageSource.DeadLetter => c.DeadLetter,
                MessageSource.TransferDeadLetter => c.TransferDeadLetter,
                _ => c.Active + (Entity.Kind == EntityKind.Queue ? c.Scheduled : 0),
            }
            : (long?)null;
        var countText = count is { } n ? $"about {n:N0} message(s)" : "all messages";
        var extra = Source == MessageSource.Active
            ? (Entity.Kind == EntityKind.Queue ? " Deferred and scheduled messages are removed too." : " Deferred messages are removed too.")
              + (Entity.RequiresSession ? " Sessions are purged one at a time." : "")
            : "";
        var message = $"Permanently delete {countText} from the {SubQueueLabel} of '{Entity.Path}'?{extra}\n\nThis cannot be undone.";
        var requireTyping = count is null or > TypeToConfirmThreshold ? Entity.Name : null;

        if (!await _dialogs.ConfirmAsync("Purge messages", message, "Purge", requireTyping))
        {
            return;
        }

        await RunAsync("Purging", async ct =>
        {
            var deleted = await Connection.Messages.PurgeAsync(Entity, Source, Progress("Purging", "deleted"), ct);
            StatusText = $"Purged {deleted:N0} message(s).";
        });
        await RefreshAsync();
    }

    // ----------------------------------------------------------------- Scheduled & deferred

    [RelayCommand(CanExecute = nameof(CanCancelScheduled))]
    private async Task CancelScheduledAsync()
    {
        var selected = CheckedWithState(ServiceBusMessageState.Scheduled);
        if (selected.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Cancel scheduled", "Tick one or more messages whose state is 'Scheduled'.");
            return;
        }

        if (!await _dialogs.ConfirmAsync("Cancel scheduled messages", $"Cancel {selected.Count:N0} scheduled message(s)? They will never be delivered.", "Cancel messages"))
        {
            return;
        }

        await RunAsync("Cancelling", async ct =>
        {
            var n = await Connection.Messages.CancelScheduledAsync(Entity, selected, ct);
            StatusText = $"Cancelled {n:N0} scheduled message(s).";
        });
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task DeleteDeferredAsync()
    {
        var selected = CheckedWithState(ServiceBusMessageState.Deferred);
        if (selected.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Delete deferred", "Tick one or more messages whose state is 'Deferred'.");
            return;
        }

        if (!await _dialogs.ConfirmAsync("Delete deferred messages", $"Permanently delete {selected.Count:N0} deferred message(s)?", "Delete"))
        {
            return;
        }

        await RunAsync("Deleting deferred", async ct =>
        {
            var n = await Connection.Messages.DeleteDeferredAsync(Entity, Source, selected, IsSessionMode ? SessionId : null, ct);
            StatusText = $"Deleted {n:N0} deferred message(s).";
        });
        await RefreshAsync();
    }

    private List<long> CheckedWithState(ServiceBusMessageState state) =>
        _loaded.Where(m => m.IsChecked && m.View.State == state).Select(m => m.SequenceNumber).ToList();

    // ----------------------------------------------------------------- Resubmit

    [RelayCommand(CanExecute = nameof(CanResubmit))]
    private async Task ResubmitSelectedAsync()
    {
        var selected = _loaded.Where(m => m.IsChecked).Select(m => m.SequenceNumber).ToHashSet();
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
        var max = Entity.Counts is { } c ? (Source == MessageSource.DeadLetter ? c.DeadLetter : c.TransferDeadLetter) : (long?)null;
        await ResubmitAsync(max is { } n ? $"all {n:N0} message(s)" : "all messages", null, max);
    }

    private async Task ResubmitAsync(string scope, IReadOnlySet<long>? selection, long? max)
    {
        var source = Source;
        var choice = await _dialogs.ChooseResubmitAsync(Entity, source, scope, _node.Namespace.SendableEntities);
        if (choice is null)
        {
            return;
        }

        await RunAsync("Resubmitting", async ct =>
        {
            var options = new ResubmitOptions(
                choice.TargetEntityPath, choice.StripDeadLetterProperties, choice.StampResubmitHeaders, selection, max);
            var result = await Connection.Messages.ResubmitAsync(Entity, source, options, Progress("Resubmitting", "moved"), ct);

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

    // ----------------------------------------------------------------- Infrastructure

    private Progress<OperationProgress> Progress(string verb, string noun) => new(p =>
    {
        var text = new StringBuilder($"{verb}… {p.Processed:N0} {noun}");
        if (p.Note is { } note)
        {
            text.Append(" (").Append(note).Append(')');
        }

        StatusText = text.ToString();
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
