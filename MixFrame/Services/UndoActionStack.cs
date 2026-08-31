namespace MixFrame.Services;

public sealed class UndoActionStack(int capacity = 20)
{
    private readonly List<Action> _actions = [];

    public void Push(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_actions.Count == capacity)
            _actions.RemoveAt(0);
        _actions.Add(action);
    }

    public bool TryUndo()
    {
        if (_actions.Count == 0) return false;
        var index = _actions.Count - 1;
        var action = _actions[index];
        _actions.RemoveAt(index);
        action();
        return true;
    }
}
