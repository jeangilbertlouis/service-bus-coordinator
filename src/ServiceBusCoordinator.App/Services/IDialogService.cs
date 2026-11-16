using ServiceBusCoordinator.Core.Connections;
using ServiceBusCoordinator.Core.Messages;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.App.Services;

/// <summary>What the user chose in the resubmit dialog.</summary>
public sealed record ResubmitChoice(string TargetEntityPath, bool StripDeadLetterProperties, bool StampResubmitHeaders);

public interface IDialogService
{
    Task<ConnectionProfile?> AddConnectionAsync();

    Task<string?> PromptAsync(string title, string message, string watermark);

    /// <summary>Asks for confirmation. When <paramref name="typeToConfirm"/> is set the user must type it exactly.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string? typeToConfirm = null);

    Task<ResubmitChoice?> ChooseResubmitAsync(EntityInfo source, MessageSource subQueue, string scopeDescription, IReadOnlyList<EntityInfo> targets);

    Task ShowMessageAsync(string title, string message);
}
