using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ServiceBusCoordinator.Core.Connections;
using ServiceBusCoordinator.Core.Messages;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.App.Services;

/// <summary>Modal dialogs built in code. Each returns null / false when cancelled.</summary>
public sealed class DialogService(Window owner) : IDialogService
{
    public async Task<ConnectionProfile?> AddConnectionAsync()
    {
        var name = new TextBox { PlaceholderText = "Filled in from the connection string" };
        var connectionString = new TextBox
        {
            PlaceholderText = "Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=…;SharedAccessKey=…",
            PasswordChar = '•',
        };
        var reveal = new CheckBox { Content = "Show connection string" };
        reveal.IsCheckedChanged += (_, _) => connectionString.RevealPassword = reveal.IsChecked == true;
        var error = ErrorText();

        // Default the display name to the namespace name, but never overwrite a name the user typed.
        string? autoName = null;
        connectionString.TextChanged += (_, _) =>
        {
            error.IsVisible = false;
            if (!ConnectionStringInfo.TryParse(connectionString.Text, out var parsed, out _))
            {
                return;
            }

            var userEdited = !string.IsNullOrWhiteSpace(name.Text) && name.Text != autoName;
            if (!userEdited)
            {
                autoName = parsed!.NamespaceName;
                name.Text = autoName;
            }
        };

        ConnectionProfile? result = null;
        var dialog = BuildDialog("Add connection", 560, out var ok, out var body, "Add");
        body.Children.Add(Label("Connection string"));
        body.Children.Add(connectionString);
        body.Children.Add(reveal);
        body.Children.Add(Label("Display name"));
        body.Children.Add(name);
        body.Children.Add(Muted("Stored in your operating system's secure storage (DPAPI / Keychain / libsecret)."));
        body.Children.Add(error);

        ok.Click += (_, _) =>
        {
            if (!ConnectionStringInfo.TryParse(connectionString.Text, out var info, out var parseError))
            {
                error.Text = parseError;
                error.IsVisible = true;
                return;
            }

            result = new ConnectionProfile
            {
                Name = string.IsNullOrWhiteSpace(name.Text) ? info!.NamespaceName : name.Text.Trim(),
                ConnectionString = connectionString.Text!.Trim(),
            };
            dialog.Close();
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    public async Task<string?> PromptAsync(string title, string message, string watermark)
    {
        var input = new TextBox { PlaceholderText = watermark };
        string? result = null;
        var dialog = BuildDialog(title, 480, out var ok, out var body, "OK");
        body.Children.Add(Text(message));
        body.Children.Add(input);
        ok.Click += (_, _) =>
        {
            result = input.Text;
            dialog.Close();
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string? typeToConfirm = null)
    {
        var result = false;
        var dialog = BuildDialog(title, 480, out var ok, out var body, confirmLabel);
        body.Children.Add(Text(message));

        if (typeToConfirm is not null)
        {
            var input = new TextBox { PlaceholderText = typeToConfirm };
            body.Children.Add(Text($"Type '{typeToConfirm}' to confirm:"));
            body.Children.Add(input);
            ok.IsEnabled = false;
            input.TextChanged += (_, _) => ok.IsEnabled = input.Text == typeToConfirm;
        }

        ok.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    public async Task<ResubmitChoice?> ChooseResubmitAsync(EntityInfo source, MessageSource subQueue, string scopeDescription, IReadOnlyList<EntityInfo> targets)
    {
        var defaultTarget = source.DefaultResubmitTarget!;
        var useDefault = new RadioButton
        {
            GroupName = "target",
            IsChecked = true,
            Content = source.Kind == EntityKind.Subscription ? $"Topic '{defaultTarget}' (original)" : $"Queue '{defaultTarget}' (original)",
        };
        var useOther = new RadioButton { GroupName = "target", Content = "Another queue or topic:" };
        var other = new ComboBox
        {
            ItemsSource = targets.Select(t => (t.Kind == EntityKind.Topic ? "topic: " : "queue: ") + t.Path).ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false,
        };
        var otherManual = new TextBox { PlaceholderText = "…or type an entity name", IsEnabled = false };
        useOther.IsCheckedChanged += (_, _) => other.IsEnabled = otherManual.IsEnabled = useOther.IsChecked == true;

        var strip = new CheckBox { Content = "Remove DeadLetterReason / DeadLetterErrorDescription properties" };
        var stamp = new CheckBox { Content = "Add x-resubmitted-* headers (time, source, count)", IsChecked = true };

        var topicWarning = new Border
        {
            Classes = { "banner" },
            Child = Text("This target is a topic: the message will be delivered to EVERY subscription whose rules match, "
                + "not only the one it was dead-lettered from."),
        };
        var error = ErrorText();

        string? ResolveTarget()
        {
            if (useDefault.IsChecked == true)
            {
                return defaultTarget;
            }

            if (!string.IsNullOrWhiteSpace(otherManual.Text))
            {
                return otherManual.Text.Trim();
            }

            return other.SelectedItem is string s ? s[(s.IndexOf(':') + 2)..] : null;
        }

        bool IsTopic(string? path) =>
            path is not null && (targets.Any(t => t.Kind == EntityKind.Topic && t.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                || (useDefault.IsChecked == true && source.Kind == EntityKind.Subscription));

        void UpdateWarning() => topicWarning.IsVisible = IsTopic(ResolveTarget());
        useDefault.IsCheckedChanged += (_, _) => UpdateWarning();
        other.SelectionChanged += (_, _) => UpdateWarning();
        otherManual.TextChanged += (_, _) => UpdateWarning();
        UpdateWarning();

        ResubmitChoice? result = null;
        var dialog = BuildDialog("Resubmit messages", 560, out var ok, out var body, "Resubmit");
        body.Children.Add(Text($"Move {scopeDescription} from '{source.Path}{subQueue.PathSuffix()}' to:"));
        body.Children.Add(useDefault);
        body.Children.Add(useOther);
        body.Children.Add(other);
        body.Children.Add(otherManual);
        body.Children.Add(topicWarning);
        body.Children.Add(strip);
        body.Children.Add(stamp);
        body.Children.Add(Muted("Each message is sent and removed from the dead-letter queue in one transaction where the service allows it; "
            + "otherwise it is sent first and then removed, so a failure can duplicate but never lose a message."));
        body.Children.Add(error);

        ok.Click += (_, _) =>
        {
            var target = ResolveTarget();
            if (string.IsNullOrWhiteSpace(target))
            {
                error.Text = "Choose a target entity.";
                error.IsVisible = true;
                return;
            }

            result = new ResubmitChoice(target, strip.IsChecked == true, stamp.IsChecked == true);
            dialog.Close();
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    public async Task<ComposedMessage?> ComposeMessageAsync(
        string title, string defaultTarget, IReadOnlyList<EntityInfo> targets, OutgoingMessage? prefill)
    {
        var targetNames = targets.Select(t => t.Path).ToList();
        if (!targetNames.Contains(defaultTarget, StringComparer.OrdinalIgnoreCase))
        {
            targetNames.Insert(0, defaultTarget);
        }

        var target = new ComboBox { ItemsSource = targetNames, SelectedItem = defaultTarget, HorizontalAlignment = HorizontalAlignment.Stretch };
        var body = new TextBox
        {
            Text = prefill?.Body ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 160,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 12,
        };
        var contentType = new TextBox { Text = prefill?.ContentType ?? "application/json" };
        var subject = new TextBox { Text = prefill?.Subject };
        var messageId = new TextBox { Text = prefill?.MessageId, PlaceholderText = "generated if empty" };
        var correlationId = new TextBox { Text = prefill?.CorrelationId };
        var sessionId = new TextBox { Text = prefill?.SessionId, PlaceholderText = "required for session-enabled targets" };
        var to = new TextBox { Text = prefill?.To };
        var replyTo = new TextBox { Text = prefill?.ReplyTo };
        var ttl = new TextBox { PlaceholderText = "entity default — or e.g. 00:30:00" };
        var delay = new TextBox { PlaceholderText = "send now — or minutes from now to schedule" };
        var properties = new TextBox
        {
            Text = OutgoingMessage.FormatProperties(prefill?.ApplicationProperties),
            AcceptsReturn = true,
            Height = 90,
            PlaceholderText = "one name=value per line, e.g.\ntenant=acme\nretry=3",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 12,
        };
        var error = ErrorText();

        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*"), RowSpacing = 6 };
        void Row(string label, Control control)
        {
            var row = fields.RowDefinitions.Count;
            fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            fields.Children.Add(l);
            fields.Children.Add(control);
        }

        Row("Content type", contentType);
        Row("Subject", subject);
        Row("Message ID", messageId);
        Row("Correlation ID", correlationId);
        Row("Session ID", sessionId);
        Row("To", to);
        Row("Reply to", replyTo);
        Row("Time to live", ttl);
        Row("Schedule in (min)", delay);

        ComposedMessage? result = null;
        var dialog = BuildDialog(title, 680, out var ok, out var panel, "Send");
        panel.Children.Add(Label("Target queue or topic"));
        panel.Children.Add(target);
        panel.Children.Add(Label("Body"));
        panel.Children.Add(body);
        panel.Children.Add(fields);
        panel.Children.Add(Label("Application properties"));
        panel.Children.Add(properties);
        panel.Children.Add(error);

        ok.Click += (_, _) =>
        {
            void Fail(string text)
            {
                error.Text = text;
                error.IsVisible = true;
            }

            if (target.SelectedItem is not string targetPath || string.IsNullOrWhiteSpace(targetPath))
            {
                Fail("Choose a target.");
                return;
            }

            TimeSpan? timeToLive = null;
            if (!string.IsNullOrWhiteSpace(ttl.Text))
            {
                if (!TimeSpan.TryParse(ttl.Text, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= TimeSpan.Zero)
                {
                    Fail("Time to live must look like 00:30:00 (hh:mm:ss) or 1.00:00:00 (d.hh:mm:ss).");
                    return;
                }

                timeToLive = parsed;
            }

            DateTimeOffset? scheduleAt = null;
            if (!string.IsNullOrWhiteSpace(delay.Text))
            {
                if (!double.TryParse(delay.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
                {
                    Fail("Schedule must be a positive number of minutes.");
                    return;
                }

                scheduleAt = DateTimeOffset.UtcNow.AddMinutes(minutes);
            }

            IReadOnlyDictionary<string, object> props;
            try
            {
                props = OutgoingMessage.ParseProperties(properties.Text);
            }
            catch (FormatException ex)
            {
                Fail("Application properties: " + ex.Message);
                return;
            }

            result = new ComposedMessage(targetPath, new OutgoingMessage(
                body.Text ?? "", contentType.Text, subject.Text, messageId.Text, correlationId.Text, sessionId.Text,
                to.Text, replyTo.Text, timeToLive, scheduleAt, props));
            dialog.Close();
        };

        dialog.SizeToContent = SizeToContent.Manual;
        dialog.Height = 760;
        dialog.CanResize = true;
        var content = dialog.Content;
        dialog.Content = null; // detach before re-parenting into the scroll viewer
        dialog.Content = new ScrollViewer { Content = content };
        await dialog.ShowDialog(owner);
        return result;
    }

    public async Task<Stream?> SaveJsonFileAsync(string title, string suggestedFileName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });

        return file is null ? null : await file.OpenWriteAsync();
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = BuildDialog(title, 520, out var ok, out var body, "OK", showCancel: false);
        body.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    public async Task OpenUrlAsync(Uri url) => await owner.Launcher.LaunchUriAsync(url);

    private static Window BuildDialog(string title, double width, out Button ok, out StackPanel body, string okLabel, bool showCancel = true)
    {
        body = new StackPanel { Spacing = 8 };
        ok = new Button { Content = okLabel, IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsVisible = showCancel };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { ok, cancel },
        };

        var window = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20), Children = { body, buttons } },
        };
        cancel.Click += (_, _) => window.Close();
        return window;
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };

    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65 };

    private static TextBlock ErrorText() => new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
}
