namespace MixFrame.Services;

internal static class BatchOutputFileNameBuilder
{
    public static string Build(
        string requestedName,
        string sourceFileName,
        IReadOnlySet<string> recognizedExtensions)
    {
        var requestedExtension = Path.GetExtension(requestedName);
        var hasRecognizedExtension = !string.IsNullOrEmpty(requestedExtension)
            && recognizedExtensions.Contains(requestedExtension);
        var prefix = hasRecognizedExtension
            ? requestedName[..^requestedExtension.Length]
            : requestedName;
        var sourceBaseName = Path.GetFileNameWithoutExtension(sourceFileName);
        return hasRecognizedExtension
            ? $"{prefix}-{sourceBaseName}{requestedExtension}"
            : $"{prefix}-{sourceBaseName}";
    }
}
