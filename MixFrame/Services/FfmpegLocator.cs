namespace MixFrame.Services;

public static class FfmpegLocator
{
    public static string FindExecutable(string executableName)
    {
        return TryFindExecutable(executableName, out var path) ? path : string.Empty;
    }

    public static bool TryFindExecutable(string executableName, out string path)
    {
        var fileName = executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executableName
            : $"{executableName}.exe";

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "ffmpeg-8.1.2-essentials_build", "bin", fileName);
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
            current = current.Parent;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (!File.Exists(candidate)) continue;
                path = candidate;
                return true;
            }
            catch (Exception) when (directory.Length > 0)
            {
                // Ignore malformed PATH entries and continue searching.
            }
        }

        path = string.Empty;
        return false;
    }

    public static string MissingMessage(string executableName) =>
        $"未找到 {Path.GetFileNameWithoutExtension(executableName)}。请确认应用目录包含 ffmpeg-8.1.2-essentials_build\\bin，或已将该工具加入系统 PATH。";
}
