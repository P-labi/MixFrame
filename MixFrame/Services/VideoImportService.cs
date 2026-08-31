using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml.Media.Imaging;
using MixFrame.Models;
using Windows.Storage;

namespace MixFrame.Services;

public sealed class VideoImportService
{
    private static readonly HashSet<string> KnownVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".webp", ".m4v", ".wmv", ".flv", ".mpeg", ".mpg", ".ts" };

    private readonly string _ffprobePath = FfmpegLocator.FindExecutable("ffprobe");
    private readonly VideoThumbnailService _thumbnailService = new(FfmpegLocator.FindExecutable("ffmpeg"));
    private readonly AnimatedWebPService _animatedWebPService = new();

    public async Task<VideoAsset> ReadAsync(StorageFile file)
    {
        try
        {
            if (file.FileType.Equals(".webp", StringComparison.OrdinalIgnoreCase))
                return await ReadAnimatedWebPAsync(file);

            var probe = await ProbeAsync(file.Path);
            using var document = JsonDocument.Parse(probe);
            var root = document.RootElement;
            var videoStream = root.GetProperty("streams").EnumerateArray()
                .FirstOrDefault(stream => stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video");
            if (videoStream.ValueKind == JsonValueKind.Undefined)
                return Unavailable(file, "不支持：未发现视频流");

            var format = root.GetProperty("format");
            var width = videoStream.TryGetProperty("width", out var widthNode) ? widthNode.GetInt32() : 0;
            var height = videoStream.TryGetProperty("height", out var heightNode) ? heightNode.GetInt32() : 0;
            if (width <= 0 || height <= 0) return Unavailable(file, "读取失败：视频尺寸无效");

            var duration = ReadDouble(videoStream, "duration") ?? ReadDouble(format, "duration") ?? 0;
            var frameRate = videoStream.TryGetProperty("avg_frame_rate", out var fpsNode) ? ParseRate(fpsNode.GetString()) : 0;
            var frameCount = ReadLong(videoStream, "nb_frames");
            var formatName = format.TryGetProperty("format_name", out var formatNode) ? formatNode.GetString() : null;
            var codecName = videoStream.TryGetProperty("codec_name", out var codecNode) ? codecNode.GetString() : null;
            if (IsStaticImageStream(formatName, codecName, duration, frameCount))
                return Unavailable(file, "不支持：静态图片请在图片转换中处理");

            var properties = await file.GetBasicPropertiesAsync();
            var durationValue = TimeSpan.FromSeconds(Math.Max(0, duration));
            BitmapImage? thumbnail = null;
            try { thumbnail = await _thumbnailService.CreateAsync(file.Path); }
            catch { /* A thumbnail failure must not reject a valid video. */ }

            return new VideoAsset
            {
                FilePath = file.Path,
                FileName = file.Name,
                ContainerFormat = FriendlyContainer(formatName),
                VideoCodec = (codecName ?? "未知").ToUpperInvariant(),
                PixelWidth = width,
                PixelHeight = height,
                SourceFps = frameRate,
                Duration = durationValue,
                SourceBytes = checked((long)properties.Size),
                HasAlpha = videoStream.TryGetProperty("pix_fmt", out var pixelFormatNode)
                    && (pixelFormatNode.GetString()?.IndexOf('a') ?? -1) >= 0,
                ReadStatus = "就绪",
                OutputWidth = width,
                Thumbnail = thumbnail
            };
        }
        catch (Exception ex)
        {
            var status = KnownVideoExtensions.Contains(file.FileType)
                ? $"读取失败：{Compact(ex.Message)}"
                : $"不支持：无法识别 {file.FileType}";
            return Unavailable(file, status);
        }
    }

    private async Task<string> ProbeAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(_ffprobePath))
            throw new FileNotFoundException(FfmpegLocator.MissingMessage("ffprobe"));
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }};
        foreach (var argument in new[]
        {
            "-v", "error",
            "-show_entries", "stream=codec_type,codec_name,pix_fmt,width,height,duration,avg_frame_rate,nb_frames:format=format_name,duration",
            "-of", "json",
            path
        })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(Compact(stderr));
        return stdout;
    }

    private async Task<VideoAsset> ReadAnimatedWebPAsync(StorageFile file)
    {
        var info = await _animatedWebPService.ProbeAsync(file.Path);
        if (!info.IsAnimated)
            return Unavailable(file, "不支持：静态 WebP 请在图片转换中处理");

        var properties = await file.GetBasicPropertiesAsync();
        BitmapImage? thumbnail = null;
        try { thumbnail = new BitmapImage(new Uri(file.Path)); }
        catch { /* A thumbnail failure must not reject a valid animation. */ }
        var asset = new VideoAsset
        {
            FilePath = file.Path,
            FileName = file.Name,
            ContainerFormat = "动态 WebP",
            VideoCodec = "WEBP",
            PixelWidth = info.Width,
            PixelHeight = info.Height,
            SourceFps = info.FrameRate,
            Duration = TimeSpan.FromSeconds(info.DurationSeconds),
            SourceBytes = checked((long)properties.Size),
            HasAlpha = info.HasAlpha,
            IsAnimatedWebP = true,
            FrameCount = info.FrameCount,
            ReadStatus = "就绪",
            OutputWidth = info.Width,
            Thumbnail = thumbnail
        };
        asset.ResetToDefault();
        return asset;
    }

    private static VideoAsset Unavailable(StorageFile file, string status) => new()
    { FilePath = file.Path, FileName = file.Name, ReadStatus = status, ErrorMessage = status };

    private static double? ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && double.TryParse(node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? ReadLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && long.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool IsStaticImageStream(string? formatName, string? codecName, double duration, long? frameCount)
    {
        var formats = (formatName ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (formats.Any(format => format.EndsWith("_pipe", StringComparison.OrdinalIgnoreCase)
            || format.Equals("image2", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (duration > 0 || frameCount is > 1)
            return false;

        return codecName is not null && codecName.ToLowerInvariant() is "png" or "mjpeg" or "webp" or "bmp" or "tiff" or "gif";
    }

    private static double ParseRate(string? rate)
    {
        var parts = (rate ?? "0/1").Split('/');
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0 ? numerator / denominator : 0;
    }

    private static string FriendlyContainer(string? value) => (value ?? "未知").Split(',')[0].ToUpperInvariant();
    private static string Compact(string message)
    {
        var value = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 140 ? value : $"{value[..140]}…";
    }
}
