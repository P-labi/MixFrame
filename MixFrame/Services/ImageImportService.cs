using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml.Media.Imaging;
using MixFrame.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MixFrame.Services;

public sealed class ImageImportService
{
    private readonly AnimatedWebPService _animatedWebPService = new();

    public async Task<ImageAsset> ReadAsync(StorageFile file)
    {
        try
        {
            if (file.FileType.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                var animation = await _animatedWebPService.ProbeAsync(file.Path);
                if (animation.IsAnimated)
                    return CreateUnavailable(file, "不支持：动态 WebP 请在视频转换中处理");
            }

            await using var stream = await file.OpenStreamForReadAsync();
            using var randomAccessStream = stream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            var properties = await file.GetBasicPropertiesAsync();
            var contentFormat = NormalizeContentFormat(decoder.DecoderInformation.FriendlyName);

            if (contentFormat is not ("PNG" or "JPEG" or "WEBP"))
            {
                return CreateUnavailable(file, $"不支持：{contentFormat}");
            }

            var extension = file.FileType.TrimStart('.').ToUpperInvariant();
            var displayFormat = ExtensionMatches(contentFormat, extension)
                ? contentFormat
                : $"{contentFormat} 内容 / .{extension.ToLowerInvariant()} 扩展名";

            var asset = new ImageAsset
            {
                FilePath = file.Path,
                FileName = file.Name,
                SourceFormat = displayFormat,
                SourceSize = $"{decoder.PixelWidth}×{decoder.PixelHeight}",
                PixelWidth = decoder.PixelWidth,
                PixelHeight = decoder.PixelHeight,
                HasAlpha = decoder.BitmapAlphaMode != BitmapAlphaMode.Ignore,
                FileSizeText = FormatBytes(properties.Size),
                FileSizeMb = properties.Size / 1024d / 1024d,
                ReadStatus = "就绪",
                Thumbnail = new BitmapImage(new Uri(file.Path))
            };
            asset.ResetToDefault();
            return asset;
        }
        catch (Exception ex)
        {
            var ffmpegAsset = await TryReadWithFfmpegAsync(file);
            if (ffmpegAsset is not null) return ffmpegAsset;
            var supportedExtension = file.FileType is ".png" or ".jpg" or ".jpeg" or ".webp";
            return CreateUnavailable(file, supportedExtension
                ? $"读取失败：{Compact(ex.Message)}"
                : $"不支持：无法识别 {file.FileType}");
        }
    }

    private static async Task<ImageAsset?> TryReadWithFfmpegAsync(StorageFile file)
    {
        try
        {
            var ffprobe = FfmpegLocator.FindExecutable("ffprobe");
            using var probe = new Process { StartInfo = CreateProcessInfo(ffprobe) };
            foreach (var argument in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height,pix_fmt", "-of", "json", file.Path })
                probe.StartInfo.ArgumentList.Add(argument);
            probe.Start();
            var stdoutTask = probe.StandardOutput.ReadToEndAsync();
            var stderrTask = probe.StandardError.ReadToEndAsync();
            await probe.WaitForExitAsync();
            var json = await stdoutTask;
            await stderrTask;
            if (probe.ExitCode != 0) return null;

            using var document = JsonDocument.Parse(json);
            var stream = document.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault();
            if (stream.ValueKind == JsonValueKind.Undefined) return null;
            var codec = stream.GetProperty("codec_name").GetString()?.ToLowerInvariant();
            var contentFormat = codec switch { "png" => "PNG", "mjpeg" => "JPEG", "webp" => "WEBP", _ => null };
            if (contentFormat is null) return null;
            var width = stream.GetProperty("width").GetInt32();
            var height = stream.GetProperty("height").GetInt32();
            if (width <= 0 || height <= 0) return null;
            var pixelFormat = stream.TryGetProperty("pix_fmt", out var pixelNode) ? pixelNode.GetString() ?? string.Empty : string.Empty;
            var properties = await file.GetBasicPropertiesAsync();
            var extension = file.FileType.TrimStart('.').ToUpperInvariant();
            var displayFormat = ExtensionMatches(contentFormat, extension) ? contentFormat : $"{contentFormat} 内容 / .{extension.ToLowerInvariant()} 扩展名";
            var thumbnail = await CreateFfmpegThumbnailAsync(file.Path);
            var asset = new ImageAsset
            {
                FilePath = file.Path,
                FileName = file.Name,
                SourceFormat = displayFormat,
                SourceSize = $"{width}×{height}",
                PixelWidth = checked((uint)width),
                PixelHeight = checked((uint)height),
                HasAlpha = pixelFormat.Contains('a'),
                FileSizeText = FormatBytes(properties.Size),
                FileSizeMb = properties.Size / 1024d / 1024d,
                ReadStatus = "就绪",
                Thumbnail = thumbnail
            };
            asset.ResetToDefault();
            return asset;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<BitmapImage?> CreateFfmpegThumbnailAsync(string sourcePath)
    {
        using var process = new Process { StartInfo = CreateProcessInfo(FfmpegLocator.FindExecutable("ffmpeg")) };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-i", sourcePath, "-frames:v", "1", "-vf", "scale=320:-2", "-f", "image2pipe", "-vcodec", "png", "pipe:1" })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var imageBytes = new MemoryStream();
        var imageTask = process.StandardOutput.BaseStream.CopyToAsync(imageBytes);
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(imageTask, stderrTask);
        if (process.ExitCode != 0 || imageBytes.Length == 0) return null;

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(imageBytes.ToArray());
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var thumbnail = new BitmapImage();
        await thumbnail.SetSourceAsync(stream);
        return thumbnail;
    }

    private static ProcessStartInfo CreateProcessInfo(string executable) => new()
    {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true
    };

    private static ImageAsset CreateUnavailable(StorageFile file, string status) => new()
    {
        FilePath = file.Path,
        FileName = file.Name,
        SourceFormat = file.FileType.TrimStart('.').ToUpperInvariant(),
        SourceSize = "未知",
        FileSizeText = "未知",
        ReadStatus = status
    };

    private static string NormalizeContentFormat(string friendlyName)
    {
        var value = friendlyName.ToUpperInvariant();
        if (value.Contains("JPEG") || value.Contains("JPG")) return "JPEG";
        if (value.Contains("PNG")) return "PNG";
        if (value.Contains("WEBP")) return "WEBP";
        return value;
    }

    private static bool ExtensionMatches(string contentFormat, string extension) =>
        contentFormat == extension || contentFormat == "JPEG" && extension is "JPG" or "JPEG";

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1024UL * 1024UL => $"{bytes / 1024d / 1024d:0.0} MB",
        >= 1024UL => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };

    private static string Compact(string message)
    {
        var value = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 120 ? value : $"{value[..120]}…";
    }
}
