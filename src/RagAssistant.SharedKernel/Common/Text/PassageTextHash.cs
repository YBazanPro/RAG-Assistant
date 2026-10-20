using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RagAssistant.SharedKernel.Common.Text;

/// <summary>
/// The fingerprint of a passage's text (spec 020): Unicode NFC, runs of ASCII whitespace collapsed to one space, trimmed, then
/// SHA-256 as lowercase hex. Passage ids are never reused across versions (ADR 0004), so a citation finds "the same passage" in a
/// newer version by this hash. Documents hashes its passages and Chat its snapshots, so both must use this one definition.
/// </summary>
/// <remarks>
/// Only ASCII whitespace is collapsed, because the migrations backfill existing rows in SQL with the same rule
/// (<see cref="SqlExpression"/>), and PostgreSQL's notion of Unicode whitespace depends on the locale.
/// </remarks>
public static partial class PassageTextHash
{
    public const int Length = 64;

    public static string Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = Whitespace().Replace(text.Normalize(NormalizationForm.FormC), " ").Trim(' ');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>The same hash in PostgreSQL, for a backfill over <paramref name="column"/> (needs a UTF-8 database).</summary>
    public static string SqlExpression(string column) =>
        $"encode(sha256(convert_to(btrim(regexp_replace(normalize({column}, NFC), '[ \\t\\n\\r\\f\\v]+', ' ', 'g'), ' '), 'UTF8')), 'hex')";

    [GeneratedRegex("[ \t\n\r\f\v]+")]
    private static partial Regex Whitespace();
}
