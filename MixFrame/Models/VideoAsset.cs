using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MixFrame.Models;

public sealed class VideoAsset : INotifyPropertyChanged
{
    private string _exportStatus = "等待中";
    private string _outputFormat = "动态 WebP";
    private string _sizeMode = "原尺寸";
    private int? _outputWidth;
    private int _fps = 25;
    private int _quality = 70;
    private int _compressionLevel = 6;
    private string _outputFileName = string.Empty;
    private string _outputDirectory = "与源文件同目录";
    private string _estimatedOutputSizeText = "转换后确定";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContainerFormat { get; set; } = string.Empty;
    public string VideoCodec { get; set; } = string.Empty;
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public double SourceFps { get; set; }
    public TimeSpan Duration { get; set; }
    public long SourceBytes { get; set; }
    public bool HasAlpha { get; set; }
    public bool IsAnimatedWebP { get; set; }
    public int FrameCount { get; set; }
    public string ReadStatus { get; set; } = "就绪";
    public string? ErrorMessage { get; set; }
    public BitmapImage? Thumbnail { get; set; }

    public string OutputFormat
    {
        get => _outputFormat;
        set
        {
            if (!SetField(ref _outputFormat, value)) return;
            if (!string.IsNullOrWhiteSpace(_outputFileName))
                _outputFileName = Path.ChangeExtension(_outputFileName, OutputExtension);
            OnPropertyChanged(nameof(OutputFileName));
            RefreshOutput();
        }
    }

    public string SizeMode { get => _sizeMode; set { if (SetField(ref _sizeMode, value)) RefreshOutput(); } }
    public int? OutputWidth { get => _outputWidth; set { if (SetField(ref _outputWidth, value)) RefreshOutput(); } }
    public int Fps { get => _fps; set { if (SetField(ref _fps, value)) RefreshOutput(); } }
    public int Quality { get => _quality; set { if (SetField(ref _quality, value)) RefreshOutput(); } }
    public int CompressionLevel { get => _compressionLevel; set { if (SetField(ref _compressionLevel, value)) RefreshOutput(); } }
    public string OutputFileName
    {
        get => string.IsNullOrWhiteSpace(_outputFileName) ? $"{Path.GetFileNameWithoutExtension(FileName)}.{OutputExtension}" : _outputFileName;
        set
        {
            if (SetField(ref _outputFileName, value)) RefreshOutput();
        }
    }
    public string RequestedOutputFileName => _outputFileName;
    public string OutputDirectory { get => _outputDirectory; set { if (SetField(ref _outputDirectory, value)) RefreshOutput(); } }

    public string ExportStatus
    {
        get => _exportStatus;
        set { if (_exportStatus != value) { _exportStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(OutputSummary)); } }
    }

    public string SourceSummary => ReadStatus == "就绪"
        ? $"{ContainerFormat} / {VideoCodec} · {PixelWidth}×{PixelHeight} · {SourceFps:0.##}fps{FrameCountText}{AlphaText} · {FormatDuration(Duration)} · {FormatBytes(SourceBytes)}"
        : ReadStatus;

    public string DurationText => FormatDuration(Duration);

    public string EffectiveOutputFileName => Path.ChangeExtension(OutputFileName, OutputExtension);

    public string OutputSummary => ReadStatus == "就绪"
        ? $"{OutputFormat} · {TargetSizeText} · {Fps}fps · {EncodingSettingsSummary} · {EffectiveOutputFileName} · {OutputDirectory} · {ExportStatus}"
        : ReadStatus;

    public string EstimateSummary => ReadStatus == "就绪"
        ? $"{TargetSizeText} · {Fps}fps · {EstimatedOutputSizeText}"
        : "无法估算";

    private string EncodingSettingsSummary => OutputFormat switch
    {
        "动态 WebP" => $"Q{Quality} · C{CompressionLevel}{(HasAlpha ? " · 保留透明" : string.Empty)}",
        "GIF" => "调色板优化",
        _ => $"Q{Quality}"
    };

    public string TargetSizeText
    {
        get
        {
            var (width, height) = GetTargetSize();
            return $"{width}×{height}";
        }
    }

    public string EstimatedOutputSizeText => _estimatedOutputSizeText;

    public (int Width, int Height) GetTargetSize()
    {
        if (SizeMode == "原尺寸" || OutputWidth is null or <= 0 || PixelWidth <= 0) return (PixelWidth, PixelHeight);
        return (OutputWidth.Value, Math.Max(1, (int)Math.Round(PixelHeight * (OutputWidth.Value / (double)PixelWidth))));
    }

    public void ResetToDefault()
    {
        OutputFormat = "动态 WebP";
        SizeMode = "原尺寸";
        OutputWidth = PixelWidth;
        Fps = IsAnimatedWebP && SourceFps > 0
            ? Math.Clamp((int)Math.Round(SourceFps), 1, 120)
            : 25;
        Quality = 70;
        CompressionLevel = 6;
        _outputFileName = string.Empty;
        OutputDirectory = "与源文件同目录";
        ExportStatus = "等待中";
        RefreshOutput();
        OnPropertyChanged(nameof(OutputFileName));
    }

    public void MarkOutputDirty() => ExportStatus = "等待中";

    public void SetEstimatedOutputSizeText(string value)
    {
        _estimatedOutputSizeText = string.IsNullOrWhiteSpace(value) ? "转换后确定" : value;
        OnPropertyChanged(nameof(EstimatedOutputSizeText));
        OnPropertyChanged(nameof(EstimateSummary));
    }

    private string OutputExtension => OutputFormat switch { "MP4" => "mp4", "GIF" => "gif", "WebM" => "webm", _ => "webp" };

    private string FrameCountText => FrameCount > 0 ? $" · {FrameCount}帧" : string.Empty;
    private string AlphaText => HasAlpha ? " · 透明" : string.Empty;

    private void RefreshOutput()
    {
        _estimatedOutputSizeText = "转换后确定";
        OnPropertyChanged(nameof(EffectiveOutputFileName));
        OnPropertyChanged(nameof(OutputSummary));
        OnPropertyChanged(nameof(TargetSizeText));
        OnPropertyChanged(nameof(EstimatedOutputSizeText));
        OnPropertyChanged(nameof(EstimateSummary));
    }

    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"hh\:mm\:ss")
        : value.ToString(@"mm\:ss");

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L * 1024L => $"{bytes / 1024d / 1024d / 1024d:0.0} GB",
        >= 1024L * 1024L => $"{bytes / 1024d / 1024d:0.0} MB",
        >= 1024L => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
