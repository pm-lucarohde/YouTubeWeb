using System.Globalization;
using System.Xml;

namespace YtMobile.Youtube;

/// <summary>German-language label formatting for durations, counts and dates.</summary>
public static class Format
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>"PT1H2M3S" -> "1:02:03", "PT4M5S" -> "4:05". Live streams have no duration.</summary>
    public static string? Duration(string? iso8601)
    {
        if (string.IsNullOrEmpty(iso8601)) return null;

        TimeSpan span;
        try
        {
            span = XmlConvert.ToTimeSpan(iso8601);
        }
        catch (FormatException)
        {
            return null;
        }

        if (span <= TimeSpan.Zero) return null;

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    /// <summary>"PT1M30S" -> 90. 0, wenn die Länge fehlt (Livestreams).</summary>
    public static int DurationSeconds(string? iso8601)
    {
        if (string.IsNullOrEmpty(iso8601)) return 0;
        try
        {
            var span = XmlConvert.ToTimeSpan(iso8601);
            return span > TimeSpan.Zero ? (int)span.TotalSeconds : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    /// <summary>1234567 -> "1,2 Mio."</summary>
    public static string Compact(ulong n) => n switch
    {
        >= 1_000_000_000 => $"{(n / 1_000_000_000d).ToString("0.#", De)} Mrd.",
        >= 1_000_000 => $"{(n / 1_000_000d).ToString("0.#", De)} Mio.",
        >= 1_000 => $"{(n / 1_000d).ToString("0.#", De)} Tsd.",
        _ => n.ToString("N0", De)
    };

    public static string? Views(ulong? n) => n is null ? null : $"{Compact(n.Value)} Aufrufe";

    public static string? Likes(ulong? n) => n is null ? null : Compact(n.Value);

    /// <summary>Relative age, e.g. "vor 3 Tagen".</summary>
    public static string Age(DateTimeOffset published)
    {
        var d = DateTimeOffset.UtcNow - published;

        if (d.TotalMinutes < 1) return "gerade eben";
        if (d.TotalHours < 1) return Plural((int)d.TotalMinutes, "Minute", "Minuten");
        if (d.TotalDays < 1) return Plural((int)d.TotalHours, "Stunde", "Stunden");
        if (d.TotalDays < 7) return Plural((int)d.TotalDays, "Tag", "Tagen");
        if (d.TotalDays < 31) return Plural((int)(d.TotalDays / 7), "Woche", "Wochen");
        if (d.TotalDays < 365) return Plural((int)(d.TotalDays / 30), "Monat", "Monaten");

        return Plural((int)(d.TotalDays / 365), "Jahr", "Jahren");
    }

    private static string Plural(int n, string one, string many) =>
        n <= 1 ? $"vor 1 {one}" : $"vor {n} {many}";
}
