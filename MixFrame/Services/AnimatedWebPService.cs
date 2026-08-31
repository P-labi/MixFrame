using ImageMagick;
using ImageMagick.Formats;
using MixFrame.Models;

namespace MixFrame.Services;

public sealed class AnimatedWebPService
{
    public async Task<AnimatedWebPInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var frames = new MagickImageCollection(path);
            if (frames.Count == 0) throw new InvalidOperationException("动态 WebP 中没有可读取的帧");

            var durationSeconds = GetDurationSeconds(frames);
            var frameRate = durationSeconds > 0 ? frames.Count / durationSeconds : 0;
            return new AnimatedWebPInfo(
                frames.Count > 1,
                checked((int)frames[0].Width),
                checked((int)frames[0].Height),
                frames.Count,
                durationSeconds,
                frameRate,
                frames.Any(frame => frame.HasAlpha),
                frames[0].AnimationIterations);
        }, cancellationToken);
    }

    public async Task<VideoExportResult> ExportAsync(
        VideoAsset asset,
        string outputPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!overwrite && File.Exists(outputPath))
                return VideoExportResult.Fail("输出文件已存在");

            await Task.Run(() =>
            {
                using var source = new MagickImageCollection(asset.FilePath);
                using var prepared = PrepareFrames(source, asset, 0, null, cancellationToken);
                Write(prepared, asset, outputPath, cancellationToken);
            }, cancellationToken);

            return File.Exists(outputPath)
                ? VideoExportResult.Ok(outputPath)
                : VideoExportResult.Fail("动态 WebP 编码未生成输出文件");
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialOutput(outputPath);
            return VideoExportResult.Cancelled();
        }
        catch (Exception ex)
        {
            TryDeletePartialOutput(outputPath);
            return VideoExportResult.Fail(Compact(ex.Message));
        }
    }

    public async Task<VideoEstimateResult> EstimateAsync(VideoAsset asset, CancellationToken cancellationToken = default)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"MixFrame-estimate-{Guid.NewGuid():N}.webp");
        try
        {
            return await Task.Run(() =>
            {
                using var source = new MagickImageCollection(asset.FilePath);
                var totalSeconds = Math.Max(0.001, GetDurationSeconds(source));
                var sampleSeconds = Math.Min(2.0, totalSeconds);
                var sampleStart = Math.Max(0, (totalSeconds - sampleSeconds) / 2);
                using var prepared = PrepareFrames(source, asset, sampleStart, sampleSeconds, cancellationToken);
                Write(prepared, asset, temporaryPath, cancellationToken);

                var sampleBytes = new FileInfo(temporaryPath).Length;
                var estimatedBytes = sampleBytes * totalSeconds / sampleSeconds;
                var uncertainty = totalSeconds <= sampleSeconds + 0.01 ? 0.08 : 0.30;
                var minimum = Math.Max(1, (long)Math.Floor(estimatedBytes * (1 - uncertainty)));
                var maximum = Math.Max(minimum, (long)Math.Ceiling(estimatedBytes * (1 + uncertainty)));
                return VideoEstimateResult.Ok(minimum, maximum);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return VideoEstimateResult.Cancelled();
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

    private static MagickImageCollection PrepareFrames(
        MagickImageCollection source,
        VideoAsset asset,
        double startSeconds,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        if (source.Count == 0) throw new InvalidOperationException("动态 WebP 中没有可读取的帧");
        source.Coalesce();

        var timeline = BuildTimeline(source);
        var totalSeconds = timeline[^1];
        var start = Math.Clamp(startSeconds, 0, Math.Max(0, totalSeconds - 0.001));
        var duration = Math.Clamp(durationSeconds ?? totalSeconds, 0.001, totalSeconds - start);
        var fps = Math.Clamp(asset.Fps, 1, 120);
        var outputFrameCount = Math.Max(1, checked((int)Math.Ceiling(duration * fps - 0.000001)));
        var (targetWidth, targetHeight) = asset.GetTargetSize();
        var iterations = source[0].AnimationIterations;
        var prepared = new MagickImageCollection();
        var sourceIndex = 0;

        for (var outputIndex = 0; outputIndex < outputFrameCount; outputIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sampleTime = Math.Min(totalSeconds - 0.000001, start + (outputIndex + 0.5) / fps);
            while (sourceIndex + 1 < timeline.Length && timeline[sourceIndex + 1] <= sampleTime)
                sourceIndex++;

            var frame = source[sourceIndex].Clone();
            if (frame.Width != targetWidth || frame.Height != targetHeight)
                frame.Resize((uint)targetWidth, (uint)targetHeight);

            frame.Strip();
            frame.Format = MagickFormat.WebP;
            frame.Quality = (uint)Math.Clamp(asset.Quality, 1, 100);
            frame.AnimationTicksPerSecond = 1000;
            var currentTick = (int)Math.Round(outputIndex * 1000d / fps);
            var nextTick = (int)Math.Round((outputIndex + 1) * 1000d / fps);
            frame.AnimationDelay = (uint)Math.Max(1, nextTick - currentTick);
            frame.AnimationIterations = iterations;
            prepared.Add(frame);
        }

        return prepared;
    }

    private static void Write(
        MagickImageCollection frames,
        VideoAsset asset,
        string outputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var defines = new WebPWriteDefines
        {
            Method = Math.Clamp(asset.CompressionLevel, 0, 6),
            AlphaCompression = WebPAlphaCompression.Compressed,
            AlphaFiltering = WebPAlphaFiltering.Best,
            AlphaQuality = 100,
            AutoFilter = true,
            ThreadLevel = true,
            UseSharpYuv = true
        };
        frames.Write(outputPath, defines);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static double[] BuildTimeline(MagickImageCollection frames)
    {
        var timeline = new double[frames.Count + 1];
        for (var index = 0; index < frames.Count; index++)
        {
            var ticksPerSecond = frames[index].AnimationTicksPerSecond > 0
                ? frames[index].AnimationTicksPerSecond
                : 100;
            var delay = frames[index].AnimationDelay > 0
                ? frames[index].AnimationDelay
                : (uint)Math.Max(1, ticksPerSecond / 25);
            timeline[index + 1] = timeline[index] + delay / (double)ticksPerSecond;
        }
        return timeline;
    }

    private static double GetDurationSeconds(MagickImageCollection frames) => BuildTimeline(frames)[^1];

    private static void TryDeletePartialOutput(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Preserve the original export or cancellation result. */ }
    }

    private static string Compact(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "动态 WebP 处理失败";
        var value = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 180 ? value : $"{value[..180]}…";
    }
}

public sealed record AnimatedWebPInfo(
    bool IsAnimated,
    int Width,
    int Height,
    int FrameCount,
    double DurationSeconds,
    double FrameRate,
    bool HasAlpha,
    uint LoopCount);
