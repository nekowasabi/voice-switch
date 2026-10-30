namespace VoiceSwitch.Windows.Core;

public sealed record DictationHistoryEntry(Guid Id, string Text, WakeOrigin Origin, DateTimeOffset CreatedAt);

public sealed class DictationHistory
{
    public static DictationHistory Shared { get; } = new();
    private readonly object sync = new();
    private readonly List<DictationHistoryEntry> entries = new();
    private readonly Func<DateTimeOffset> now;
    private readonly int capacity;
    private long generation;
    public long Generation { get { lock (sync) return generation; } }
    private readonly TimeSpan retention;
    public event Action? Changed;
    public DictationJournal? Journal { get; set; }

    public DictationHistory(Func<DateTimeOffset>? now = null, int capacity = 50, TimeSpan? retention = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.retention = retention ?? TimeSpan.FromMinutes(30);
        if (this.retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.capacity = capacity;
    }

    public bool Add(Guid id, string text, WakeOrigin origin, long? expectedGeneration = null, AutomaticTranscriptPolicy policy = default, string? endReason = null, double? recordingSeconds = null, bool manual = false, FocusApp? focus = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000 || policy.Rejects(text, origin)) return false;
        DateTimeOffset created;
        lock (sync)
        {
            Prune();
            if (expectedGeneration is not null && expectedGeneration != generation) return false;
            if (entries.Any(entry => entry.Id == id)) return false;
            created = now();
            entries.Add(new(id, text, origin, created));
            while (entries.Count > capacity) entries.RemoveAt(0);
        }

        var noted = JournalNotes.Take(id);
        var journal = Journal ?? (ReferenceEquals(this, Shared) ? DictationJournal.Active : null);
        try
        {
            journal?.Append(new JournalEntry(id, created, text, DictationJournal.OriginLabel(origin, manual || noted.Manual), endReason ?? noted.EndReason, recordingSeconds ?? noted.RecordingSeconds, focus ?? noted.Focus));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The utterance is already in memory. A journal failure must not drop it.
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
        lock (sync) { entries.Clear(); generation++; }
        Changed?.Invoke();
    }

    private void Prune()
    {
        var instant = now();
        entries.RemoveAll(entry => instant < entry.CreatedAt || instant - entry.CreatedAt >= retention);
    }
}
