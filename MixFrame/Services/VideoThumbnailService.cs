using System.Diagnostics;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace MixFrame.Services;

public sealed class VideoThumbnailService(string ffmpegPath)
{
    public async Task<BitmapImage?> CreateAsync(string sourcePath)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }};
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-ss", "0", "-i", sourcePath, "-frames:v", "1", "-vf", "scale=320:-2", "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1" })
                process.StartInfo.ArgumentList.Add(argument);

            process.Start();
            using var imageBytes = new MemoryStream();
            var imageTask = process.StandardOutput.BaseStream.CopyToAsync(imageBytes);
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stderr, imageTask);
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
        catch
        {
            // Thumbnail failures must not make an otherwise readable video unavailable.
            return null;
        }
    }
}
