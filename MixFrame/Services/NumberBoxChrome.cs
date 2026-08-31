using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MixFrame.Services;

internal static class NumberBoxChrome
{
    public static void RemoveClearButton(NumberBox numberBox)
    {
        numberBox.ApplyTemplate();
        var inputBox = FindDescendant<TextBox>(numberBox, "InputBox");
        if (inputBox is null) return;

        inputBox.ApplyTemplate();
        var clearButton = FindDescendant<Button>(inputBox, "DeleteButton");
        if (clearButton is null) return;

        clearButton.Visibility = Visibility.Collapsed;
        clearButton.IsHitTestVisible = false;
        clearButton.MinWidth = 0;
        clearButton.Width = 0;
        clearButton.MaxWidth = 0;
        clearButton.Margin = new Thickness(0);
        clearButton.Padding = new Thickness(0);
    }

    private static T? FindDescendant<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && string.Equals(match.Name, name, StringComparison.Ordinal))
                return match;

            var nested = FindDescendant<T>(child, name);
            if (nested is not null) return nested;
        }

        return null;
    }
}
