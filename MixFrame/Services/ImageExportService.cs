using System.Diagnostics;
using MixFrame.Models;

namespace MixFrame.Services;

public sealed class ImageExportService(string ffmpegPath)
{
    public async Task<ImageExportResult> ExportAsync(ImageAsset asset, string outputPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        try
        {
            var arguments = BuildArguments(asset, outputPath, overwrite);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(stderrTask, stdoutTask);
                TryDeletePartialOutput(outputPath);
                return ImageExportResult.Cancelled();
            }
            var stderr = await stderrTask;
            var stdout = await stdoutTask;

            if (process.ExitCode == 0 && File.Exists(outputPath))
            {
                return ImageExportResult.Ok(outputPath);
            }

            var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            TryDeletePartialOutput(outputPath);
            return ImageExportResult.Fail(TrimProcessMessage(message));
        }
        catch (Exception ex)
        {
            return ImageExportResult.Fail(ex.Message);
        }
    }

    public static string ResolveOutputDirectory(ImageAsset asset)
    {
        return asset.OutputLocation == "与源文件同目录"
            ? Path.GetDirectoryName(asset.FilePath) ?? string.Empty
            : asset.OutputLocation;
    }

    public static string BuildRequestedOutputPath(ImageAsset asset)
    {
        return Path.Combine(ResolveOutputDirectory(asset), NormalizeOutputFileName(asset.OutputFileName, asset.OutputFormat));
    }

    public static string BuildUniqueOutputPath(ImageAsset asset, string outputDirectory)
    {
        var fileName = NormalizeOutputFileName(asset.OutputFileName, asset.OutputFormat);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(outputDirectory, fileName);
        var sourceFullPath = Path.GetFullPath(asset.FilePath);
        var index = 1;

        while (File.Exists(candidate)
            || string.Equals(Path.GetFullPath(candidate), sourceFullPath, StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.Combine(outputDirectory, $"{baseName} ({index}){extension}");
            index++;
        }

        return candidate;
    }

    private static string NormalizeOutputFileName(string outputFileName, string outputFormat)
    {
        var extension = outputFormat switch
        {
            "JPG" => ".jpg",
            "PNG" => ".png",
            _ => ".webp"
        };

        var fileNameWithoutPath = Path.GetFileName(outputFileName);
        if (string.IsNullOrWhiteSpace(fileNameWithoutPath))
        {
            throw new InvalidOperationException("输出文件名为空");
        }

        return Path.ChangeExtension(fileNameWithoutPath, extension);
    }

    private static IReadOnlyList<string> BuildArguments(ImageAsset asset, string outputPath, bool overwrite)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "error",
            overwrite ? "-y" : "-n",
            "-i",
            asset.FilePath,
            "-map_metadata",
            "-1"
        };

        var needsBackgroundComposite = asset.OutputFormat == "JPG" && asset.HasAlpha;
        var filters = BuildFilters(asset, includeJpegPixelFormat: !needsBackgroundComposite);
        if (needsBackgroundComposite)
        {
            var (widthValue, heightValue) = asset.GetTargetSize();
            var width = Math.Round(widthValue);
            var height = Math.Round(heightValue);
            var foregroundFilters = string.IsNullOrWhiteSpace(filters) ? "format=rgba" : $"{filters},format=rgba";
            var color = NormalizeFfmpegColor(asset.Background);
            arguments.Add("-filter_complex");
            arguments.Add($"[0:v]{foregroundFilters}[fg];color=c={color}:s={width:0}x{height:0}[bg];[bg][fg]overlay=0:0:format=auto,format=rgb24[out]");
            arguments.Add("-map");
            arguments.Add("[out]");
        }
        else if (!string.IsNullOrWhiteSpace(filters))
        {
            arguments.Add("-vf");
            arguments.Add(filters);
        }

        switch (asset.OutputFormat)
        {
            case "WebP":
                arguments.Add("-c:v");
                arguments.Add("libwebp");
                arguments.Add("-quality");
                arguments.Add(Math.Round(asset.Quality).ToString("0"));
                arguments.Add("-compression_level");
                arguments.Add(asset.CompressionLevel.ToString());
                break;
            case "JPG":
                arguments.Add("-q:v");
                arguments.Add(MapJpegQuality(asset.Quality).ToString("0"));
                break;
            case "PNG":
                arguments.Add("-compression_level");
                arguments.Add(asset.CompressionLevel.ToString());
                break;
        }

        arguments.Add("-frames:v");
        arguments.Add("1");
        arguments.Add(outputPath);
        return arguments;
    }

    private static string BuildFilters(ImageAsset asset, bool includeJpegPixelFormat)
    {
        var filters = new List<string>();
        var (widthValue, heightValue) = asset.GetTargetSize();
        var width = Math.Round(widthValue);
        var height = Math.Round(heightValue);
        switch (asset.SizeMode)
        {
            case "指定宽度，高度等比":
                filters.Add($"scale={width:0}:-1");
                break;
            case "指定画布宽高，包含完整图片":
                filters.Add($"scale={width:0}:{height:0}:force_original_aspect_ratio=decrease");
                var preserveTransparency = asset.Background == "保留透明度" && asset.OutputFormat is "PNG" or "WebP";
                if (preserveTransparency) filters.Add("format=rgba");
                filters.Add($"pad={width:0}:{height:0}:(ow-iw)/2:(oh-ih)/2:color={NormalizeFfmpegColor(asset.Background)}");
                if (preserveTransparency) filters.Add("format=rgba");
                break;
            case "指定画布宽高，裁切填满":
                filters.Add($"scale={width:0}:{height:0}:force_original_aspect_ratio=increase");
                filters.Add($"crop={width:0}:{height:0}");
                break;
        }

        if (asset.OutputFormat == "JPG" && includeJpegPixelFormat)
        {
            filters.Add("format=rgb24");
        }

        return string.Join(",", filters);
    }

    private static string NormalizeFfmpegColor(string background) => background switch
    {
        "保留透明度" => "black@0",
        var value when value.StartsWith('#') => value.Replace("#", "0x", StringComparison.Ordinal),
        _ => "black@0"
    };

    private static int MapJpegQuality(double quality)
    {
        var normalized = Math.Clamp(quality, 1, 100);
        return (int)Math.Round(31 - ((normalized - 1) * 29 / 99));
    }

    private static void TryDeletePartialOutput(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Keep the original export or cancellation result. */ }
    }

    private static string TrimProcessMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "编码失败";
        }

        var oneLine = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= 160 ? oneLine : $"{oneLine[..160]}...";
    }
}

public sealed record ImageExportResult(bool Success, bool IsCancelled, string OutputPath, string ErrorMessage)
{
    public static ImageExportResult Ok(string outputPath) => new(true, false, outputPath, string.Empty);

    public static ImageExportResult Fail(string errorMessage) => new(false, false, string.Empty, errorMessage);
    public static ImageExportResult Cancelled() => new(false, true, string.Empty, "已取消");
}
