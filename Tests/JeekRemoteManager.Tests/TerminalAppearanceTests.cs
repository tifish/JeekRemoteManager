using JeekRemoteManager.Services;

namespace JeekRemoteManager.Tests;

public class TerminalAppearanceTests
{
    [Fact]
    public void Unknown_scheme_falls_back_to_default() =>
        Assert.Equal(TerminalAppearance.DefaultSchemeName, TerminalAppearance.NormalizeSchemeName("no such scheme"));

    [Fact]
    public void Scheme_names_match_case_insensitively() =>
        Assert.Equal("Dracula", TerminalAppearance.NormalizeSchemeName("dracula"));

    [Fact]
    public void Every_scheme_has_a_full_palette() =>
        Assert.All(TerminalAppearance.Schemes, scheme => Assert.Equal(16, scheme.Palette.Count));

    [Theory]
    [InlineData(0, TerminalAppearance.DefaultScrollbackLines)]
    [InlineData(5, 100)]
    [InlineData(5000, 5000)]
    [InlineData(10_000_000, 200_000)]
    public void Scrollback_is_clamped(int input, int expected) =>
        Assert.Equal(expected, TerminalAppearance.NormalizeScrollback(input));

    [Fact]
    public void The_render_cache_hooks_still_exist() =>
        Assert.True(TerminalAppearance.CanClearRenderCache,
            "SvcSystems.UI.Terminal renamed ClearFormattedTextCache or _surface; scheme switches would leave stale text colors.");
}
