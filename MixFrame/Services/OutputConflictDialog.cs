using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MixFrame.Services;

public enum OutputConflictChoice
{
    Overwrite,
    Rename,
    Skip
}

public sealed record OutputConflictDecision(OutputConflictChoice Choice, bool ApplyToRemaining);

public static class OutputConflictDialog
{
    public static async Task<OutputConflictDecision> ShowAsync(XamlRoot xamlRoot, string outputPath, bool isSourceFile)
    {
        var applyToRemaining = new CheckBox
        {
            Content = "将此选择应用到剩余冲突"
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = isSourceFile
                ? $"输出路径与源文件相同，不能直接覆盖源文件。\n\n{outputPath}"
                : $"目标位置已经存在同名文件。\n\n{outputPath}",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        content.Children.Add(applyToRemaining);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "输出文件已存在",
            Content = content,
            PrimaryButtonText = "自动改名",
            SecondaryButtonText = "覆盖",
            CloseButtonText = "跳过",
            DefaultButton = ContentDialogButton.Primary,
            IsSecondaryButtonEnabled = !isSourceFile
        };

        var choice = await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => OutputConflictChoice.Rename,
            ContentDialogResult.Secondary => OutputConflictChoice.Overwrite,
            _ => OutputConflictChoice.Skip
        };
        return new OutputConflictDecision(choice, applyToRemaining.IsChecked == true);
    }
}
