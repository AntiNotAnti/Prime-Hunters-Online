using System.Threading;
namespace MphRead.Mods.Network.Telemetry;

/// <summary>Preallocated low-contention ring. Producers never wait for the
/// consumer or each other; contention and fullness both drop only telemetry.</summary>
internal sealed class NetTelemetryQueue
{
    private readonly NetTelemetryEvent[] _events;
    private readonly object _gate = new();
    private int _head, _tail, _count;
    public int Count => Volatile.Read(ref _count);
    public NetTelemetryQueue(int capacity) => _events = new NetTelemetryEvent[capacity];
    public bool TryWrite(in NetTelemetryEvent item)
    {
        if (!Monitor.TryEnter(_gate)) return false;
        try
        {
            if (_count == _events.Length) return false;
            _events[_tail] = item; _tail = (_tail + 1) % _events.Length;
            Volatile.Write(ref _count, _count + 1); return true;
        }
        finally { Monitor.Exit(_gate); }
    }
    public bool TryRead(out NetTelemetryEvent item)
    {
        item = default;
        if (!Monitor.TryEnter(_gate)) return false;
        try
        {
            if (_count == 0) return false;
            item = _events[_head]; _head = (_head + 1) % _events.Length;
            Volatile.Write(ref _count, _count - 1); return true;
        }
        finally { Monitor.Exit(_gate); }
    }
}
