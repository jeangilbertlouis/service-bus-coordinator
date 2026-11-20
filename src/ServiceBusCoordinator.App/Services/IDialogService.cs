using ServiceBusCoordinator.Core.Connections;
using ServiceBusCoordinator.Core.Messages;
using ServiceBusCoordinator.Core.Models;
using ServiceBusCoordinator.Core.Settings;

namespace ServiceBusCoordinator.App.Services;

/// <summary>What the user chose in the resubmit dialog.</summary>
public sealed record ResubmitChoice(string TargetEntityPath, bool StripDeadLetterProperties, bool StampResubmitHeaders);

/// <summary>A message composed in the send dialog, and where to send it.</summary>
public sealed record ComposedMessage(string TargetEntityPath, OutgoingMessage Message);

/// <summary>What the user chose in the export dialog: a password to encrypt the file with, or null for plain JSON.</summary>
public sealed record ExportChoice(string? Password);

public interface IDialogService
{
    Task<ConnectionProfile?> AddConnectionAsync();

    Task<string?> PromptAsync(string title, string message, string watermark, string? initialValue = null);

    /// <summary>Asks for confirmation. When <paramref name="typeToConfirm"/> is set the user must type it exactly.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string? typeToConfirm = null);

    Task<ResubmitChoice?> ChooseResubmitAsync(EntityInfo source, MessageSource subQueue, string scopeDescription, IReadOnlyList<EntityInfo> targets);

    Task<ComposedMessage?> ComposeMessageAsync(string title, string defaultTarget, IReadOnlyList<EntityInfo> targets, OutgoingMessage? prefill);

    /// <summary>Shows a save-file picker for a JSON file. Returns a writable stream, or null when cancelled.</summary>
    Task<Stream?> SaveJsonFileAsync(string title, string suggestedFileName);

    /// <summary>Shows an open-file picker for a JSON file. Returns a readable stream, or null when cancelled.</summary>
    Task<Stream?> OpenJsonFileAsync(string title);

    /// <summary>Asks whether and how to protect a connections export. Returns null when cancelled.</summary>
    Task<ExportChoice?> ChooseExportAsync(int connectionCount);

    /// <summary>Asks for a password (input hidden). Returns null when cancelled.</summary>
    Task<string?> PromptPasswordAsync(string title, string message, string? error = null);

    Task ShowMessageAsync(string title, string message);

    /// <summary>Shows the Options dialog for a copy of the options. Returns the edited copy, or null when cancelled.</summary>
    Task<AppOptions?> EditOptionsAsync(AppOptions options);

    /// <summary>Opens a web page in the user's browser.</summary>
    Task OpenUrlAsync(Uri url);
}
