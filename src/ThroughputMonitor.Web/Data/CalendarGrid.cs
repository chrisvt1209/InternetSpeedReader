using System.Globalization;

namespace ThroughputMonitor.Web.Data;

/// <summary>Pure helpers behind the date picker: which days to draw, and how to read typed dates.</summary>
public static class CalendarGrid
{
    /// <summary>
    /// 42 days (6 weeks x 7) covering the month of <paramref name="anyDayInMonth"/>, starting on
    /// <paramref name="firstDayOfWeek"/>. Days before and after the month fill the first and last week.
    /// </summary>
    public static IReadOnlyList<DateTime> Build(DateTime anyDayInMonth, DayOfWeek firstDayOfWeek)
    {
        var first = new DateTime(anyDayInMonth.Year, anyDayInMonth.Month, 1);
        int offset = ((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        var start = first.AddDays(-offset);
        return Enumerable.Range(0, 42).Select(i => start.AddDays(i)).ToList();
    }

    /// <summary>Day names for the header row, starting on <paramref name="firstDayOfWeek"/>.</summary>
    public static IReadOnlyList<string> DayHeaders(CultureInfo culture, DayOfWeek firstDayOfWeek)
    {
        var names = culture.DateTimeFormat.AbbreviatedDayNames;   // Sunday first
        return Enumerable.Range(0, 7).Select(i => names[((int)firstDayOfWeek + i) % 7]).ToList();
    }
}

public static class DateInput
{
    private static readonly string[] ExtraFormats = ["yyyy-MM-dd", "d-M-yyyy", "d/M/yyyy", "d.M.yyyy"];

    /// <summary>
    /// Reads a date typed by a person. Empty text is valid and means "no date" (<paramref name="value"/> null).
    /// Returns false when the text is not a date.
    /// </summary>
    public static bool TryParse(string? text, CultureInfo culture, out DateTime? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        text = text.Trim();
        if (DateTime.TryParse(text, culture, DateTimeStyles.None, out var parsed) ||
            DateTime.TryParseExact(text, ExtraFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            value = parsed.Date;
            return true;
        }
        return false;
    }
}
