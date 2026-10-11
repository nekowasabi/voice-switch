namespace VoiceSwitch.Windows.Core;

public sealed record DictationHistoryEntry(Guid Id, string Text, WakeOrigin Origin, DateTimeOffset CreatedAt);

public sealed class DictationHistory
{
    public static DictationHistory Shared { get; } = new();
    private readonly object sync = new();
    private readonly List<DictationHistoryEntry> entries = new();
    private readonly Func<DateTimeOffset> now;
    private readonly int capacity;
    private readonly TimeSpan retention;
    public event Action? Changed;

    public DictationHistory(Func<DateTimeOffset>? now = null, int capacity = 50, TimeSpan? retention = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.retention = retention ?? TimeSpan.FromMinutes(30);
        if (this.retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.capacity = capacity;
    }

    public bool Add(Guid id, string text, WakeOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        lock (sync)
        {
            Prune();
            if (entries.Any(entry => entry.Id == id)) return false;
            entries.Add(new(id, text, origin, now()));
            while (entries.Count > capacity) entries.RemoveAt(0);
        }
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<DictationHistoryEntry> Snapshot()
    {
        lock (sync) { Prune(); return entries.AsEnumerable().Reverse().ToArray(); }
    }

    public string? Select(Guid id)
    {
        lock (sync) { Prune(); return entries.FirstOrDefault(entry => entry.Id == id)?.Text; }
    }

    public void Clear()
    {
        lock (sync) entries.Clear();
        Changed?.Invoke();
    }

    private void Prune() => entries.RemoveAll(entry => now() - entry.CreatedAt >= retention);
}
