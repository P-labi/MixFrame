namespace MixFrame.Pages;

public interface IDroppedPathHandler
{
    Task ImportDroppedPathsAsync(IReadOnlyList<string> paths);
}
