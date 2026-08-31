namespace MixFrame.Models;

public sealed record ImageConversionPreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string OutputFormat { get; init; } = "WebP";
    public string SizeMode { get; init; } = "原尺寸";
    public double OutputWidth { get; init; }
    public double OutputHeight { get; init; }
    public double Quality { get; init; } = 85;
    public int CompressionLevel { get; init; } = 6;
    public string Background { get; init; } = "保留透明度";

    public static ImageConversionPreset Capture(string name, ImageAsset asset, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Name = name.Trim(),
        OutputFormat = asset.OutputFormat,
        SizeMode = asset.SizeMode,
        OutputWidth = asset.OutputWidth,
        OutputHeight = asset.OutputHeight,
        Quality = asset.Quality,
        CompressionLevel = asset.CompressionLevel,
        Background = asset.Background
    };

    public void ApplyTo(ImageAsset asset)
    {
        asset.OutputFormat = OutputFormat;
        asset.SizeMode = SizeMode;
        asset.OutputWidth = SizeMode == "原尺寸" ? asset.PixelWidth : OutputWidth;
        asset.OutputHeight = SizeMode == "原尺寸" ? asset.PixelHeight : OutputHeight;
        asset.Quality = Quality;
        asset.CompressionLevel = CompressionLevel;
        asset.Background = OutputFormat == "JPG" && Background == "保留透明度" ? "#FFFFFF" : Background;
        asset.MarkOutputDirty();
    }

    public bool Matches(ImageAsset asset) =>
        asset.OutputFormat == OutputFormat
        && asset.SizeMode == SizeMode
        && (SizeMode == "原尺寸" || NearlyEqual(asset.OutputWidth, OutputWidth))
        && (SizeMode is not ("指定画布宽高，包含完整图片" or "指定画布宽高，裁切填满") || NearlyEqual(asset.OutputHeight, OutputHeight))
        && NearlyEqual(asset.Quality, Quality)
        && asset.CompressionLevel == CompressionLevel
        && asset.Background == Background;

    public static bool MatchesDefault(ImageAsset asset) =>
        asset.OutputFormat == "WebP"
        && asset.SizeMode == "原尺寸"
        && NearlyEqual(asset.Quality, 85)
        && asset.CompressionLevel == 6
        && asset.Background == "保留透明度";

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < 0.01;
}

public sealed record VideoConversionPreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string OutputFormat { get; init; } = "动态 WebP";
    public string SizeMode { get; init; } = "原尺寸";
    public int? OutputWidth { get; init; }
    public int Fps { get; init; } = 25;
    public int Quality { get; init; } = 70;
    public int CompressionLevel { get; init; } = 6;

    public static VideoConversionPreset Capture(string name, VideoAsset asset, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Name = name.Trim(),
        OutputFormat = asset.OutputFormat,
        SizeMode = asset.SizeMode,
        OutputWidth = asset.OutputWidth,
        Fps = asset.Fps,
        Quality = asset.Quality,
        CompressionLevel = asset.CompressionLevel
    };

    public bool CanApplyTo(VideoAsset asset) => !asset.IsAnimatedWebP || OutputFormat == "动态 WebP";

    public void ApplyTo(VideoAsset asset)
    {
        asset.OutputFormat = OutputFormat;
        asset.SizeMode = SizeMode;
        asset.OutputWidth = SizeMode == "原尺寸" ? asset.PixelWidth : OutputWidth;
        asset.Fps = Fps;
        asset.Quality = Quality;
        asset.CompressionLevel = CompressionLevel;
        asset.MarkOutputDirty();
    }

    public bool Matches(VideoAsset asset) =>
        asset.OutputFormat == OutputFormat
        && asset.SizeMode == SizeMode
        && (SizeMode == "原尺寸" || asset.OutputWidth == OutputWidth)
        && asset.Fps == Fps
        && asset.Quality == Quality
        && asset.CompressionLevel == CompressionLevel;

    public static bool MatchesDefault(VideoAsset asset) =>
        asset.OutputFormat == "动态 WebP"
        && asset.SizeMode == "原尺寸"
        && asset.Fps == (asset.IsAnimatedWebP && asset.SourceFps > 0 ? Math.Clamp((int)Math.Round(asset.SourceFps), 1, 120) : 25)
        && asset.Quality == 70
        && asset.CompressionLevel == 6;
}
