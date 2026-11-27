namespace GlassDesk.AppServices;

public sealed class StageWorkspace
{
    private readonly Dictionary<nint, List<nint>> _groups = [];
    private readonly Dictionary<nint, nint> _membership = [];
    private readonly List<nint> _order = [];
    private long _nextGroup;
    private long _generation;

    public nint Active { get; private set; }
    public IReadOnlyList<nint> Background => _order.Where(group => group != Active).ToArray();

    public nint Add(nint window)
    {
        if (_membership.TryGetValue(window, out var group)) return group;
        group = checked((nint)(++_nextGroup));
        _groups.Add(group, [window]);
        _order.Add(group);
        _membership.Add(window, group);
        if (_groups.Count == 1) Active = group;
        return group;
    }

    public nint GroupOf(nint window) => _membership.GetValueOrDefault(window);
    public IReadOnlyList<nint> Members(nint group) => _groups.TryGetValue(group, out var members) ? members : [];

    public void Remove(nint window)
    {
        if (!_membership.Remove(window, out var group)) return;
        var members = _groups[group];
        members.Remove(window);
        if (members.Count != 0) return;
        _groups.Remove(group);
        _order.Remove(group);
        if (Active == group) Active = 0;
    }

    public StageSwitch BeginSwitch(nint group) => new(++_generation, group);

    public bool Commit(StageSwitch request)
    {
        if (request.Generation != _generation || !_groups.ContainsKey(request.Group)) return false;
        if (Active != 0 && request.Group != Active)
        {
            var outgoing = _order.IndexOf(Active);
            var incoming = _order.IndexOf(request.Group);
            (_order[outgoing], _order[incoming]) = (_order[incoming], _order[outgoing]);
        }
        Active = request.Group;
        return true;
    }

    public bool MergeIntoActive(nint group)
    {
        if (Active == 0 || group == Active || !_groups.Remove(group, out var members)) return false;
        _order.Remove(group);
        foreach (var member in members)
        {
            _membership[member] = Active;
            _groups[Active].Add(member);
        }
        return true;
    }

    public nint Split(nint window)
    {
        var source = GroupOf(window);
        if (source == 0 || _groups[source].Count < 2) return source;
        Remove(window);
        return Add(window);
    }
}

public readonly record struct StageSwitch(long Generation, nint Group);
