namespace ScreenshotApp.Editor.Model;

internal interface IEditCommand
{
    void Do();
    void Undo();

    /// <summary>Lets rapid repeats (a slider drag) collapse into one undo step. <paramref name="next"/> is already applied.</summary>
    bool TryMerge(IEditCommand next) => false;
}

/// <summary>Undo/redo history. Commands store annotation snapshots (clones) so later edits cannot corrupt them.</summary>
internal sealed class CommandStack
{
    private readonly List<IEditCommand> _undo = new();
    private readonly List<IEditCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public event Action? Changed;

    /// <summary>Applies the command and records it.</summary>
    public void Execute(IEditCommand command)
    {
        command.Do();
        Push(command);
    }

    /// <summary>Records a command whose effect was already applied live (e.g. during a drag).</summary>
    public void Push(IEditCommand command)
    {
        if (_undo.Count == 0 || !_undo[^1].TryMerge(command))
            _undo.Add(command);
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var c = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        c.Undo();
        _redo.Add(c);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var c = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        c.Do();
        _undo.Add(c);
        Changed?.Invoke();
    }
}

internal sealed class AddCommand : IEditCommand
{
    private readonly List<Annotation> _list;
    private readonly Annotation _snapshot;

    public AddCommand(List<Annotation> list, Annotation annotation)
    {
        _list = list;
        _snapshot = annotation.Clone();
    }

    public void Do()
    {
        if (!_list.Any(a => a.Id == _snapshot.Id)) _list.Add(_snapshot.Clone());
    }

    public void Undo() => _list.RemoveAll(a => a.Id == _snapshot.Id);
}

internal sealed class RemoveCommand : IEditCommand
{
    private readonly List<Annotation> _list;
    private readonly Annotation _snapshot;
    private readonly int _index;

    public RemoveCommand(List<Annotation> list, Annotation annotation)
    {
        _list = list;
        _snapshot = annotation.Clone();
        _index = list.FindIndex(a => a.Id == annotation.Id);
    }

    public void Do() => _list.RemoveAll(a => a.Id == _snapshot.Id);

    public void Undo()
    {
        if (_list.Any(a => a.Id == _snapshot.Id)) return;
        _list.Insert(Math.Clamp(_index, 0, _list.Count), _snapshot.Clone());
    }
}

/// <summary>Move, resize or restyle: swaps an annotation between its before and after snapshots.</summary>
internal sealed class ReplaceCommand : IEditCommand
{
    private static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(800);

    private readonly List<Annotation> _list;
    private readonly Annotation _before;
    private Annotation _after;
    private readonly string? _mergeKey;
    private DateTime _time = DateTime.UtcNow;

    public ReplaceCommand(List<Annotation> list, Annotation before, Annotation after, string? mergeKey = null)
    {
        _list = list;
        _before = before.Clone();
        _after = after.Clone();
        _mergeKey = mergeKey;
    }

    public void Do() => Apply(_after);
    public void Undo() => Apply(_before);

    private void Apply(Annotation state)
    {
        int i = _list.FindIndex(a => a.Id == state.Id);
        if (i >= 0) _list[i] = state.Clone();
    }

    public bool TryMerge(IEditCommand next)
    {
        if (_mergeKey is null || next is not ReplaceCommand r || r._mergeKey != _mergeKey
            || r._before.Id != _before.Id || r._time - _time > MergeWindow)
            return false;
        _after = r._after;
        _time = r._time;
        return true;
    }
}

/// <summary>Non-destructive crop of one shot: the visible/exported region in image pixels, or null for the full image.</summary>
internal sealed class CropState
{
    public System.Drawing.RectangleF? Rect { get; set; }
}

internal sealed class CropCommand : IEditCommand
{
    private readonly CropState _state;
    private readonly System.Drawing.RectangleF? _before, _after;

    public CropCommand(CropState state, System.Drawing.RectangleF? before, System.Drawing.RectangleF? after)
    {
        _state = state;
        _before = before;
        _after = after;
    }

    public void Do() => _state.Rect = _after;
    public void Undo() => _state.Rect = _before;
}

/// <summary>"Clean": removes every annotation and the crop; undo brings them all back in their original order.</summary>
internal sealed class ClearCommand : IEditCommand
{
    private readonly List<Annotation> _list;
    private readonly CropState _crop;
    private readonly List<Annotation> _snapshot;
    private readonly System.Drawing.RectangleF? _cropSnapshot;

    public ClearCommand(List<Annotation> list, CropState crop)
    {
        _list = list;
        _crop = crop;
        _snapshot = list.Select(a => a.Clone()).ToList();
        _cropSnapshot = crop.Rect;
    }

    public void Do()
    {
        _list.Clear();
        _crop.Rect = null;
    }

    public void Undo()
    {
        _list.Clear();
        _list.AddRange(_snapshot.Select(a => a.Clone()));
        _crop.Rect = _cropSnapshot;
    }
}
