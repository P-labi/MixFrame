using System.Diagnostics;

namespace MixFrame.Services;

public static class OutputDirectoryLauncher
{
    public static string? TryOpen(string directory, Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo { FileName = directory, UseShellExecute = true };
            (startProcess ?? Process.Start)(startInfo);
            return null;
        }
        catch (Exception ex)
        {
            return $"无法打开输出文件夹：{ex.Message}";
        }
    }
}
