using System.Text.RegularExpressions;

namespace ServiceBusCoordinator.Core;

/// <summary>Removes SAS keys/signatures from text before it is shown or logged.</summary>
public static partial class SecretScrubber
{
    [GeneratedRegex(@"(SharedAccessKey|SharedAccessSignature)\s*=\s*[^;""'\s]*", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    public static string Scrub(string? text) =>
        string.IsNullOrEmpty(text) ? text ?? "" : SecretPattern().Replace(text, "$1=***");
}
