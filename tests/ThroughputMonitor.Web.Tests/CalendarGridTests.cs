using System.Globalization;
using ThroughputMonitor.Web.Data;

namespace ThroughputMonitor.Web.Tests;

public class CalendarGridTests
{
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact]
    public void Grid_is_always_six_weeks_of_consecutive_days()
    {
        foreach (var month in new[] { new DateTime(2026, 2, 1), new DateTime(2026, 11, 15), new DateTime(2024, 2, 29), new DateTime(2026, 8, 1) })
        {
            var grid = CalendarGrid.Build(month, DayOfWeek.Monday);

            Assert.Equal(42, grid.Count);
            for (int i = 1; i < grid.Count; i++)
                Assert.Equal(grid[i - 1].AddDays(1), grid[i]);
            Assert.Contains(new DateTime(month.Year, month.Month, 1), grid);
            Assert.Contains(new DateTime(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month)), grid);
        }
    }

    [Fact]
    public void Monday_first_grid_for_november_2026_starts_on_monday_26_october()
    {
        // 1 November 2026 is a Sunday, so a Monday-first week still has six days of October in front of it.
        var grid = CalendarGrid.Build(new DateTime(2026, 11, 19), DayOfWeek.Monday);

        Assert.Equal(new DateTime(2026, 10, 26), grid[0]);
        Assert.Equal(DayOfWeek.Monday, grid[0].DayOfWeek);
        Assert.Equal(new DateTime(2026, 11, 1), grid[6]);
    }

    [Fact]
    public void Sunday_first_grid_for_november_2026_starts_on_the_first()
    {
        var grid = CalendarGrid.Build(new DateTime(2026, 11, 19), DayOfWeek.Sunday);

        Assert.Equal(new DateTime(2026, 11, 1), grid[0]);
    }

    [Fact]
    public void Every_row_starts_on_the_chosen_first_day()
    {
        var grid = CalendarGrid.Build(new DateTime(2026, 3, 10), DayOfWeek.Wednesday);

        for (int week = 0; week < 6; week++)
            Assert.Equal(DayOfWeek.Wednesday, grid[week * 7].DayOfWeek);
    }

    [Fact]
    public void Day_headers_start_on_the_first_day_of_the_week()
    {
        var headers = CalendarGrid.DayHeaders(Invariant, DayOfWeek.Monday);

        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"], headers);
        Assert.Equal("Sun", CalendarGrid.DayHeaders(Invariant, DayOfWeek.Sunday)[0]);
    }

    [Theory]
    [InlineData("19-11-2026", 2026, 11, 19)]        // Dutch style
    [InlineData("1-2-2026", 2026, 2, 1)]
    [InlineData("2026-11-19", 2026, 11, 19)]        // ISO
    [InlineData("  19-11-2026  ", 2026, 11, 19)]    // stray spaces
    [InlineData("19/11/2026", 2026, 11, 19)]
    public void Typed_dates_are_understood(string text, int y, int m, int d)
    {
        Assert.True(DateInput.TryParse(text, Dutch, out var value));
        Assert.Equal(new DateTime(y, m, d), value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_text_means_no_date_and_is_valid(string? text)
    {
        Assert.True(DateInput.TryParse(text, Dutch, out var value));
        Assert.Null(value);
    }

    [Theory]
    [InlineData("tomorrow-ish")]
    [InlineData("32-13-2026")]
    [InlineData("hello")]
    public void Garbage_is_rejected(string text)
    {
        Assert.False(DateInput.TryParse(text, Dutch, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void A_typed_date_drops_any_time_part()
    {
        Assert.True(DateInput.TryParse("19-11-2026 14:30", Dutch, out var value));
        Assert.Equal(new DateTime(2026, 11, 19), value);
    }
}
