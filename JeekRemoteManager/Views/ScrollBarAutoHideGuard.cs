using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace JeekRemoteManager.Views;

/// <summary>
/// Keeps every <see cref="ScrollBar"/> and <see cref="ScrollViewer"/> permanently
/// non-auto-hiding so scrollbars stay at their full width and reserve their own
/// layout track instead of overlaying content.
/// <para>
/// Global styles in <c>Themes/AppControls.axaml</c> cover ordinary XAML and
/// code-built controls; this guard also catches third-party controls such as
/// <see cref="SvcSystems.UI.Terminal.TerminalControl"/> that assign
/// <see cref="ScrollBar.AllowAutoHide"/> = <see langword="true"/> as a local
/// value (which outranks styles) during construction and layout updates.
/// </para>
/// </summary>
internal static class ScrollBarAutoHideGuard
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;

        ScrollBar.AllowAutoHideProperty.Changed.AddClassHandler<ScrollBar>((bar, e) =>
        {
            if (e.NewValue is true)
                bar.SetCurrentValue(ScrollBar.AllowAutoHideProperty, false);
        });

        ScrollViewer.AllowAutoHideProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
        {
            if (e.NewValue is true)
                viewer.SetCurrentValue(ScrollViewer.AllowAutoHideProperty, false);
        });

        // A local assignment of true on a newly created control matches the default
        // value (true) and does not raise Changed, so normalize on template apply
        // (before the first layout/render) and again on load.
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ScrollBar>((bar, _) =>
        {
            if (bar.AllowAutoHide)
                bar.SetCurrentValue(ScrollBar.AllowAutoHideProperty, false);
        });

        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ScrollViewer>((viewer, _) =>
        {
            if (viewer.AllowAutoHide)
                viewer.SetCurrentValue(ScrollViewer.AllowAutoHideProperty, false);
        });

        Control.LoadedEvent.AddClassHandler<ScrollBar>((bar, _) =>
        {
            if (bar.AllowAutoHide)
                bar.SetCurrentValue(ScrollBar.AllowAutoHideProperty, false);
        });

        Control.LoadedEvent.AddClassHandler<ScrollViewer>((viewer, _) =>
        {
            if (viewer.AllowAutoHide)
                viewer.SetCurrentValue(ScrollViewer.AllowAutoHideProperty, false);
        });
    }
}
