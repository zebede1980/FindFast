using System.Globalization;

namespace FastFind.Core.Search;

public static class SizeParser
{
    private static readonly (string Name, long Min, long Max)[] NamedSizes =
    {
        ("empty", 0, 0),
        ("tiny", 0, 10 * 1024L),
        ("small", 10 * 1024L, 100 * 1024L),
        ("medium", 100 * 1024L, 1024 * 1024L),
        ("large", 1024 * 1024L, 16 * 1024 * 1024L),
        ("huge", 16 * 1024 * 1024L, 128 * 1024 * 1024L),
        ("gigantic", 128 * 1024 * 1024L, long.MaxValue),
    };

    /// <summary>Parses "10mb", "1.5 GB", "500k", "1024", "12mib". Returns bytes and the unit's magnitude (1 for bytes).</summary>
    public static bool TryParseSize(string text, out long bytes, out long unit)
    {
        bytes = 0; unit = 1;
        text = text.Trim().ToLowerInvariant().Replace(" ", "").Replace(",", "");
        if (text.Length == 0) return false;
        int i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
        if (i == 0) return false;
        if (!double.TryParse(text.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return false;
        string suffix = text[i..].TrimEnd('b').TrimEnd('i');
        unit = suffix switch
        {
            "" => 1,
            "k" => 1024L,
            "m" => 1024L * 1024,
            "g" => 1024L * 1024 * 1024,
            "t" => 1024L * 1024 * 1024 * 1024,
            _ => -1,
        };
        if (unit < 0) return false;
        bytes = (long)Math.Round(value * unit);
        return true;
    }

    /// <summary>Parses a size expression: named sizes, &gt;x, &lt;x, &gt;=x, x..y, x-y, =x, or a bare value (meaning within one unit).</summary>
    public static bool TryParseRange(string text, out long min, out long max)
    {
        min = 0; max = long.MaxValue;
        text = text.Trim().ToLowerInvariant();
        foreach (var (name, lo, hi) in NamedSizes)
        {
            if (text == name) { min = lo; max = hi; return true; }
        }
        if (text.StartsWith(">=")) { if (!TryParseSize(text[2..], out min, out _)) return false; return true; }
        if (text.StartsWith("<=")) { if (!TryParseSize(text[2..], out max, out _)) return false; return true; }
        if (text.StartsWith('>')) { if (!TryParseSize(text[1..], out long v, out _)) return false; min = v + 1; return true; }
        if (text.StartsWith('<')) { if (!TryParseSize(text[1..], out long v, out _)) return false; max = v - 1; return true; }
        if (text.StartsWith('=')) { if (!TryParseSize(text[1..], out long v, out _)) return false; min = max = v; return true; }
        int sep = text.IndexOf("..", StringComparison.Ordinal);
        int sepLen = 2;
        if (sep < 0)
        {
            // "10mb-20mb": a dash after the first number
            int firstNonNumeric = 0;
            while (firstNonNumeric < text.Length && (char.IsDigit(text[firstNonNumeric]) || text[firstNonNumeric] == '.')) firstNonNumeric++;
            int dash = text.IndexOf('-', firstNonNumeric);
            if (dash > 0) { sep = dash; sepLen = 1; }
        }
        if (sep > 0)
        {
            if (!TryParseSize(text[..sep], out min, out _)) return false;
            if (!TryParseSize(text[(sep + sepLen)..], out max, out _)) return false;
            if (max < min) (min, max) = (max, min);
            return true;
        }
        if (TryParseSize(text, out long value, out long unit))
        {
            min = value;
            max = unit == 1 ? value : value + unit - 1;
            return true;
        }
        return false;
    }

    public static string Format(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double v = bytes / 1024.0;
        if (v < 1024) return v.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        v /= 1024;
        if (v < 1024) return v.ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        v /= 1024;
        if (v < 1024) return v.ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        v /= 1024;
        return v.ToString("0.##", CultureInfo.InvariantCulture) + " TB";
    }
}

public static class DateRangeParser
{
    /// <summary>
    /// Parses Everything-style date expressions into a [min, max) UTC range:
    /// today, yesterday, thisweek, lastweek, thismonth, lastmonth, thisyear, lastyear,
    /// lastNdays / lastNweeks / lastNmonths / lastNyears, 2024, 2024-05, 2024-05-17,
    /// &gt;date, &lt;date, &gt;=date, &lt;=date, date..date.
    /// </summary>
    public static bool TryParse(string text, DateTime nowLocal, out DateTime minUtc, out DateTime maxUtc)
    {
        minUtc = DateTime.MinValue; maxUtc = DateTime.MaxValue;
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0) return false;
        var today = nowLocal.Date;

        if (TryNamed(text, today, out var lo, out var hi)) { minUtc = ToUtc(lo); maxUtc = ToUtc(hi); return true; }

        if (text.StartsWith(">=")) { if (!TryPoint(text[2..], today, out lo, out _)) return false; minUtc = ToUtc(lo); return true; }
        if (text.StartsWith("<=")) { if (!TryPoint(text[2..], today, out _, out hi)) return false; maxUtc = ToUtc(hi); return true; }
        if (text.StartsWith('>')) { if (!TryPoint(text[1..], today, out _, out hi)) return false; minUtc = ToUtc(hi); return true; }
        if (text.StartsWith('<')) { if (!TryPoint(text[1..], today, out lo, out _)) return false; maxUtc = ToUtc(lo); return true; }

        int sep = text.IndexOf("..", StringComparison.Ordinal);
        if (sep > 0)
        {
            if (!TryPoint(text[..sep], today, out lo, out _)) return false;
            if (!TryPoint(text[(sep + 2)..], today, out _, out hi)) return false;
            minUtc = ToUtc(lo); maxUtc = ToUtc(hi);
            return true;
        }
        if (TryPoint(text, today, out lo, out hi)) { minUtc = ToUtc(lo); maxUtc = ToUtc(hi); return true; }
        return false;
    }

    private static DateTime ToUtc(DateTime local)
    {
        if (local == DateTime.MinValue || local == DateTime.MaxValue) return local;
        return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
    }

    private static bool TryNamed(string text, DateTime today, out DateTime lo, out DateTime hi)
    {
        lo = hi = default;
        switch (text)
        {
            case "today": lo = today; hi = today.AddDays(1); return true;
            case "yesterday": lo = today.AddDays(-1); hi = today; return true;
            case "thisweek":
            {
                int diff = ((int)today.DayOfWeek + 6) % 7; // Monday start
                lo = today.AddDays(-diff); hi = lo.AddDays(7); return true;
            }
            case "lastweek":
            {
                int diff = ((int)today.DayOfWeek + 6) % 7;
                hi = today.AddDays(-diff); lo = hi.AddDays(-7); return true;
            }
            case "thismonth": lo = new DateTime(today.Year, today.Month, 1); hi = lo.AddMonths(1); return true;
            case "lastmonth": hi = new DateTime(today.Year, today.Month, 1); lo = hi.AddMonths(-1); return true;
            case "thisyear": lo = new DateTime(today.Year, 1, 1); hi = lo.AddYears(1); return true;
            case "lastyear": hi = new DateTime(today.Year, 1, 1); lo = hi.AddYears(-1); return true;
        }
        if (text.StartsWith("last"))
        {
            var rest = text[4..];
            int i = 0;
            while (i < rest.Length && char.IsDigit(rest[i])) i++;
            if (i > 0 && int.TryParse(rest[..i], out int n))
            {
                string unit = rest[i..].TrimEnd('s');
                hi = today.AddDays(1);
                lo = unit switch
                {
                    "day" => today.AddDays(-n + 1),
                    "week" => today.AddDays(-7 * n + 1),
                    "month" => today.AddMonths(-n).AddDays(1),
                    "year" => today.AddYears(-n).AddDays(1),
                    "hour" => DateTime.Now.AddHours(-n),
                    "minute" or "min" => DateTime.Now.AddMinutes(-n),
                    _ => default,
                };
                return lo != default;
            }
        }
        return false;
    }

    /// <summary>A single date point expands to the span it covers: a year, a month, or a day.</summary>
    private static bool TryPoint(string text, DateTime today, out DateTime lo, out DateTime hi)
    {
        lo = hi = default;
        if (TryNamed(text, today, out lo, out hi)) return true;
        var parts = text.Split('-', '/', '.');
        if (parts.Length == 1 && parts[0].Length == 4 && int.TryParse(parts[0], out int y) && y >= 1601 && y <= 9999)
        {
            lo = new DateTime(y, 1, 1); hi = lo.AddYears(1); return true;
        }
        if (parts.Length == 2 && int.TryParse(parts[0], out y) && int.TryParse(parts[1], out int m) && y >= 1601 && m is >= 1 and <= 12)
        {
            lo = new DateTime(y, m, 1); hi = lo.AddMonths(1); return true;
        }
        if (parts.Length == 3)
        {
            // yyyy-mm-dd or dd-mm-yyyy
            if (parts[0].Length == 4 && int.TryParse(parts[0], out y) && int.TryParse(parts[1], out m) && int.TryParse(parts[2], out int d) && IsValid(y, m, d))
            { lo = new DateTime(y, m, d); hi = lo.AddDays(1); return true; }
            if (parts[2].Length == 4 && int.TryParse(parts[2], out y) && int.TryParse(parts[1], out m) && int.TryParse(parts[0], out d) && IsValid(y, m, d))
            { lo = new DateTime(y, m, d); hi = lo.AddDays(1); return true; }
        }
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            lo = dt.Date; hi = lo.AddDays(1); return true;
        }
        return false;
    }

    private static bool IsValid(int y, int m, int d) => y >= 1601 && y <= 9999 && m >= 1 && m <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m);
}
