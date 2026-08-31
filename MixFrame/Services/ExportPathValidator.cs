namespace MixFrame.Services;

public static class ExportPathValidator
{
    private static readonly HashSet<string> ReservedBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string? ValidateFileName(string requestedName, string effectiveName)
    {
        // An empty requested value intentionally means: keep the source basename.
        var name = string.IsNullOrWhiteSpace(requestedName) ? effectiveName : requestedName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return "输出文件名为空";
        if (!string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar))
            return "输出文件名不能包含路径或文件夹片段";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "输出文件名包含 Windows 不允许的字符";
        if (name.EndsWith(' ') || name.EndsWith('.'))
            return "输出文件名不能以空格或句点结尾";

        var baseName = Path.GetFileNameWithoutExtension(name);
        if (baseName is "." or ".." || ReservedBaseNames.Contains(baseName))
            return "输出文件名是 Windows 保留名称";
        return null;
    }

    public static string? ValidateDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "输出位置为空";
        if (!Directory.Exists(directory)) return "输出目录不存在";

        try
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReadOnly) != 0) return "输出目录为只读";

            var probePath = Path.Combine(directory, $".mixframe-write-test-{Guid.NewGuid():N}.tmp");
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "没有写入输出目录的权限";
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or PathTooLongException)
        {
            return $"输出目录不可写：{ex.Message}";
        }
    }
}
