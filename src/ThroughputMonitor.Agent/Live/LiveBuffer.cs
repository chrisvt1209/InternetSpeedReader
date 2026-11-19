using ThroughputMonitor.Core.Ipc;

namespace ThroughputMonitor.Agent.Live;

/// <summary>The most recent seconds of traffic, kept in memory (a ring: the oldest point is overwritten).</summary>
public sealed class LiveBuffer(int capacity)
{
    private readonly LivePoint[] _ring = new LivePoint[Math.Max(1, capacity)];
    private int _next;
    private int _count;

    public int Count => _count;

    public void Add(LivePoint point)
    {
        _ring[_next] = point;
        _next = (_next + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
    }

    public LivePoint? Latest => _count == 0 ? null : _ring[(_next - 1 + _ring.Length) % _ring.Length];

    /// <summary>Points oldest first.</summary>
    public IReadOnlyList<LivePoint> ToList()
    {
        var list = new List<LivePoint>(_count);
        int start = _count < _ring.Length ? 0 : _next;
        for (int i = 0; i < _count; i++)
            list.Add(_ring[(start + i) % _ring.Length]);
        return list;
    }
}
