using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SteamSwitcher.Core;

/// <summary>
/// Shared title-bar drag behavior for <c>WindowStyle="None"</c> windows.
/// A bare <see cref="Window.DragMove"/> in a Window-level MouseLeftButtonDown
/// handler fires for presses on any element that does not mark the event as
/// handled, so dragging a text selection inside a TextBox or nudging a Slider
/// used to drag the whole window along with it. This helper keeps dragging for
/// passive surfaces (title bars, empty space) and yields for interactive
/// controls that own the gesture themselves.
/// </summary>
internal static class WindowDragHelper
{
    private static readonly Type[] InteractiveTypes =
    {
        typeof(ButtonBase),      // Button, ToggleButton, CheckBox, RadioButton, RepeatButton
        typeof(TextBoxBase),     // TextBox, RichTextBox
        typeof(PasswordBox),
        typeof(ComboBox),
        typeof(ComboBoxItem),
        typeof(Slider),
        typeof(Thumb),
        typeof(ScrollBar),
        typeof(ListBoxItem),
        typeof(ListViewItem),
        typeof(DataGridCell),
        typeof(MenuItem),
        typeof(TabItem),
        typeof(Expander),
    };

    /// <summary>
    /// True when the press originated from — or bubbled through — an
    /// interactive control that should handle the gesture itself.
    /// </summary>
    public static bool IsInteractiveSource(object? source)
    {
        if (source is not DependencyObject current) return false;

        while (current != null)
        {
            foreach (var t in InteractiveTypes)
            {
                if (t.IsInstanceOfType(current)) return true;
            }

            // Logical parent first so template children resolve to the
            // templated parent (and Popup content to its placement target),
            // then fall back to the visual parent for raw adorner surfaces.
            var logical = LogicalTreeHelper.GetParent(current);
            current = logical ?? VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    /// <summary>
    /// Drags the window when the gesture came from a passive surface.
    /// Safe to call from a Window-level MouseLeftButtonDown handler.
    /// </summary>
    public static void TryDrag(Window window, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (IsInteractiveSource(e.OriginalSource)) return;

        try
        {
            window.DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove aborts when the window changes state mid-drag.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Same class of race when the button is released outside.
        }
    }
}
