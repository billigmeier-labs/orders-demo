using Orders.Console.Domain;

namespace Orders.Console.Events;

/// <summary>In-memory, de-duplicated log of everything the console has observed.</summary>
public sealed class EventStore
{
    private const int Capacity = 5000;
    private readonly Lock _gate = new();
    private readonly List<ConsoleEvent> _events = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public int Add(IEnumerable<ConsoleEvent> events)
    {
        var added = 0;
        lock (_gate)
        {
            foreach (var e in events)
            {
                if (_keys.Add(e.Key))
                {
                    _events.Add(e);
                    added++;
                }
            }

            if (added > 0)
            {
                _events.Sort((a, b) => a.At.CompareTo(b.At));
                if (_events.Count > Capacity)
                {
                    var drop = _events.Count - Capacity;
                    foreach (var old in _events.Take(drop))
                    {
                        _keys.Remove(old.Key);
                    }

                    _events.RemoveRange(0, drop);
                }
            }
        }

        return added;
    }

    public void Add(ConsoleEvent e) => Add([e]);

    public IReadOnlyList<ConsoleEvent> Since(DateTimeOffset since)
    {
        lock (_gate)
        {
            return _events.Where(e => e.At >= since).ToList();
        }
    }

    public IReadOnlyList<ConsoleEvent> Latest(int count)
    {
        lock (_gate)
        {
            return _events.Skip(Math.Max(0, _events.Count - count)).Reverse().ToList();
        }
    }

    public int Count(string kind, DateTimeOffset since)
    {
        lock (_gate)
        {
            return _events.Count(e => e.Kind == kind && e.At >= since);
        }
    }
}
