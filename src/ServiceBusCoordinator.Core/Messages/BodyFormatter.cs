using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ServiceBusCoordinator.Core.Messages;

public enum BodyKind
{
    Empty,
    Json,
    Xml,
    Text,
    Binary,
}

/// <summary>Turns a message body into display text: pretty JSON/XML, plain text, or a hex dump.</summary>
public static class BodyFormatter
{
    public const int MaxHexBytes = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static (BodyKind Kind, string Text) Format(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return (BodyKind.Empty, "");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return (BodyKind.Binary, HexDump(body));
        }

        text = text.TrimStart('﻿');
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
        {
            return (BodyKind.Binary, HexDump(body));
        }

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return (BodyKind.Json, JsonSerializer.Serialize(doc.RootElement, Indented));
            }
            catch (JsonException)
            {
                // Not JSON after all.
            }
        }

        if (trimmed.StartsWith('<'))
        {
            try
            {
                return (BodyKind.Xml, XDocument.Parse(text).ToString());
            }
            catch (System.Xml.XmlException)
            {
                // Not XML after all.
            }
        }

        return (BodyKind.Text, text);
    }

    public static string HexDump(ReadOnlySpan<byte> bytes)
    {
        var length = Math.Min(bytes.Length, MaxHexBytes);
        var sb = new StringBuilder();
        for (var offset = 0; offset < length; offset += 16)
        {
            var line = bytes.Slice(offset, Math.Min(16, length - offset));
            sb.Append(offset.ToString("X8")).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                sb.Append(i < line.Length ? line[i].ToString("X2") + " " : "   ");
            }

            sb.Append(' ');
            foreach (var b in line)
            {
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.AppendLine();
        }

        if (bytes.Length > length)
        {
            sb.AppendLine($"… {bytes.Length - length:N0} more bytes not shown");
        }

        return sb.ToString();
    }
}
