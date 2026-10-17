using System.Globalization;

namespace RagAssistant.Web.Client.Admin;

public static class AdminFormat
{
    public static string Ms(double? value) => value is { } ms ? $"{Math.Round(ms).ToString(CultureInfo.InvariantCulture)} ms" : "—";

    public static string Ms(int? value) => value is { } ms ? $"{ms.ToString(CultureInfo.InvariantCulture)} ms" : "—";

    public static string Percent(double ratio) => (ratio * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %";

    public static string Score(double? value) => value is { } score ? score.ToString("0.000", CultureInfo.InvariantCulture) : "—";

    /// <summary>"45 s", "12 min", "3.5 h", "2 d": the unit that keeps the number short.</summary>
    public static string Duration(double seconds) => seconds switch
    {
        < 90 => $"{Math.Round(seconds).ToString(CultureInfo.InvariantCulture)} s",
        < 5400 => $"{Math.Round(seconds / 60).ToString(CultureInfo.InvariantCulture)} min",
        < 172_800 => $"{(seconds / 3600).ToString("0.#", CultureInfo.InvariantCulture)} h",
        _ => $"{Math.Round(seconds / 86_400).ToString(CultureInfo.InvariantCulture)} d",
    };

    /// <summary>The owner is only a <c>sub</c>: shown shortened, in full as the tooltip and by the copy button.</summary>
    public static string ShortSub(string sub) => sub.Length <= 12 ? sub : sub[..8] + "…";
}
