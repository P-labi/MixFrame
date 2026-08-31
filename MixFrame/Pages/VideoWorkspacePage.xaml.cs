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

public sealed partial class VideoWorkspacePage : Page, IDroppedPathHandler
{
    private static readonly IReadOnlySet<string> BatchOutputExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".webp", ".mp4", ".gif", ".webm" };
    private readonly VideoImportService _importService = new();
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingForm;
    private CancellationTokenSource? _exportCancellation;
    private CancellationTokenSource? _estimateCancellation;
    private readonly HashSet<string> _lastOutputDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly UndoActionStack _undoActions = new();
    private readonly PresetStoreService _presetStore = PresetStoreService.Instance;

    private sealed record RemovedVideoAsset(VideoAsset Asset, int Index);

    private sealed record VideoOutputSnapshot(
        VideoAsset Asset,
        string OutputFormat,
        string SizeMode,
        int? OutputWidth,
        int Fps,
        int Quality,
        int CompressionLevel,
        string RequestedOutputFileName,
        string OutputDirectory,
        string ExportStatus)
    {
        public static VideoOutputSnapshot Capture(VideoAsset asset) => new(
            asset,
            asset.OutputFormat,
            asset.SizeMode,
            asset.OutputWidth,
            asset.Fps,
            asset.Quality,
            asset.CompressionLevel,
            asset.RequestedOutputFileName,
            asset.OutputDirectory,
            asset.ExportStatus);

        public void Restore()
        {
            Asset.OutputFormat = OutputFormat;
            Asset.SizeMode = SizeMode;
            Asset.OutputWidth = OutputWidth;
            Asset.Fps = Fps;
            Asset.Quality = Quality;
            Asset.CompressionLevel = CompressionLevel;
            Asset.OutputFileName = RequestedOutputFileName;
            Asset.OutputDirectory = OutputDirectory;
            Asset.ExportStatus = ExportStatus;
        }
    }

    public ObservableCollection<VideoAsset> Assets { get; } = [];

    public VideoWorkspacePage()
    {
        InitializeComponent();
        RefreshSummary();
        SyncEditorFromSelection();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => CancelAutomaticEstimates();

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
        FpsBox.Height = 34;
        QualityBox.Height = 34;
        CompressionBox.Height = 34;
        FileNameBox.Height = 34;
        OutputLocationBox.Height = 34;
        ChooseOutputLocationButton.Height = 34;
        WidthBox.Height = 34;
    }

    private async void OnImportFilesClick(object sender, RoutedEventArgs e)
        => await PickAndImportFilesAsync();

    private async Task PickAndImportFilesAsync()
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        await ImportFilesAsync(await picker.PickMultipleFilesAsync());
    }

    private async void OnImportFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
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
            var files = new List<StorageFile>();
            foreach (var item in await e.DataView.GetStorageItemsAsync())
            {
                if (item is StorageFile file) files.Add(file);
                if (item is StorageFolder folder) await ImportFolderAsync(folder, RecursiveCheckBox.IsChecked == true);
            }
            await ImportFilesAsync(files);
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
            StatusBar.Message = $"{FfmpegLocator.MissingMessage("ffmpeg")} 视频缩略图和导出功能不可用。";
            return;
        }
        var processed = 0;
        var added = 0;
        var readFailed = 0;
        var unsupported = 0;
        var duplicates = 0;
        var addedAssets = new List<VideoAsset>();
        foreach (var file in fileList)
        {
            processed++;
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = $"正在读取 {processed}/{fileList.Count}：{file.Name}";
            var path = Path.GetFullPath(file.Path);
            if (!_knownPaths.Add(path)) { duplicates++; continue; }

            var asset = await _importService.ReadAsync(file);
            if (asset.ReadStatus == "就绪")
            {
                if (_presetStore.TryGetVideoImportDefault(out var importPreset)
                    && importPreset is not null
                    && importPreset.CanApplyTo(asset))
                    importPreset.ApplyTo(asset);
                Assets.Add(asset);
                addedAssets.Add(asset);
                added++;
            }
            else
            {
                _knownPaths.Remove(path);
                if (asset.ReadStatus.StartsWith("不支持", StringComparison.Ordinal)) unsupported++;
                else readFailed++;
            }
        }

        RefreshSummary();
        StatusBar.Severity = readFailed > 0 || unsupported > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        StatusBar.Message = $"导入完成：新增 {added} 个视频；忽略不支持 {unsupported}，读取失败 {readFailed}，重复 {duplicates}。";
        ScheduleAutomaticEstimates(addedAssets);
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
        => SelectAllAssets();

    private void SelectAllAssets()
    {
        AssetList.SelectedItems.Clear();
        foreach (var asset in Assets) AssetList.SelectedItems.Add(asset);
        RefreshSummary();
    }
    private void OnClearSelectionClick(object sender, RoutedEventArgs e) { AssetList.SelectedItems.Clear(); RefreshSummary(); }
    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
        => DeleteSelectedAssets();

    private void DeleteSelectedAssets()
    {
        var selected = AssetList.SelectedItems.OfType<VideoAsset>().ToList();
        if (selected.Count == 0)
        {
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = "请先选择要从列表移除的素材。";
            return;
        }

        var removed = selected
            .Select(asset => new RemovedVideoAsset(asset, Assets.IndexOf(asset)))
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
        ScheduleAutomaticEstimates([]);
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = $"已从列表移除 {selected.Count} 个素材；源文件未被删除。";
    }

    private void RestoreRemovedAssets(IReadOnlyList<RemovedVideoAsset> removed)
    {
        var restored = new List<VideoAsset>();
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
        ScheduleAutomaticEstimates(restored);
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
        var value = GetComboText(FormatBox);
        if (value is null) return;
        ApplyToSelected(asset =>
        {
            asset.OutputFormat = value;
            if (value == "动态 WebP" && asset.CompressionLevel > 6)
                asset.CompressionLevel = 6;
        });
        SyncEditorFromSelection();
    }

    private void OnSizeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = GetComboText(SizeModeBox);
        if (value is not null) ApplyToSelected(asset => asset.SizeMode = value);
        SyncWidthEnabledState();
    }

    private void OnWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        ApplyValidatedNumber(sender, args.NewValue, 1, 16384, "宽度必须在 1 到 16384 之间。", asset => asset.OutputWidth = (int)Math.Round(args.NewValue));

    private void OnFpsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        ApplyValidatedNumber(sender, args.NewValue, 1, 120, "帧率必须在 1 到 120 之间。", asset => asset.Fps = (int)Math.Round(args.NewValue));

    private void OnQualityChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        ApplyValidatedNumber(sender, args.NewValue, 1, 100, "质量必须在 1 到 100 之间。", asset => asset.Quality = (int)Math.Round(args.NewValue));

    private void OnCompressionChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        ApplyValidatedNumber(sender, args.NewValue, 0, 6, "动态 WebP 压缩等级必须在 0 到 6 之间。", asset => asset.CompressionLevel = (int)Math.Round(args.NewValue));

    private void OnNumberBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is NumberBox numberBox) NumberBoxChrome.RemoveClearButton(numberBox);
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
            ? "右侧改动已写入当前视频。"
            : string.IsNullOrWhiteSpace(requestedName)
                ? $"已将 {selected.Count} 个视频恢复为原文件名。"
                : $"已将“{requestedName}”作为前缀写入 {selected.Count} 个视频，并保留各自原文件名。";
    }

    private async void OnOutputLocationClick(object sender, RoutedEventArgs e)
    {
        var selected = GetReadySelectedAssets();
        if (selected.Count == 0) return;
        var window = MainWindow.AppMainWindow ?? throw new InvalidOperationException("MainWindow 尚未初始化。");
        var ownerWindow = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var initialDirectory = Path.GetDirectoryName(selected[0].FilePath);
        var outputDirectory = await OutputFolderPickerService.PickAsync(ownerWindow, initialDirectory, PickerLocationId.VideosLibrary);
        if (outputDirectory is null) return;
        foreach (var asset in selected)
        {
            asset.OutputDirectory = outputDirectory;
            asset.MarkOutputDirty();
        }
        SyncEditorFromSelection();
    }

    private void OnResetSelectedClick(object sender, RoutedEventArgs e)
        => ApplyDefaultPreset();

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        var presets = _presetStore.GetVideoPresets();
        var selected = GetReadySelectedAssets();
        var flyout = new MenuFlyout();

        var defaultItem = new ToggleMenuFlyoutItem
        {
            Text = "默认设置",
            IsChecked = selected.Count > 0
                ? selected.All(VideoConversionPreset.MatchesDefault)
                : _presetStore.VideoImportDefaultId is null
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
                    : _presetStore.VideoImportDefaultId == preset.Id
            };
            item.Click += (_, _) => ApplyVideoPreset(preset);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var saveItem = new MenuFlyoutItem { Text = "＋ 将当前参数保存为新预设" };
        saveItem.Click += async (_, _) => await SaveVideoPresetAsync();
        flyout.Items.Add(saveItem);

        if (presets.Count > 0)
        {
            var manageItem = new MenuFlyoutSubItem { Text = "管理预设" };
            foreach (var preset in presets)
            {
                var presetItem = new MenuFlyoutSubItem { Text = preset.Name };
                var updateItem = new MenuFlyoutItem { Text = "用当前参数更新" };
                updateItem.Click += async (_, _) => await UpdateVideoPresetAsync(preset);
                var renameItem = new MenuFlyoutItem { Text = "重命名" };
                renameItem.Click += async (_, _) => await RenameVideoPresetAsync(preset);
                var deleteItem = new MenuFlyoutItem { Text = "删除" };
                deleteItem.Click += async (_, _) => await DeleteVideoPresetAsync(preset);
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
                _presetStore.SetVideoImportDefault(null);
                StatusBar.Severity = InfoBarSeverity.Success;
                StatusBar.Message = "之后导入的视频将使用默认动态 WebP 设置。";
            }
            catch (Exception ex)
            {
                ShowPresetError(ex.Message);
            }
            return;
        }
        var snapshots = selected.Select(VideoOutputSnapshot.Capture).ToList();
        _undoActions.Push(() =>
        {
            foreach (var snapshot in snapshots) snapshot.Restore();
            SyncEditorFromSelection();
            ScheduleAutomaticEstimates(snapshots.Select(snapshot => snapshot.Asset));
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = $"已撤销恢复默认，找回 {snapshots.Count} 个视频的原设置。";
        });
        foreach (var asset in selected) asset.ResetToDefault();
        SyncEditorFromSelection();
        ScheduleAutomaticEstimates(selected);
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Message = $"已把 {selected.Count} 个当前选中视频恢复为默认动态 WebP 输出。";
    }

    private void ApplyVideoPreset(VideoConversionPreset preset)
    {
        var selected = GetReadySelectedAssets();
        if (selected.Count == 0)
        {
            try
            {
                _presetStore.SetVideoImportDefault(preset.Id);
                StatusBar.Severity = InfoBarSeverity.Success;
                StatusBar.Message = $"之后导入的视频将自动使用“{preset.Name}”。";
            }
            catch (Exception ex)
            {
                ShowPresetError(ex.Message);
            }
            return;
        }

        if (selected.Any(asset => !preset.CanApplyTo(asset)))
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = "当前选择包含动态 WebP 输入，不能应用输出为 MP4、GIF 或 WebM 的预设。";
            return;
        }

        var snapshots = selected.Select(VideoOutputSnapshot.Capture).ToList();
        _undoActions.Push(() =>
        {
            foreach (var snapshot in snapshots) snapshot.Restore();
            SyncEditorFromSelection();
            ScheduleAutomaticEstimates(snapshots.Select(snapshot => snapshot.Asset));
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = $"已撤销应用预设“{preset.Name}”。";
        });
        foreach (var asset in selected) preset.ApplyTo(asset);
        SyncEditorFromSelection();
        ScheduleAutomaticEstimates(selected);
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Message = $"已将预设“{preset.Name}”应用到 {selected.Count} 个视频。";
    }

    private async Task SaveVideoPresetAsync()
    {
        if (!TryGetUniformVideoPresetSource(out var source)) return;
        var name = await PresetDialogService.PromptNameAsync(XamlRoot, "保存视频预设");
        if (name is null) return;
        try
        {
            var preset = _presetStore.AddVideoPreset(name, source);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = $"已保存视频预设“{preset.Name}”。";
        }
        catch (Exception ex)
        {
            ShowPresetError(ex.Message);
        }
    }

    private async Task UpdateVideoPresetAsync(VideoConversionPreset preset)
    {
        if (!TryGetUniformVideoPresetSource(out var source)) return;
        if (!await PresetDialogService.ConfirmAsync(XamlRoot, "更新预设", $"用当前转换参数覆盖“{preset.Name}”？", "更新")) return;
        try
        {
            _presetStore.UpdateVideoPreset(preset.Id, source);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = $"已更新视频预设“{preset.Name}”。";
        }
        catch (Exception ex)
        {
            ShowPresetError(ex.Message);
        }
    }

    private async Task RenameVideoPresetAsync(VideoConversionPreset preset)
    {
        var name = await PresetDialogService.PromptNameAsync(XamlRoot, "重命名视频预设", preset.Name);
        if (name is null) return;
        try
        {
            _presetStore.RenameVideoPreset(preset.Id, name);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = $"已将预设重命名为“{name}”。";
        }
        catch (Exception ex)
        {
            ShowPresetError(ex.Message);
        }
    }

    private async Task DeleteVideoPresetAsync(VideoConversionPreset preset)
    {
        if (!await PresetDialogService.ConfirmAsync(XamlRoot, "删除预设", $"确定删除视频预设“{preset.Name}”？此操作不能撤销。", "删除")) return;
        try
        {
            _presetStore.DeleteVideoPreset(preset.Id);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = $"已删除视频预设“{preset.Name}”。";
        }
        catch (Exception ex)
        {
            ShowPresetError(ex.Message);
        }
    }

    private bool TryGetUniformVideoPresetSource(out VideoAsset source)
    {
        var selected = GetReadySelectedAssets();
        source = selected.FirstOrDefault()!;
        if (selected.Count == 0)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = "请先选择一个或多个视频，再保存当前参数。";
            return false;
        }

        var current = VideoConversionPreset.Capture(string.Empty, source);
        if (selected.Any(asset => !current.Matches(asset)))
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = "当前选中视频的转换参数不一致，请先统一参数或只选择一个视频。";
            return false;
        }
        return true;
    }

    private void ShowPresetError(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Message = $"预设操作失败：{message}";
    }

    private void ScheduleAutomaticEstimates(IEnumerable<VideoAsset> changedAssets)
    {
        var previous = _estimateCancellation;
        _estimateCancellation = null;
        previous?.Cancel();

        var targets = changedAssets
            .Concat(Assets.Where(NeedsAutomaticEstimate))
            .Where(asset => asset.ReadStatus == "就绪" && File.Exists(asset.FilePath))
            .Distinct()
            .ToList();
        if (targets.Count == 0) return;

        var cancellation = new CancellationTokenSource();
        _estimateCancellation = cancellation;
        foreach (var asset in targets)
            asset.SetEstimatedOutputSizeText("正在预估…");
        _ = RunAutomaticEstimatesAsync(targets, cancellation);
    }

    private async Task RunAutomaticEstimatesAsync(IReadOnlyList<VideoAsset> targets, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellation.Token);
            if (!FfmpegLocator.TryFindExecutable("ffmpeg", out var ffmpegPath))
            {
                if (ReferenceEquals(_estimateCancellation, cancellation))
                    foreach (var asset in targets) asset.SetEstimatedOutputSizeText("转换后确定");
                return;
            }

            var exporter = new VideoExportService(ffmpegPath);
            foreach (var asset in targets)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var signature = BuildEstimateSignature(asset);
                var result = await exporter.EstimateAsync(asset, cancellation.Token);
                if (result.IsCancelled || !ReferenceEquals(_estimateCancellation, cancellation)) return;
                if (!string.Equals(signature, BuildEstimateSignature(asset), StringComparison.Ordinal)) continue;

                asset.SetEstimatedOutputSizeText(result.Success
                    ? $"约 {FormatByteSize(result.MinimumBytes)}–{FormatByteSize(result.MaximumBytes)}"
                    : "转换后确定");
            }
        }
        catch (OperationCanceledException)
        {
            // A newer parameter set replaced this estimate queue.
        }
        catch (Exception)
        {
            if (ReferenceEquals(_estimateCancellation, cancellation))
                foreach (var asset in targets) asset.SetEstimatedOutputSizeText("转换后确定");
        }
        finally
        {
            if (ReferenceEquals(_estimateCancellation, cancellation))
                _estimateCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelAutomaticEstimates()
    {
        var cancellation = _estimateCancellation;
        _estimateCancellation = null;
        cancellation?.Cancel();
        foreach (var asset in Assets.Where(asset => asset.EstimatedOutputSizeText == "正在预估…"))
            asset.SetEstimatedOutputSizeText("转换后确定");
    }

    private static bool NeedsAutomaticEstimate(VideoAsset asset) =>
        !asset.EstimatedOutputSizeText.StartsWith("约 ", StringComparison.Ordinal)
        && !asset.EstimatedOutputSizeText.StartsWith("实际 ", StringComparison.Ordinal);

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        CancelAutomaticEstimates();
        var readyAssets = Assets.Where(asset => asset.ReadStatus == "就绪").ToList();
        if (readyAssets.Count == 0)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = "没有可导出的有效视频。";
            return;
        }

        if (!FfmpegLocator.TryFindExecutable("ffmpeg", out var ffmpegPath))
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = FfmpegLocator.MissingMessage("ffmpeg");
            return;
        }

        var longWebpCount = readyAssets.Count(asset => asset.OutputFormat == "动态 WebP" && asset.Duration > TimeSpan.FromSeconds(10));
        if (longWebpCount > 0)
        {
            var warning = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "动态 WebP 时长警告",
                Content = $"有 {longWebpCount} 个动态 WebP 任务超过 10 秒，文件可能较大、转换耗时可能较长。程序不会截断视频，也不会阻止导出。动态 WebP 将无限循环播放。",
                PrimaryButtonText = "继续导出",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await warning.ShowAsync() != ContentDialogResult.Primary)
            {
                StatusBar.Severity = InfoBarSeverity.Informational;
                StatusBar.Message = "已取消导出。";
                return;
            }
        }

        ExportButton.IsEnabled = false;
        CancelExportButton.IsEnabled = true;
        LocateFailureButton.IsEnabled = false;
        OpenOutputButton.IsEnabled = false;
        _lastOutputDirectories.Clear();
        _exportCancellation?.Dispose();
        _exportCancellation = new CancellationTokenSource();
        var exporter = new VideoExportService(ffmpegPath);
        var success = 0;
        var failed = 0;
        var skipped = 0;
        var cancelled = 0;
        var outputDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        OutputConflictChoice? remainingConflictChoice = null;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = $"开始导出 {readyAssets.Count} 个有效视频任务。";

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

            var outputPath = VideoExportService.BuildRequestedOutputPath(asset);
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
                    outputPath = VideoExportService.BuildUniqueOutputPath(asset, VideoExportService.ResolveOutputDirectory(asset));
                else
                    overwrite = true;
            }

            asset.ExportStatus = $"转换中（{currentIndex}/{readyAssets.Count}）";
            StatusBar.Message = $"正在导出 {currentIndex}/{readyAssets.Count}：{asset.FileName}";
            var result = await exporter.ExportAsync(asset, outputPath, overwrite, _exportCancellation.Token);
            if (result.Success)
            {
                asset.ExportStatus = $"已完成：{Path.GetFileName(result.OutputPath)} · {FormatFileSize(result.OutputPath)}";
                asset.SetEstimatedOutputSizeText($"实际 {FormatFileSize(result.OutputPath)}");
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
        var outputSummary = outputDirectories.Count == 0 ? "无成功输出" : string.Join("；", outputDirectories.Take(3));
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
        TaskSummaryText.Text = $"有效转换任务 {summary.ValidCount}";
        ImportSummaryText.Text = $"总数 {summary.TotalCount}  有效 {summary.ValidCount}  读取失败 {summary.ReadFailedCount}  不支持 {summary.UnsupportedCount}  重复 {summary.DuplicateCount}";
        EmptyStateText.Visibility = Assets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = summary.ValidCount > 0;
        var selected = AssetList.SelectedItems.OfType<VideoAsset>().Where(asset => asset.ReadStatus == "就绪").ToList();
        var editorScope = selected.Count switch
        {
            0 => "没有选择素材；选择预设将设为后续导入默认",
            1 => $"正在编辑：{selected[0].FileName}",
            _ => $"正在批量编辑：{selected.Count} 个"
        };
        ToolTipService.SetToolTip(PresetButton, editorScope);
    }

    private void SyncEditorFromSelection()
    {
        _syncingForm = true;
        var selected = GetReadySelectedAssets();
        var hasSelection = selected.Count > 0;
        FormatBox.IsEnabled = hasSelection;
        SizeModeBox.IsEnabled = hasSelection;
        FpsBox.IsEnabled = hasSelection;
        QualityBox.IsEnabled = hasSelection;
        FileNameBox.IsEnabled = hasSelection;
        OutputLocationBox.IsEnabled = hasSelection;
        ChooseOutputLocationButton.IsEnabled = hasSelection;
        ResetSelectedButton.IsEnabled = hasSelection;

        if (!hasSelection)
        {
            SetComboSelection(FormatBox, "动态 WebP");
            SetComboSelection(SizeModeBox, "原尺寸");
            WidthBox.Value = double.NaN;
            FpsBox.Value = 25;
            QualityBox.Value = 70;
            CompressionBox.Value = 6;
            FileNameBox.Text = string.Empty;
            FileNameBox.PlaceholderText = "默认按原文件名生成";
            OutputLocationBox.Text = "与源文件同目录";
        }
        else
        {
            SetComboSelection(FormatBox, CommonValue(selected, asset => asset.OutputFormat));
            SetComboSelection(SizeModeBox, CommonValue(selected, asset => asset.SizeMode));
            WidthBox.Value = CommonNumber(selected, asset => asset.OutputWidth ?? double.NaN);
            FpsBox.Value = CommonNumber(selected, asset => asset.Fps);
            QualityBox.Value = CommonNumber(selected, asset => asset.Quality);
            CompressionBox.Value = CommonNumber(selected, asset => asset.CompressionLevel);
            var fileName = CommonValue(selected, asset => asset.OutputFileName);
            FileNameBox.Text = fileName ?? string.Empty;
            FileNameBox.PlaceholderText = fileName is null ? "多个不同名称（修改其他字段会保留）" : "默认按原文件名生成";
            var outputLocation = CommonValue(selected, asset => asset.OutputDirectory);
            OutputLocationBox.Text = outputLocation ?? string.Empty;
            OutputLocationBox.PlaceholderText = outputLocation is null ? "多个值" : "点击选择输出文件夹";
        }
        SyncWidthEnabledState();
        SyncFormatSpecificFields();
        _syncingForm = false;
    }

    private void SyncWidthEnabledState() => WidthBox.IsEnabled =
        GetReadySelectedAssets().Count > 0 && GetComboText(SizeModeBox) != "原尺寸";

    private void SyncFormatSpecificFields()
    {
        var selected = GetReadySelectedAssets();
        var hasSelection = selected.Count > 0;
        var allWebp = hasSelection && selected.All(asset => asset.OutputFormat == "动态 WebP");
        var allGif = hasSelection && selected.All(asset => asset.OutputFormat == "GIF");
        var containsAnimatedWebP = selected.Any(asset => asset.IsAnimatedWebP);
        CompressionField.Visibility = allWebp ? Visibility.Visible : Visibility.Collapsed;
        QualityField.Visibility = allGif ? Visibility.Collapsed : Visibility.Visible;
        CompressionBox.IsEnabled = allWebp;
        CompressionBox.Maximum = 6;
        Mp4FormatItem.IsEnabled = !containsAnimatedWebP;
        GifFormatItem.IsEnabled = !containsAnimatedWebP;
        WebMFormatItem.IsEnabled = !containsAnimatedWebP;
    }

    private void ApplyValidatedNumber(NumberBox field, double value, double minimum, double maximum, string error, Action<VideoAsset> apply)
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

    private void ApplyToSelected(Action<VideoAsset> apply)
    {
        if (_syncingForm) return;
        var selected = GetReadySelectedAssets();
        if (selected.Count == 0) return;
        foreach (var asset in selected) { apply(asset); asset.MarkOutputDirty(); }
        ScheduleAutomaticEstimates(selected);
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = selected.Count == 1
            ? "右侧改动已写入当前视频。"
            : $"右侧改动已写入当前选中的 {selected.Count} 个视频；未改字段保持各自原值。";
    }

    private List<VideoAsset> GetReadySelectedAssets() => AssetList.SelectedItems
        .OfType<VideoAsset>().Where(asset => asset.ReadStatus == "就绪").ToList();

    private static string? CommonValue(IEnumerable<VideoAsset> assets, Func<VideoAsset, string> selector)
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

    private static double CommonNumber(IEnumerable<VideoAsset> assets, Func<VideoAsset, double> selector)
    {
        double? common = null;
        foreach (var asset in assets)
        {
            var value = selector(asset);
            if (common is null) common = value;
            else if (double.IsNaN(value) || double.IsNaN(common.Value) || Math.Abs(common.Value - value) > 0.01) return double.NaN;
        }
        return common ?? double.NaN;
    }

    private static string BuildEstimateSignature(VideoAsset asset) => string.Join('|',
        asset.FilePath,
        asset.OutputFormat,
        asset.SizeMode,
        asset.OutputWidth,
        asset.Fps,
        asset.Quality,
        asset.CompressionLevel);

    private static string? GetComboText(ComboBox comboBox) => (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
    private static void SetComboSelection(ComboBox comboBox, string? value)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), value, StringComparison.Ordinal));
        comboBox.PlaceholderText = value is null ? "多个值" : string.Empty;
    }

    private static void InitializePicker(object picker)
    {
        var window = MainWindow.AppMainWindow ?? throw new InvalidOperationException("MainWindow 尚未初始化。");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private static string? ValidateForExport(VideoAsset asset)
    {
        if (!File.Exists(asset.FilePath)) return "源文件不存在";
        if (asset.OutputFormat is not ("动态 WebP" or "MP4" or "GIF" or "WebM")) return "输出格式无效";
        if (asset.IsAnimatedWebP && asset.OutputFormat != "动态 WebP") return "动态 WebP 输入目前仅支持输出为动态 WebP";
        var (width, height) = asset.GetTargetSize();
        if (width is < 1 or > 16384 || height is < 1 or > 16384) return "尺寸超出范围";
        if (asset.Fps is < 1 or > 120) return "帧率超出范围";
        if (asset.Quality is < 1 or > 100) return "质量超出范围";
        if (asset.OutputFormat == "动态 WebP" && asset.CompressionLevel is < 0 or > 6) return "动态 WebP 压缩等级超出范围";
        var fileNameError = ExportPathValidator.ValidateFileName(asset.RequestedOutputFileName, asset.EffectiveOutputFileName);
        if (fileNameError is not null) return fileNameError;
        var directory = VideoExportService.ResolveOutputDirectory(asset);
        return ExportPathValidator.ValidateDirectory(directory);
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

    private static string FormatByteSize(long bytes) => bytes switch
    {
        >= 1024L * 1024L * 1024L => $"{bytes / 1024d / 1024d / 1024d:0.0} GB",
        >= 1024L * 1024L => $"{bytes / 1024d / 1024d:0.0} MB",
        >= 1024L => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };
}
