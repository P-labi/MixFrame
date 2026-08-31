using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MixFrame.Models;
using MixFrame.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MixFrame.Pages;

public sealed partial class ImageWorkspacePage : Page, IDroppedPathHandler
{
    private static readonly IReadOnlySet<string> BatchOutputExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".webp", ".jpg", ".jpeg", ".png" };
    private readonly ImageImportService _importService = new();
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingForm;
    private CancellationTokenSource? _exportCancellation;
    private readonly HashSet<string> _lastOutputDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly UndoActionStack _undoActions = new();
    private readonly PresetStoreService _presetStore = PresetStoreService.Instance;

    private sealed record RemovedImageAsset(ImageAsset Asset, int Index);

    private sealed record ImageOutputSnapshot(
        ImageAsset Asset,
        string OutputFormat,
        string SizeMode,
        double OutputWidth,
        double OutputHeight,
        double Quality,
        int CompressionLevel,
        string Background,
        string RequestedOutputFileName,
        string OutputLocation,
        string ExportStatus)
    {
        public static ImageOutputSnapshot Capture(ImageAsset asset) => new(
            asset,
            asset.OutputFormat,
            asset.SizeMode,
            asset.OutputWidth,
            asset.OutputHeight,
            asset.Quality,
            asset.CompressionLevel,
            asset.Background,
            asset.RequestedOutputFileName,
            asset.OutputLocation,
            asset.ExportStatus);

        public void Restore()
        {
            Asset.OutputFormat = OutputFormat;
            Asset.SizeMode = SizeMode;
            Asset.OutputWidth = OutputWidth;
            Asset.OutputHeight = OutputHeight;
            Asset.Quality = Quality;
            Asset.CompressionLevel = CompressionLevel;
            Asset.Background = Background;
            Asset.OutputFileName = RequestedOutputFileName;
            Asset.OutputLocation = OutputLocation;
            Asset.ExportStatus = ExportStatus;
        }
    }

    public ObservableCollection<ImageAsset> Assets { get; } = [];

    public ImageWorkspacePage()
    {
        InitializeComponent();
        RefreshSummary();
        SyncEditorFromSelection();
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var windowWidth = XamlRoot?.Size.Width ?? e.NewSize.Width + 64;
        var singleColumn = windowWidth < 450;

        SettingsColumn.Width = singleColumn
            ? new GridLength(0)
            : new GridLength(280);
        WorkspaceGrid.ColumnSpacing = singleColumn ? 0 : 8;
        WorkspaceGrid.RowSpacing = 8;
        Grid.SetColumn(SettingsPanel, singleColumn ? 0 : 1);
        Grid.SetRow(SettingsPanel, singleColumn ? 1 : 0);
        SettingsPanel.MaxHeight = singleColumn ? 540 : double.PositiveInfinity;

        PageRoot.Padding = new Thickness(10, 6, 10, 10);
        FontSize = 12;
        SettingsPanel.Padding = new Thickness(10);
        SettingsPanel.RowSpacing = 8;
        SettingsPanel.CornerRadius = new CornerRadius(10);
        SettingsScrollViewer.Padding = new Thickness(0, 0, 6, 0);
        SettingsFormPanel.Spacing = 7;
        SettingsActionsPanel.Spacing = 7;
        ImportPanel.Margin = new Thickness(4);
        ImportPanel.Padding = new Thickness(10);
        SummaryPanel.Padding = new Thickness(8, 6, 8, 6);
        SummaryPanel.ColumnSpacing = 8;
        ImportSummaryText.Visibility = Visibility.Collapsed;

        FormatBox.Height = 34;
        SizeModeBox.Height = 34;
        BackgroundBox.Height = 34;
        WidthBox.Height = 34;
        HeightBox.Height = 34;
        FileNameBox.Height = 34;
        OutputLocationBox.Height = 34;
        ChooseOutputLocationButton.Height = 34;
        QualityBox.Height = 34;
        CompressionLevelBox.Height = 34;
    }

    private async void OnImportFilesClick(object sender, RoutedEventArgs e)
        => await PickAndImportFilesAsync();

    private async Task PickAndImportFilesAsync()
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        await ImportFilesAsync(await picker.PickMultipleFilesAsync());
    }

    private async void OnImportFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) await ImportFolderAsync(folder, RecursiveCheckBox.IsChecked == true);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            var looseFiles = new List<StorageFile>();
            foreach (var item in await e.DataView.GetStorageItemsAsync())
            {
                if (item is StorageFile file) looseFiles.Add(file);
                if (item is StorageFolder folder) await ImportFolderAsync(folder, RecursiveCheckBox.IsChecked == true);
            }
            await ImportFilesAsync(looseFiles);
        }
        finally { deferral.Complete(); }
    }

    public async Task ImportDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        var looseFiles = new List<StorageFile>();
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                    await ImportFolderAsync(await StorageFolder.GetFolderFromPathAsync(path), RecursiveCheckBox.IsChecked == true);
                else if (File.Exists(path))
                    looseFiles.Add(await StorageFile.GetFileFromPathAsync(path));
            }
            catch (Exception ex)
            {
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.Message = $"无法导入“{Path.GetFileName(path)}”：{ex.Message}";
            }
        }
        await ImportFilesAsync(looseFiles);
    }

    private async Task ImportFolderAsync(StorageFolder folder, bool recursive)
    {
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = $"正在扫描文件夹：{folder.Path}";
        await ImportFilesAsync(await folder.GetFilesAsync());
        if (!recursive) return;
        foreach (var child in await folder.GetFoldersAsync()) await ImportFolderAsync(child, true);
    }

    private async Task ImportFilesAsync(IEnumerable<StorageFile> files)
    {
        var fileList = files.ToList();
        if (fileList.Count == 0)
        {
            if (Assets.Count == 0)
            {
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.Message = "没有发现可读取的文件。";
            }
            return;
        }
        if (!FfmpegLocator.TryFindExecutable("ffprobe", out _))
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = FfmpegLocator.MissingMessage("ffprobe");
            return;
        }
        if (!FfmpegLocator.TryFindExecutable("ffmpeg", out _))
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = $"{FfmpegLocator.MissingMessage("ffmpeg")} 图片兼容读取和导出功能不可用。";
            return;
        }
        var processed = 0;
        var added = 0;
        var readFailed = 0;
        var unsupported = 0;
        var duplicates = 0;
        foreach (var file in fileList)
        {
            processed++;
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = $"正在读取 {processed}/{fileList.Count}：{file.Name}";
            var normalizedPath = Path.GetFullPath(file.Path);
            if (!_knownPaths.Add(normalizedPath))
            {
                duplicates++;
                continue;
            }

            var asset = await _importService.ReadAsync(file);
            if (asset.ReadStatus == "就绪")
            {
                if (_presetStore.TryGetImageImportDefault(out var importPreset) && importPreset is not null)
                    importPreset.ApplyTo(asset);
                Assets.Add(asset);
                added++;
            }
            else
            {
                _knownPaths.Remove(normalizedPath);
                if (asset.ReadStatus.StartsWith("不支持", StringComparison.Ordinal)) unsupported++;
                else readFailed++;
            }
        }

        RefreshSummary();
        StatusBar.Severity = readFailed > 0 || unsupported > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        StatusBar.Message = $"导入完成：新增 {added} 张图片；忽略不支持 {unsupported}，读取失败 {readFailed}，重复 {duplicates}。";
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
        => SelectAllAssets();

    private void SelectAllAssets()
    {
        AssetList.SelectedItems.Clear();
        foreach (var asset in Assets) AssetList.SelectedItems.Add(asset);
        RefreshSummary();
    }

    private void OnClearSelectionClick(object sender, RoutedEventArgs e)
    {
        AssetList.SelectedItems.Clear();
        RefreshSummary();
    }

    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
        => DeleteSelectedAssets();

    private void DeleteSelectedAssets()
    {
        var selected = AssetList.SelectedItems.OfType<ImageAsset>().ToList();
        if (selected.Count == 0)
        {
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = "请先选择要从列表移除的素材。";
            return;
        }

        var removed = selected
            .Select(asset => new RemovedImageAsset(asset, Assets.IndexOf(asset)))
            .OrderBy(item => item.Index)
            .ToList();
        foreach (var item in removed)
        {
            Assets.Remove(item.Asset);
            _knownPaths.Remove(Path.GetFullPath(item.Asset.FilePath));
        }
        _undoActions.Push(() => RestoreRemovedAssets(removed));
        RefreshSummary();
        SyncEditorFromSelection();
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = $"已从列表移除 {selected.Count} 个素材；源文件未被删除。";
    }

    private void RestoreRemovedAssets(IReadOnlyList<RemovedImageAsset> removed)
    {
        var restored = new List<ImageAsset>();
        foreach (var item in removed)
        {
            var fullPath = Path.GetFullPath(item.Asset.FilePath);
            if (_knownPaths.Contains(fullPath)) continue;
            Assets.Insert(Math.Clamp(item.Index, 0, Assets.Count), item.Asset);
            _knownPaths.Add(fullPath);
            restored.Add(item.Asset);
        }

        AssetList.SelectedItems.Clear();
        foreach (var asset in restored) AssetList.SelectedItems.Add(asset);
        RefreshSummary();
        SyncEditorFromSelection();
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = restored.Count == 0
            ? "无法恢复：这些素材已经重新导入。"
            : $"已撤销移除，恢复 {restored.Count} 个素材。";
    }

    private async void OnOpenAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PickAndImportFilesAsync();
    }

    private void OnSelectAllAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextEditingControlFocused()) return;
        args.Handled = true;
        SelectAllAssets();
    }

    private void OnDeleteAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsFocusWithinAssetList()) return;
        args.Handled = true;
        DeleteSelectedAssets();
    }

    private void OnUndoAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextEditingControlFocused()) return;
        args.Handled = true;
        if (_undoActions.TryUndo()) return;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = "没有可撤销的操作。";
    }

    private bool IsTextEditingControlFocused()
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        return focused is TextBox or NumberBox or PasswordBox;
    }

    private bool IsFocusWithinAssetList()
    {
        var current = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (current is not null)
        {
            if (ReferenceEquals(current, AssetList)) return true;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void OnAssetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSummary();
        SyncEditorFromSelection();
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingForm) return;
        var value = GetComboText(FormatBox);
        if (value is not null)
        {
            ApplyToSelected(asset =>
            {
                asset.OutputFormat = value;
                if (value == "WebP" && asset.CompressionLevel > 6) asset.CompressionLevel = 6;
            });
        }
        SyncEditorFromSelection();
    }

    private void OnSizeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = GetComboText(SizeModeBox);
        if (value is not null) ApplyToSelected(asset => asset.SizeMode = value);
        SyncDimensionEnabledState();
    }

    private void OnWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        ApplyValidatedNumber(sender, args.NewValue, 1, 16384, "宽度必须在 1 到 16384 之间。", asset => asset.OutputWidth = Math.Round(args.NewValue));
    }

    private void OnHeightChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        ApplyValidatedNumber(sender, args.NewValue, 1, 16384, "高度必须在 1 到 16384 之间。", asset => asset.OutputHeight = Math.Round(args.NewValue));
    }

    private void OnQualityChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        ApplyValidatedNumber(sender, args.NewValue, 1, 100, "质量必须在 1 到 100 之间。", asset => asset.Quality = Math.Round(args.NewValue));
    }

    private void OnCompressionLevelChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        var selected = GetReadySelectedAssets();
        var maximum = selected.Any(asset => asset.OutputFormat == "WebP") ? 6 : 9;
        ApplyValidatedNumber(sender, args.NewValue, 0, maximum, $"压缩级别必须在 0 到 {maximum} 之间。", asset => asset.CompressionLevel = (int)Math.Round(args.NewValue));
    }

    private void OnNumberBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is NumberBox numberBox) NumberBoxChrome.RemoveClearButton(numberBox);
    }

    private void OnBackgroundChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = GetComboText(BackgroundBox);
        if (value is not null) ApplyToSelected(asset => asset.Background = value);
    }

    private void OnFileNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingForm || FileNameBox.FocusState == FocusState.Unfocused) return;
        var selected = GetReadySelectedAssets();
        if (selected.Count == 0) return;

        var requestedName = FileNameBox.Text.Trim();
        foreach (var asset in selected)
        {
            asset.OutputFileName = selected.Count > 1 && !string.IsNullOrWhiteSpace(requestedName)
                ? BatchOutputFileNameBuilder.Build(requestedName, asset.FileName, BatchOutputExtensions)
                : requestedName;
            asset.MarkOutputDirty();
        }

        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = selected.Count == 1
            ? "右侧改动已写入当前图片。"
            : $"已将“{requestedName}”作为前缀写入 {selected.Count} 张图片，并保留各自原文件名。";
    }

    private async void OnOutputLocationClick(object sender, RoutedEventArgs e)
    {
        var selected = GetReadySelectedAssets();
        if (selected.Count == 0) return;
        var window = MainWindow.AppMainWindow ?? throw new InvalidOperationException("MainWindow 尚未初始化。");
        var ownerWindow = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var initialDirectory = Path.GetDirectoryName(selected[0].FilePath);
        var outputDirectory = await OutputFolderPickerService.PickAsync(ownerWindow, initialDirectory, PickerLocationId.PicturesLibrary);
        if (outputDirectory is null) return;
        foreach (var asset in selected)
        {
            asset.OutputLocation = outputDirectory;
            asset.MarkOutputDirty();
        }
        SyncEditorFromSelection();
    }

    private void OnResetSelectedClick(object sender, RoutedEventArgs e)
        => ApplyDefaultPreset();

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        var presets = _presetStore.GetImagePresets();
        var selected = GetReadySelectedAssets();
        var flyout = new MenuFlyout();

        var defaultItem = new ToggleMenuFlyoutItem
        {
            Text = "默认设置",
            IsChecked = selected.Count > 0
                ? selected.All(ImageConversionPreset.MatchesDefault)
                : _presetStore.ImageImportDefaultId is null
        };
        defaultItem.Click += (_, _) => ApplyDefaultPreset();
        flyout.Items.Add(defaultItem);

        foreach (var preset in presets)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = preset.Name,
                IsChecked = selected.Count > 0
                    ? selected.All(preset.Matches)
                    : _presetStore.ImageImportDefaultId == preset.Id
            };
            item.Click += (_, _) => ApplyImagePreset(preset);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var saveItem = new MenuFlyoutItem { Text = "＋ 将当前参数保存为新预设" };
        saveItem.Click += async (_, _) => await SaveImagePresetAsync();
        flyout.Items.Add(saveItem);

        if (presets.Count > 0)
        {
            var manageItem = new MenuFlyoutSubItem { Text = "管理预设" };
            foreach (var preset in presets)
            {
                var presetItem = new MenuFlyoutSubItem { Text = preset.Name };
                var updateItem = new MenuFlyoutItem { Text = "用当前参数更新" };
                updateItem.Click += async (_, _) => await UpdateImagePresetAsync(preset);
                var renameItem = new MenuFlyoutItem { Text = "重命名" };
                renameItem.Click += async (_, _) => await RenameImagePresetAsync(preset);
                var deleteItem = new MenuFlyoutItem { Text = "删除" };
                deleteItem.Click += async (_, _) => await DeleteImagePresetAsync(preset);
                presetItem.Items.Add(updateItem);
                presetItem.Items.Add(renameItem);
                presetItem.Items.Add(deleteItem);
                manageItem.Items.Add(presetItem);
            }
            flyout.Items.Add(manageItem);
        }

        flyout.ShowAt(PresetButton);
    }

    private void ApplyDefaultPreset()
    {
        var selected = GetReadySelectedAssets();
        if (selected.Count == 0)
        {
            try
            {
                _presetStore.SetImageImportDefault(null);
                StatusBar.Severity = InfoBarSeverity.Success;
                StatusBar.Message = "之后导入的图片将使用默认设置。";
            }
            catch (Exception ex)
            {
                ShowPresetError(ex.Message);
            }
            return;
        }
        var snapshots = selected.Select(ImageOutputSnapshot.Capture).ToList();
        _undoActions.Push(() =>
        {
            foreach (var snapshot in snapshots) snapshot.Restore();
            SyncEditorFromSelection();
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = $"已撤销恢复默认，找回 {snapshots.Count} 张图片的原设置。";
        });
        foreach (var asset in selected) asset.ResetToDefault();
        SyncEditorFromSelection();
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Message = $"已把 {selected.Count} 张当前选中图片恢复为默认输出。";
    }

    private void ApplyImagePreset(ImageConversionPreset preset)
    {
        var selected = GetReadySelectedAssets();
        if (selected.Count == 0)
        {
            try
            {
                _presetStore.SetImageImportDefault(preset.Id);
                StatusBar.Severity = InfoBarSeverity.Success;
                StatusBar.Message = $"之后导入的图片将自动使用“{preset.Name}”。";
            }
            catch (Exception ex)
            {
                ShowPresetError(ex.Message);
            }
            return;
        }

        var snapshots = selected.Select(ImageOutputSnapshot.Capture).ToList();
        _undoActions.Push(() =>
        {
            foreach (var snapshot in snapshots) snapshot.Restore();
            SyncEditorFromSelection();
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = $"已撤销应用预设“{preset.Name}”。";
        });
        foreach (var asset in selected) preset.ApplyTo(asset);
        SyncEditorFromSelection();
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Message = $"已将预设“{preset.Name}”应用到 {selected.Count} 张图片。";
    }

    private async Task SaveImagePresetAsync()
    {
        if (!TryGetUniformImagePresetSource(out var source)) return;
        var name = await PresetDialogService.PromptNameAsync(XamlRoot, "保存图片预设");
        if (name is null) return;
        try
        {
            var preset = _presetStore.AddImagePreset(name, source);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = $"已保存图片预设“{preset.Name}”。";
        }
        catch (Exception ex)
        {
            ShowPresetError(ex.Message);
        }
    }

    private async Task UpdateImagePresetAsync(ImageConversionPreset preset)
    {
        if (!TryGetUniformImagePresetSource(out var source)) return;
        if (!await PresetDialogService.ConfirmAsync(XamlRoot, "更新预设", $"用当前转换参数覆盖“{preset.Name}”？", "更新")) return;
        try
        {
            _presetStore.UpdateImagePreset(preset.Id, source);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = $"已更新图片预设“{preset.Name}”。";
        }
        catch (Exception ex)
        {
            ShowPresetError(ex.Message);
        }
    }

    private async Task RenameImagePresetAsync(ImageConversionPreset preset)
    {
        var name = await PresetDialogService.PromptNameAsync(XamlRoot, "重命名图片预设", preset.Name);
        if (name is null) return;
        try
        {
            _presetStore.RenameImagePreset(preset.Id, name);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = $"已将预设重命名为“{name}”。";
        }
        catch (Exception ex)
        {
            ShowPresetError(ex.Message);
        }
    }

    private async Task DeleteImagePresetAsync(ImageConversionPreset preset)
    {
        if (!await PresetDialogService.ConfirmAsync(XamlRoot, "删除预设", $"确定删除图片预设“{preset.Name}”？此操作不能撤销。", "删除")) return;
        try
        {
            _presetStore.DeleteImagePreset(preset.Id);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = $"已删除图片预设“{preset.Name}”。";
        }
        catch (Exception ex)
        {
            ShowPresetError(ex.Message);
        }
    }

    private bool TryGetUniformImagePresetSource(out ImageAsset source)
    {
        var selected = GetReadySelectedAssets();
        source = selected.FirstOrDefault()!;
        if (selected.Count == 0)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = "请先选择一张或多张图片，再保存当前参数。";
            return false;
        }

        var current = ImageConversionPreset.Capture(string.Empty, source);
        if (selected.Any(asset => !current.Matches(asset)))
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = "当前选中图片的转换参数不一致，请先统一参数或只选择一张图片。";
            return false;
        }
        return true;
    }

    private void ShowPresetError(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Message = $"预设操作失败：{message}";
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var readyAssets = Assets.Where(asset => asset.ReadStatus == "就绪").ToList();
        if (readyAssets.Count == 0)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = "没有可导出的有效图片。";
            return;
        }

        if (!FfmpegLocator.TryFindExecutable("ffmpeg", out var ffmpegPath))
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = FfmpegLocator.MissingMessage("ffmpeg");
            return;
        }

        ExportButton.IsEnabled = false;
        CancelExportButton.IsEnabled = true;
        LocateFailureButton.IsEnabled = false;
        OpenOutputButton.IsEnabled = false;
        _lastOutputDirectories.Clear();
        _exportCancellation?.Dispose();
        _exportCancellation = new CancellationTokenSource();
        var exporter = new ImageExportService(ffmpegPath);
        var success = 0;
        var failed = 0;
        var skipped = 0;
        var cancelled = 0;
        var outputDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        OutputConflictChoice? remainingConflictChoice = null;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = $"开始导出 {readyAssets.Count} 个有效图片任务。";

        var currentIndex = 0;
        foreach (var asset in readyAssets)
        {
            currentIndex++;
            if (_exportCancellation.IsCancellationRequested)
            {
                asset.ExportStatus = "已取消";
                cancelled++;
                continue;
            }
            var validationError = ValidateForExport(asset);
            if (validationError is not null)
            {
                asset.ExportStatus = $"已跳过：{validationError}";
                skipped++;
                continue;
            }

            var outputPath = ImageExportService.BuildRequestedOutputPath(asset);
            var isSourceFile = string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(asset.FilePath), StringComparison.OrdinalIgnoreCase);
            var overwrite = false;
            if (File.Exists(outputPath) || isSourceFile)
            {
                OutputConflictChoice choice;
                if (remainingConflictChoice is { } savedChoice && (!isSourceFile || savedChoice != OutputConflictChoice.Overwrite))
                {
                    choice = savedChoice;
                }
                else
                {
                    var decision = await OutputConflictDialog.ShowAsync(XamlRoot, outputPath, isSourceFile);
                    choice = decision.Choice;
                    if (decision.ApplyToRemaining) remainingConflictChoice = choice;
                }
                if (choice == OutputConflictChoice.Skip)
                {
                    asset.ExportStatus = "已跳过：同名文件";
                    skipped++;
                    continue;
                }

                if (choice == OutputConflictChoice.Rename)
                    outputPath = ImageExportService.BuildUniqueOutputPath(asset, ImageExportService.ResolveOutputDirectory(asset));
                else
                    overwrite = true;
            }

            asset.ExportStatus = $"转换中（{currentIndex}/{readyAssets.Count}）";
            StatusBar.Message = $"正在导出 {currentIndex}/{readyAssets.Count}：{asset.FileName}";
            var result = await exporter.ExportAsync(asset, outputPath, overwrite, _exportCancellation.Token);
            if (result.Success)
            {
                asset.ExportStatus = $"已完成：{Path.GetFileName(result.OutputPath)} · {FormatFileSize(result.OutputPath)}";
                var directory = Path.GetDirectoryName(result.OutputPath);
                if (!string.IsNullOrWhiteSpace(directory)) { outputDirectories.Add(directory); _lastOutputDirectories.Add(directory); }
                success++;
            }
            else if (result.IsCancelled)
            {
                asset.ExportStatus = "已取消";
                cancelled++;
            }
            else
            {
                asset.ExportStatus = $"失败：{result.ErrorMessage}";
                failed++;
            }
        }

        ExportButton.IsEnabled = readyAssets.Count > 0;
        CancelExportButton.IsEnabled = false;
        OpenOutputButton.IsEnabled = _lastOutputDirectories.Count > 0;
        LocateFailureButton.IsEnabled = failed > 0 || skipped > 0;
        var outputSummary = outputDirectories.Count == 0
            ? "无成功输出"
            : string.Join("；", outputDirectories.Take(3));
        StatusBar.Severity = failed > 0 || skipped > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        StatusBar.Message = $"导出完成：成功 {success}，失败 {failed}，跳过 {skipped}，取消 {cancelled}。输出位置：{outputSummary}";
    }

    private void OnCancelExportClick(object sender, RoutedEventArgs e)
    {
        _exportCancellation?.Cancel();
        CancelExportButton.IsEnabled = false;
        StatusBar.Severity = InfoBarSeverity.Warning;
        StatusBar.Message = "正在取消当前转换并停止后续任务…";
    }

    private void OnOpenOutputClick(object sender, RoutedEventArgs e)
    {
        var directory = _lastOutputDirectories.FirstOrDefault(Directory.Exists);
        if (directory is null)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = "输出目录已不存在，请重新导出后再打开。";
            OpenOutputButton.IsEnabled = false;
            return;
        }

        var error = OutputDirectoryLauncher.TryOpen(directory);
        if (error is not null)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = error;
        }
    }

    private void OnLocateFailureClick(object sender, RoutedEventArgs e)
    {
        var asset = Assets.FirstOrDefault(item => item.ExportStatus.StartsWith("失败", StringComparison.Ordinal) || item.ExportStatus.StartsWith("已跳过", StringComparison.Ordinal));
        if (asset is null) return;
        AssetList.SelectedItems.Clear();
        AssetList.SelectedItem = asset;
        AssetList.ScrollIntoView(asset);
        AssetList.Focus(FocusState.Programmatic);
    }

    private ImportSummary GetImportSummary() => new(Assets.Count, Assets.Count, 0, 0, 0);

    private void RefreshSummary()
    {
        var summary = GetImportSummary();
        SelectionSummaryText.Text = $"已选择 {AssetList.SelectedItems.Count} / {Assets.Count}";
        TaskSummaryText.Text = $"有效输出任务 {summary.ValidCount}";
        ImportSummaryText.Text = $"总数 {summary.TotalCount}  有效 {summary.ValidCount}  读取失败 {summary.ReadFailedCount}  不支持 {summary.UnsupportedCount}  重复 {summary.DuplicateCount}";
        EmptyStateText.Visibility = Assets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = summary.ValidCount > 0;
        var readySelected = AssetList.SelectedItems.OfType<ImageAsset>().Count(asset => asset.ReadStatus == "就绪");
        var editorScope = readySelected switch { 0 => "没有选择素材；选择预设将设为后续导入默认", 1 => $"正在编辑：{AssetList.SelectedItems.OfType<ImageAsset>().First(asset => asset.ReadStatus == "就绪").FileName}", _ => $"正在批量编辑：{readySelected} 张" };
        ToolTipService.SetToolTip(PresetButton, editorScope);
    }

    private void SyncEditorFromSelection()
    {
        _syncingForm = true;
        var selected = GetReadySelectedAssets();
        var hasSelection = selected.Count > 0;

        FormatBox.IsEnabled = hasSelection;
        SizeModeBox.IsEnabled = hasSelection;
        BackgroundBox.IsEnabled = hasSelection;
        FileNameBox.IsEnabled = hasSelection;
        OutputLocationBox.IsEnabled = hasSelection;
        ChooseOutputLocationButton.IsEnabled = hasSelection;
        ResetSelectedButton.IsEnabled = hasSelection;
        SyncFormatParameterState(selected);

        if (!hasSelection)
        {
            SetComboSelection(FormatBox, "WebP");
            SetComboSelection(SizeModeBox, "原尺寸");
            SetComboSelection(BackgroundBox, "保留透明度");
            WidthBox.Value = double.NaN;
            HeightBox.Value = double.NaN;
            QualityBox.Value = 85;
            CompressionLevelBox.Value = 6;
            FileNameBox.Text = string.Empty;
            FileNameBox.PlaceholderText = "默认按原文件名生成";
            OutputLocationBox.Text = "与源文件同目录";
        }
        else
        {
            SetComboSelection(FormatBox, CommonValue(selected, asset => asset.OutputFormat));
            SetComboSelection(SizeModeBox, CommonValue(selected, asset => asset.SizeMode));
            SetComboSelection(BackgroundBox, CommonValue(selected, asset => asset.Background));
            WidthBox.Value = CommonNumber(selected, asset => asset.OutputWidth);
            HeightBox.Value = CommonNumber(selected, asset => asset.OutputHeight);
            QualityBox.Value = CommonNumber(selected, asset => asset.Quality);
            CompressionLevelBox.Value = CommonNumber(selected, asset => asset.CompressionLevel);

            var fileName = CommonValue(selected, asset => asset.OutputFileName);
            FileNameBox.Text = fileName ?? string.Empty;
            FileNameBox.PlaceholderText = fileName is null
                ? selected.Count > 1 ? "多个不同名称（修改其他字段会保留）" : "多个值"
                : "默认按原文件名生成";
            var outputLocation = CommonValue(selected, asset => asset.OutputLocation);
            OutputLocationBox.Text = outputLocation ?? string.Empty;
            OutputLocationBox.PlaceholderText = outputLocation is null ? "多个值" : "点击选择输出文件夹";
        }

        SyncDimensionEnabledState();
        _syncingForm = false;
    }

    private void SyncFormatParameterState(IReadOnlyCollection<ImageAsset> selected)
    {
        var hasSelection = selected.Count > 0;
        var format = hasSelection ? CommonValue(selected, asset => asset.OutputFormat) : "WebP";
        var showQuality = format != "PNG";
        var showCompression = format != "JPG";
        var containsJpg = hasSelection && selected.Any(asset => asset.OutputFormat == "JPG");

        QualityField.Visibility = showQuality ? Visibility.Visible : Visibility.Collapsed;
        CompressionLevelField.Visibility = showCompression ? Visibility.Visible : Visibility.Collapsed;
        QualityBox.IsEnabled = hasSelection && showQuality;
        CompressionLevelBox.IsEnabled = hasSelection && showCompression;
        TransparentBackgroundItem.IsEnabled = !containsJpg;
        if (!showQuality) QualityBox.Header = null;
        if (!showCompression) CompressionLevelBox.Header = null;

        Grid.SetColumn(QualityField, 1);
        Grid.SetColumnSpan(QualityField, format == "JPG" ? 2 : 1);
        Grid.SetColumn(CompressionLevelField, format == "PNG" ? 1 : 2);
        Grid.SetColumnSpan(CompressionLevelField, format == "PNG" ? 2 : 1);

        var containsWebP = !hasSelection || selected.Any(asset => asset.OutputFormat == "WebP");
        CompressionLevelBox.Maximum = containsWebP ? 6 : 9;
    }

    private void SyncDimensionEnabledState()
    {
        var hasSelection = GetReadySelectedAssets().Count > 0;
        var mode = GetComboText(SizeModeBox);
        WidthBox.IsEnabled = hasSelection && mode != "原尺寸";
        HeightBox.IsEnabled = hasSelection && mode is "指定画布宽高，包含完整图片" or "指定画布宽高，裁切填满";
    }

    private void ApplyValidatedNumber(NumberBox field, double value, double minimum, double maximum, string error, Action<ImageAsset> apply)
    {
        if (_syncingForm || double.IsNaN(value)) return;
        if (value < minimum || value > maximum)
        {
            field.Header = CreateFieldError(error);
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = error;
            return;
        }
        field.Header = null;
        ApplyToSelected(apply);
    }

    private static TextBlock CreateFieldError(string message) => new()
    {
        Text = message,
        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 113, 113)),
        FontSize = 12
    };

    private void ApplyToSelected(Action<ImageAsset> apply)
    {
        if (_syncingForm) return;
        var selected = GetReadySelectedAssets();
        if (selected.Count == 0) return;
        foreach (var asset in selected)
        {
            apply(asset);
            asset.MarkOutputDirty();
        }
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = selected.Count == 1
            ? "右侧改动已写入当前图片。"
            : $"右侧改动已写入当前选中的 {selected.Count} 张图片；未改字段保持各自原值。";
    }

    private List<ImageAsset> GetReadySelectedAssets() => AssetList.SelectedItems
        .OfType<ImageAsset>()
        .Where(asset => asset.ReadStatus == "就绪")
        .ToList();

    private static string? CommonValue(IEnumerable<ImageAsset> assets, Func<ImageAsset, string> selector)
    {
        string? common = null;
        var hasValue = false;
        foreach (var asset in assets)
        {
            var value = selector(asset);
            if (!hasValue) { common = value; hasValue = true; }
            else if (!string.Equals(common, value, StringComparison.Ordinal)) return null;
        }
        return common;
    }

    private static double CommonNumber(IEnumerable<ImageAsset> assets, Func<ImageAsset, double> selector)
    {
        double? common = null;
        foreach (var asset in assets)
        {
            var value = selector(asset);
            if (common is null) common = value;
            else if (Math.Abs(common.Value - value) > 0.01) return double.NaN;
        }
        return common ?? double.NaN;
    }

    private static string? GetComboText(ComboBox comboBox)
    {
        var item = comboBox.SelectedItem as ComboBoxItem;
        return item?.Tag?.ToString() ?? item?.Content?.ToString();
    }

    private static void SetComboSelection(ComboBox comboBox, string? value)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString() ?? item.Content?.ToString(), value, StringComparison.Ordinal));
        comboBox.PlaceholderText = value is null ? "多个值" : string.Empty;
    }

    private static void InitializePicker(object picker)
    {
        var window = MainWindow.AppMainWindow ?? throw new InvalidOperationException("MainWindow 尚未初始化。");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private static string? ValidateForExport(ImageAsset asset)
    {
        if (!File.Exists(asset.FilePath)) return "源文件不存在";
        if (asset.OutputFormat is not ("WebP" or "JPG" or "PNG")) return "输出格式无效";
        var (width, height) = asset.GetTargetSize();
        if (width is < 1 or > 16384 || height is < 1 or > 16384) return "尺寸超出范围";
        if (asset.OutputFormat != "PNG" && (asset.Quality < 1 || asset.Quality > 100)) return "质量超出范围";
        if (asset.OutputFormat == "WebP" && (asset.CompressionLevel < 0 || asset.CompressionLevel > 6)) return "WebP 压缩级别超出范围";
        if (asset.OutputFormat == "PNG" && (asset.CompressionLevel < 0 || asset.CompressionLevel > 9)) return "PNG 压缩级别超出范围";
        if (asset.OutputFormat == "JPG" && asset.Background == "保留透明度")
            return "JPG 不支持保留透明度，请选择背景色";
        var fileNameError = ExportPathValidator.ValidateFileName(asset.RequestedOutputFileName, asset.OutputFileName);
        if (fileNameError is not null) return fileNameError;
        var outputDirectory = ImageExportService.ResolveOutputDirectory(asset);
        return ExportPathValidator.ValidateDirectory(outputDirectory);
    }

    private static string FormatFileSize(string path)
    {
        var bytes = new FileInfo(path).Length;
        return bytes switch
        {
            >= 1024L * 1024L * 1024L => $"{bytes / 1024d / 1024d / 1024d:0.0} GB",
            >= 1024L * 1024L => $"{bytes / 1024d / 1024d:0.0} MB",
            >= 1024L => $"{bytes / 1024d:0} KB",
            _ => $"{bytes} B"
        };
    }
}
