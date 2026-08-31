using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MixFrame.Services;

public static class PresetDialogService
{
    public static async Task<string?> PromptNameAsync(XamlRoot xamlRoot, string title, string initialValue = "")
    {
        var nameBox = new TextBox
        {
            Text = initialValue,
            PlaceholderText = "例如：网站横图 900×450",
            MaxLength = 40,
            MinWidth = 300,
            SelectionStart = 0,
            SelectionLength = initialValue.Length
        };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = nameBox,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? nameBox.Text.Trim() : null;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot xamlRoot, string title, string message, string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
