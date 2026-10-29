using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Tests;

public class ConnectionEditorTests
{
    [Fact]
    public void Unchanged_distro_choices_keep_the_same_items_source()
    {
        var editor = new ConnectionEditorViewModel { WslDistro = "Jeek-Wsl-regression-test" };
        var choices = editor.AvailableWslDistros;

        Assert.Contains(editor.WslDistro, choices);
        Assert.Same(choices, editor.AvailableWslDistros);
    }

    [Fact]
    public void Changing_the_saved_distro_refreshes_choices_and_preserves_missing_names()
    {
        var editor = new ConnectionEditorViewModel { WslDistro = "Jeek-Wsl-old-test" };
        var oldChoices = editor.AvailableWslDistros;

        editor.WslDistro = "Jeek-Wsl-new-test";
        var newChoices = editor.AvailableWslDistros;

        Assert.NotSame(oldChoices, newChoices);
        Assert.Contains(editor.WslDistro, newChoices);
        Assert.Same(newChoices, editor.AvailableWslDistros);
    }

    [Fact]
    public void Selection_clear_does_not_trigger_repeated_items_source_replacement()
    {
        var editor = new ConnectionEditorViewModel { WslDistro = "Jeek-Wsl-selection-test" };
        var choices = editor.AvailableWslDistros;
        var replacements = 0;
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(editor.AvailableWslDistros))
                return;
            var next = editor.AvailableWslDistros;
            if (ReferenceEquals(choices, next))
                return;
            // Model the ComboBox clearing SelectedItem when ItemsSource changes.
            // Bound the recursion so the regression fails without crashing VSTest.
            if (++replacements > 8)
                throw new InvalidOperationException("The distro ItemsSource keeps replacing itself.");
            choices = next;
            editor.WslDistro = null!;
        };

        editor.WslDistro = null!;

        Assert.Equal("Jeek-Wsl-selection-test", editor.WslDistro);
        Assert.Equal(0, replacements);
    }

    [Fact]
    public void Debug_MCP_advertises_the_WSL_editor_selection_probe() =>
        Assert.Contains(DebugMcpContract.BuildToolList(),
            tool => tool?["name"]?.GetValue<string>() == "wsl_editor_selection_check");
}
