using System.Security.Cryptography;
using System.Text;

namespace RagAssistant.Modules.Documents.Common;

internal static class DocumentText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Decodes strictly: invalid UTF-8 is an error, never silently replaced.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>Removes a leading BOM and converts CRLF and CR line endings to LF.</summary>
    public static string Normalize(string text)
    {
        var span = text.AsSpan();
        if (span.Length > 0 && span[0] == '﻿')
        {
            span = span[1..];
        }

        return span.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>SHA-256 over the UTF-8 bytes of already-normalized text, lowercase hex.</summary>
    public static string Hash(string normalizedText) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText)));
}
