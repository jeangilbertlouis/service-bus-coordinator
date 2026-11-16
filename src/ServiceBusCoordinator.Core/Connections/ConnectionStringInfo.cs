using Azure.Messaging.ServiceBus;

namespace ServiceBusCoordinator.Core.Connections;

/// <summary>Non-secret facts parsed from a Service Bus connection string.</summary>
public sealed record ConnectionStringInfo(string FullyQualifiedNamespace, string? EntityPath, string? KeyName)
{
    public static bool TryParse(string? connectionString, out ConnectionStringInfo? info, out string? error)
    {
        info = null;
        error = null;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            error = "Connection string is empty.";
            return false;
        }

        try
        {
            var props = ServiceBusConnectionStringProperties.Parse(connectionString.Trim());
            if (string.IsNullOrEmpty(props.FullyQualifiedNamespace))
            {
                error = "Connection string has no Endpoint.";
                return false;
            }

            info = new ConnectionStringInfo(
                props.FullyQualifiedNamespace,
                string.IsNullOrEmpty(props.EntityPath) ? null : props.EntityPath,
                props.SharedAccessKeyName);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            error = SecretScrubber.Scrub(ex.Message);
            return false;
        }
    }
}
