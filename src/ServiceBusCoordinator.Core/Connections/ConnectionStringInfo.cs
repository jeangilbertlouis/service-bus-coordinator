using System.Text.RegularExpressions;
using Azure.Messaging.ServiceBus;

namespace ServiceBusCoordinator.Core.Connections;

/// <summary>Non-secret facts parsed from a Service Bus connection string.</summary>
public sealed partial record ConnectionStringInfo(string FullyQualifiedNamespace, string? EntityPath, string? KeyName, bool IsEmulator = false)
{
    /// <summary>Port the local emulator serves management operations on.</summary>
    public const int EmulatorManagementPort = 5300;

    [GeneratedRegex(@"(Endpoint\s*=\s*sb://)([^;/:]+)(:\d+)?(/?)", RegexOptions.IgnoreCase)]
    private static partial Regex EndpointPattern();

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
                props.SharedAccessKeyName,
                IsEmulatorConnectionString(connectionString));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            error = SecretScrubber.Scrub(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The emulator serves management operations on port 5300 while AMQP uses the default port, so one pasted
    /// connection string needs the port added for the administration client. Other connection strings are unchanged.
    /// </summary>
    public static string GetAdministrationConnectionString(string connectionString)
    {
        if (!IsEmulatorConnectionString(connectionString))
        {
            return connectionString;
        }

        return EndpointPattern().Replace(
            connectionString,
            m => m.Groups[3].Success ? m.Value : $"{m.Groups[1].Value}{m.Groups[2].Value}:{EmulatorManagementPort}{m.Groups[4].Value}",
            count: 1);
    }

    private static bool IsEmulatorConnectionString(string connectionString) =>
        connectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase);
}
