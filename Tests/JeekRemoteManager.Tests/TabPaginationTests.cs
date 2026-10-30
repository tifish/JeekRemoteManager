using Avalonia;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Tests;

public class TabPaginationTests
{
    [Fact]
    public void Five_rows_wrap_into_pages_without_losing_headers()
    {
        var layout = TabPagination.Calculate(Enumerable.Repeat(new Size(100, 34), 31).ToArray(), 300, 5, 1000);
        Assert.Equal(new[] { 5, 5, 1 }, layout.RowsPerPage);
        Assert.Equal(170, layout.Height);
        Assert.Equal(0, layout.Items[14].Page);
        Assert.Equal(1, layout.Items[15].Page);
        Assert.Equal(2, layout.Items[30].Page);
    }

    [Fact]
    public void Height_budget_and_variable_row_heights_are_respected()
    {
        var layout = TabPagination.Calculate([new(200, 34), new(200, 46), new(200, 34)], 200, 5, 70);
        Assert.Equal(new[] { 1, 1, 1 }, layout.RowsPerPage);
        Assert.Equal(46, layout.Height);
    }

    [Fact]
    public void Oversize_header_fits_narrow_window_and_empty_search_has_one_empty_page()
    {
        var layout = TabPagination.Calculate([new(500, 34), new(40, 34)], 100, 1, 1000);
        Assert.Equal(100, layout.Items[0].Bounds.Width);
        Assert.Equal(new[] { 1, 1 }, layout.RowsPerPage);
        var empty = TabPagination.Calculate([], 100, 5, 100);
        Assert.Equal(new[] { 0 }, empty.RowsPerPage);
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.Height);
    }

    [Fact]
    public void Row_setting_defaults_to_five_and_round_trips_in_machine_settings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "JrmTabSettings-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var machine = Path.Combine(directory, "machine.json");
            var roaming = Path.Combine(directory, "roaming.json");
            var settings = new SettingsService(machine, roaming);
            Assert.Equal(5, settings.Settings.TerminalTabMaxRows);
            settings.Settings.TerminalTabMaxRows = 3;
            settings.SaveIfChanged();
            Assert.Equal(3, new SettingsService(machine, roaming).Settings.TerminalTabMaxRows);
            Assert.Contains("TerminalTabMaxRows", File.ReadAllText(machine));
            Assert.DoesNotContain("TerminalTabMaxRows", (File.Exists(roaming) ? File.ReadAllText(roaming) : ""));
            settings.Settings.TerminalTabMaxRows = -1;
            settings.SaveIfChanged();
            Assert.Equal(1, new SettingsService(machine, roaming).Settings.TerminalTabMaxRows);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
