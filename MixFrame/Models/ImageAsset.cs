using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MixFrame.Models;

public sealed class ImageAsset : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _outputFormat = "WebP";
    private string _sizeMode = "原尺寸";
    private double _outputWidth;
    private double _outputHeight;
    private double _quality = 85;
    private int _compressionLevel = 6;
    private string _background = "保留透明度";
    private string _outputFileName = string.Empty;
    private string _outputLocation = "与源文件同目录";
    private string _exportStatus = "等待中";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SourceFormat { get; set; } = string.Empty;
    public string SourceSize { get; set; } = string.Empty;
    public ulong PixelWidth { get; set; }
    public ulong PixelHeight { get; set; }
    public bool HasAlpha { get; set; }
    public string FileSizeText { get; set; } = string.Empty;
    public double FileSizeMb { get; set; }
    public string ReadStatus { get; set; } = "就绪";
    public BitmapImage? Thumbnail { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string OutputFormat
    {
        get => _outputFormat;
        set
        {
            if (SetField(ref _outputFormat, value))
            {
                if (value == "JPG" && _background == "保留透明度")
                {
                    _background = "#FFFFFF";
                    OnPropertyChanged(nameof(Background));
                }
                if (!string.IsNullOrWhiteSpace(_outputFileName))
                {
                    _outputFileName = Path.ChangeExtension(_outputFileName, GetOutputExtension());
                }
                RefreshDerivedOutput();
            }
        }
    }

    public string SizeMode
    {
        get => _sizeMode;
        set
        {
            if (SetField(ref _sizeMode, value))
            {
                RefreshDerivedOutput();
            }
        }
    }

    public double OutputWidth
    {
        get => _outputWidth;
        set
        {
            if (SetField(ref _outputWidth, value))
            {
                RefreshDerivedOutput();
            }
        }
    }

    public double OutputHeight
    {
        get => _outputHeight;
        set
        {
            if (SetField(ref _outputHeight, value))
            {
                RefreshDerivedOutput();
            }
        }
    }

    public double Quality
    {
        get => _quality;
        set
        {
            if (SetField(ref _quality, value))
            {
                RefreshDerivedOutput();
            }
        }
    }

    public int CompressionLevel
    {
        get => _compressionLevel;
        set
        {
            if (SetField(ref _compressionLevel, value))
            {
                RefreshDerivedOutput();
            }
        }
    }

    public string Background
    {
        get => _background;
        set => SetField(ref _background, value);
    }

    public string OutputFileName
    {
        get => string.IsNullOrWhiteSpace(_outputFileName) ? BuildDefaultOutputFileName() : _outputFileName;
        set
        {
            if (SetField(ref _outputFileName, value)) RefreshDerivedOutput();
        }
    }

    public string OutputLocation
    {
        get => string.IsNullOrWhiteSpace(_outputLocation) ? "与源文件同目录" : _outputLocation;
        set
        {
            if (SetField(ref _outputLocation, value))
            {
                RefreshDerivedOutput();
            }
        }
    }

    public string ExportStatus
    {
        get => _exportStatus;
        set
        {
            if (SetField(ref _exportStatus, value))
            {
                OnPropertyChanged(nameof(OutputSummary));
            }
        }
    }

    public string SourceSummary => ReadStatus == "就绪"
        ? $"{SourceFormat} · {SourceSize} · {FileSizeText}"
        : ReadStatus;

    public string OutputSummary => $"{OutputFormat} · {TargetSizeText} · 约 {EstimatedOutputSizeText} · {OutputFileName} · {OutputLocation} · {ExportStatus}";

    public string TargetSizeText
    {
        get
        {
            var (width, height) = GetTargetSize();
            return $"{width:0}×{height:0}";
        }
    }

    public string EstimatedOutputSizeText
    {
        get
        {
            var (width, height) = GetTargetSize();
            if (ReadStatus != "就绪" || PixelWidth == 0 || PixelHeight == 0)
            {
                return "不可估算";
            }

            var sourceArea = Math.Max(1d, PixelWidth * PixelHeight);
            var targetArea = Math.Max(1, width * height);
            var areaFactor = targetArea / sourceArea;
            var qualityFactor = OutputFormat == "PNG"
                ? 1d
                : Math.Clamp(Quality / 85d, 0.45d, 1.35d);
            var formatFactor = OutputFormat switch
            {
                "WebP" => 0.55d,
                "JPG" => 0.75d,
                "PNG" => 1.25d,
                _ => 0.85d
            };
            var estimatedMb = Math.Max(0.02d, FileSizeMb * areaFactor * qualityFactor * formatFactor);
            return estimatedMb >= 1 ? $"{estimatedMb:0.0} MB" : $"{Math.Max(1, estimatedMb * 1024):0} KB";
        }
    }

    public void ResetToDefault()
    {
        OutputFormat = "WebP";
        SizeMode = "原尺寸";
        OutputWidth = PixelWidth;
        OutputHeight = PixelHeight;
        Quality = 85;
        CompressionLevel = 6;
        Background = "保留透明度";
        _outputFileName = string.Empty;
        OutputLocation = "与源文件同目录";
        ExportStatus = "等待中";
        RefreshDerivedOutput();
        OnPropertyChanged(nameof(OutputFileName));
    }

    public void MarkOutputDirty()
    {
        ExportStatus = "等待中";
    }

    private string BuildDefaultOutputFileName()
    {
        var baseName = Path.GetFileNameWithoutExtension(FileName);
        return $"{baseName}.{GetOutputExtension()}";
    }

    public string RequestedOutputFileName => _outputFileName;

    private string GetOutputExtension() => OutputFormat switch
    {
        "JPG" => "jpg",
        "PNG" => "png",
        _ => "webp"
    };

    public (double Width, double Height) GetTargetSize()
    {
        if (SizeMode == "原尺寸" || OutputWidth <= 0 || PixelWidth == 0)
        {
            return (PixelWidth, PixelHeight);
        }

        if (SizeMode is "指定画布宽高，包含完整图片" or "指定画布宽高，裁切填满")
        {
            return (OutputWidth, OutputHeight > 0 ? OutputHeight : PixelHeight);
        }

        var height = PixelHeight * (OutputWidth / PixelWidth);
        return (OutputWidth, Math.Max(1, height));
    }

    private void RefreshDerivedOutput()
    {
        OnPropertyChanged(nameof(OutputFileName));
        OnPropertyChanged(nameof(TargetSizeText));
        OnPropertyChanged(nameof(EstimatedOutputSizeText));
        OnPropertyChanged(nameof(OutputSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
