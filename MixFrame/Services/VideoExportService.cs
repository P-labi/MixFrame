using System.Diagnostics;
using MixFrame.Models;

namespace MixFrame.Services;

public sealed class VideoExportService(string ffmpegPath)
{
    private readonly AnimatedWebPService _animatedWebPService = new();

    public async Task<VideoExportResult> ExportAsync(VideoAsset asset, string outputPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        try
        {
            if (asset.IsAnimatedWebP)
            {
                if (asset.OutputFormat != "动态 WebP")
                    return VideoExportResult.Fail("动态 WebP 输入目前仅支持输出为动态 WebP");
                return await _animatedWebPService.ExportAsync(asset, outputPath, overwrite, cancellationToken);
            }

            var arguments = BuildArguments(asset, outputPath, overwrite);
            return await RunAsync(arguments, outputPath, cancellationToken);
        }
        catch (Exception ex)
        {
            return VideoExportResult.Fail(Compact(ex.Message));
        }
    }

    public async Task<VideoEstimateResult> EstimateAsync(VideoAsset asset, CancellationToken cancellationToken = default)
    {
        if (asset.IsAnimatedWebP)
        {
            if (asset.OutputFormat != "动态 WebP")
                return VideoEstimateResult.Fail("动态 WebP 输入目前仅支持输出为动态 WebP");
            return await _animatedWebPService.EstimateAsync(asset, cancellationToken);
        }

        var totalSeconds = Math.Max(0.1, asset.Duration.TotalSeconds);
        var sampleSeconds = Math.Min(2.0, totalSeconds);
        var sampleStart = totalSeconds > sampleSeconds
            ? Math.Max(0, (totalSeconds - sampleSeconds) / 2)
            : 0;
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"MixFrame-estimate-{Guid.NewGuid():N}.{GetOutputExtension(asset.OutputFormat)}");

        try
        {
            var arguments = BuildArguments(asset, temporaryPath, overwrite: true, sampleStart, sampleSeconds);
            var result = await RunAsync(arguments, temporaryPath, cancellationToken);
            if (!result.Success)
                return result.IsCancelled ? VideoEstimateResult.Cancelled() : VideoEstimateResult.Fail(result.ErrorMessage);

            var sampleBytes = new FileInfo(temporaryPath).Length;
            var estimatedBytes = sampleBytes * totalSeconds / sampleSeconds;
            var uncertainty = totalSeconds <= sampleSeconds + 0.01 ? 0.08 : 0.35;
            var minimum = Math.Max(1, (long)Math.Floor(estimatedBytes * (1 - uncertainty)));
            var maximum = Math.Max(minimum, (long)Math.Ceiling(estimatedBytes * (1 + uncertainty)));
            return VideoEstimateResult.Ok(minimum, maximum);
        }
        catch (Exception ex)
        {
            return VideoEstimateResult.Fail(Compact(ex.Message));
        }
        finally
        {
            TryDeletePartialOutput(temporaryPath);
        }
    }

    public static string ResolveOutputDirectory(VideoAsset asset) => asset.OutputDirectory == "与源文件同目录"
        ? Path.GetDirectoryName(asset.FilePath) ?? string.Empty
        : asset.OutputDirectory;

    private static IReadOnlyList<string> BuildArguments(
        VideoAsset asset,
        string outputPath,
        bool overwrite,
        double? sampleStart = null,
        double? sampleDuration = null)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", overwrite ? "-y" : "-n" };
        if (sampleStart is > 0)
            arguments.AddRange(["-ss", sampleStart.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-i", asset.FilePath]);
        if (sampleDuration is > 0)
            arguments.AddRange(["-t", sampleDuration.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-map_metadata", "-1"]);
        var videoFilter = BuildVideoFilter(asset);

        switch (asset.OutputFormat)
        {
            case "动态 WebP":
                arguments.AddRange(["-map", "0:v:0", "-vf", videoFilter, "-c:v", "libwebp_anim", "-quality", asset.Quality.ToString(), "-compression_level", asset.CompressionLevel.ToString(), "-loop", "0", "-an"]);
                break;
            case "MP4":
                arguments.AddRange(["-map", "0:v:0", "-vf", videoFilter, "-c:v", "libx264", "-crf", MapH264Crf(asset.Quality).ToString(), "-preset", "medium", "-pix_fmt", "yuv420p", "-an", "-movflags", "+faststart"]);
                break;
            case "GIF":
                var gifBase = asset.SizeMode == "指定宽度，高度等比" && asset.OutputWidth is > 0
                    ? $"fps={asset.Fps},scale={asset.OutputWidth.Value}:-2:flags=lanczos"
                    : $"fps={asset.Fps}";
                var gifGraph = $"{gifBase},split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse[out]";
                arguments.AddRange(["-filter_complex", gifGraph, "-map", "[out]", "-an"]);
                break;
            case "WebM":
                arguments.AddRange(["-map", "0:v:0", "-vf", videoFilter, "-c:v", "libvpx-vp9", "-crf", MapVp9Crf(asset.Quality).ToString(), "-b:v", "0", "-row-mt", "1", "-an"]);
                break;
            default:
                throw new InvalidOperationException("不支持的输出格式");
        }

        arguments.Add(outputPath);
        return arguments;
    }

    private static string BuildVideoFilter(VideoAsset asset)
    {
        var filters = new List<string> { $"fps={asset.Fps}" };
        if (asset.SizeMode == "指定宽度，高度等比" && asset.OutputWidth is > 0)
            filters.Add($"scale={asset.OutputWidth.Value}:-2");
        if (asset.OutputFormat == "动态 WebP" && asset.HasAlpha)
            filters.Add("format=rgba");
        return string.Join(',', filters);
    }

    private async Task<VideoExportResult> RunAsync(IReadOnlyList<string> arguments, string outputPath, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }};
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
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
            return VideoExportResult.Cancelled();
        }
        var stderr = await stderrTask;
        var stdout = await stdoutTask;
        if (process.ExitCode == 0 && File.Exists(outputPath)) return VideoExportResult.Ok(outputPath);
        TryDeletePartialOutput(outputPath);
        return VideoExportResult.Fail(Compact(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr));
    }

    private static string GetOutputExtension(string format) => format switch
    {
        "MP4" => "mp4",
        "GIF" => "gif",
        "WebM" => "webm",
        _ => "webp"
    };

    public static string BuildRequestedOutputPath(VideoAsset asset)
    {
        return Path.Combine(ResolveOutputDirectory(asset), Path.GetFileName(asset.EffectiveOutputFileName));
    }

    public static string BuildUniqueOutputPath(VideoAsset asset, string directory)
    {
        var extension = asset.OutputFormat switch { "MP4" => ".mp4", "GIF" => ".gif", "WebM" => ".webm", _ => ".webp" };
        var requestedName = Path.GetFileName(asset.EffectiveOutputFileName);
        var fileName = Path.ChangeExtension(requestedName, extension);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var sourcePath = Path.GetFullPath(asset.FilePath);
        var candidate = Path.Combine(directory, fileName);
        var index = 1;
        while (File.Exists(candidate) || string.Equals(Path.GetFullPath(candidate), sourcePath, StringComparison.OrdinalIgnoreCase))
            candidate = Path.Combine(directory, $"{baseName} ({index++}){extension}");
        return candidate;
    }

    private static int MapH264Crf(int quality) => (int)Math.Round(51 - (Math.Clamp(quality, 1, 100) - 1) * 43d / 99d);
    private static int MapVp9Crf(int quality) => (int)Math.Round(63 - (Math.Clamp(quality, 1, 100) - 1) * 53d / 99d);
    private static void TryDeletePartialOutput(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Keep the original export or cancellation result. */ }
    }
    private static string Compact(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "编码失败";
        var value = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 180 ? value : $"{value[..180]}…";
    }
}

public sealed record VideoEstimateResult(bool Success, bool IsCancelled, long MinimumBytes, long MaximumBytes, string ErrorMessage)
{
    public static VideoEstimateResult Ok(long minimumBytes, long maximumBytes) => new(true, false, minimumBytes, maximumBytes, string.Empty);
    public static VideoEstimateResult Fail(string error) => new(false, false, 0, 0, error);
    public static VideoEstimateResult Cancelled() => new(false, true, 0, 0, "已取消");
}

public sealed record VideoExportResult(bool Success, bool IsCancelled, string OutputPath, string ErrorMessage)
{
    public static VideoExportResult Ok(string path) => new(true, false, path, string.Empty);
    public static VideoExportResult Fail(string error) => new(false, false, string.Empty, error);
    public static VideoExportResult Cancelled() => new(false, true, string.Empty, "已取消");
}
