using System.Collections.Concurrent;

namespace TikTokArchive.Web.Services
{
    public enum WorkerRunState
    {
        /// <summary>The worker is gated off behind a feature flag and isn't running.</summary>
        Disabled,
        /// <summary>Running but not currently processing anything.</summary>
        Idle,
        /// <summary>Actively processing an item right now.</summary>
        Working
    }

    /// <summary>Immutable point-in-time view of one background worker, read by the monitor page.</summary>
    public sealed record WorkerSnapshot
    {
        public string Key { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public bool Enabled { get; init; }
        public WorkerRunState State { get; init; }

        /// <summary>Primary label of the in-flight item — a file name, video id, or URL.</summary>
        public string? CurrentItem { get; init; }
        /// <summary>Secondary context for the in-flight item, e.g. a description snippet.</summary>
        public string? CurrentDetail { get; init; }
        /// <summary>The stage the worker is in for this item, e.g. "Transcribing", "Downloading".</summary>
        public string? CurrentStep { get; init; }
        /// <summary>When the current item started (UTC); null when idle.</summary>
        public DateTime? StartedAt { get; init; }

        /// <summary>When the worker last finished an item (UTC).</summary>
        public DateTime? LastActiveAt { get; init; }
        /// <summary>Items attempted since the app started.</summary>
        public long ProcessedCount { get; init; }
        /// <summary>The most recent error message reported by the worker, if any.</summary>
        public string? LastError { get; init; }

        /// <summary>Stable display order, assigned at registration.</summary>
        public int Order { get; init; }
    }

    /// <summary>
    /// In-memory, process-wide registry of what each background worker is doing right now.
    /// Workers report begin/step/complete as they process items; the monitor page reads the
    /// snapshots. State lives only in memory — it's a live activity view, not durable history
    /// (the Admin page already surfaces the persisted queues), so nothing here survives a restart.
    /// A single instance is shared across every worker, so all mutations are thread-safe.
    /// </summary>
    public class BackgroundTaskMonitor
    {
        private readonly ConcurrentDictionary<string, WorkerSnapshot> _workers = new();
        private int _order;

        /// <summary>
        /// Declares a worker so it appears on the monitor page even before it processes anything
        /// (and even when disabled). Called once per worker at startup.
        /// </summary>
        public void Register(string key, string displayName, string description, bool enabled)
        {
            var order = Interlocked.Increment(ref _order);
            _workers[key] = new WorkerSnapshot
            {
                Key = key,
                DisplayName = displayName,
                Description = description,
                Enabled = enabled,
                State = enabled ? WorkerRunState.Idle : WorkerRunState.Disabled,
                Order = order
            };
        }

        /// <summary>Marks the worker as actively processing a new item.</summary>
        public void BeginItem(string key, string? item, string? detail = null, string? step = null) =>
            Mutate(key, s => s with
            {
                State = WorkerRunState.Working,
                CurrentItem = item,
                CurrentDetail = detail,
                CurrentStep = step,
                StartedAt = DateTime.UtcNow
            });

        /// <summary>Updates the stage label of the item currently being processed.</summary>
        public void UpdateStep(string key, string step) =>
            Mutate(key, s => s with { CurrentStep = step });

        /// <summary>
        /// Marks the current item finished: bumps the processed count, returns the worker to idle,
        /// and (optionally) records the error that ended this item.
        /// </summary>
        public void CompleteItem(string key, string? error = null) =>
            Mutate(key, s => s with
            {
                State = s.Enabled ? WorkerRunState.Idle : WorkerRunState.Disabled,
                CurrentItem = null,
                CurrentDetail = null,
                CurrentStep = null,
                StartedAt = null,
                LastActiveAt = DateTime.UtcNow,
                ProcessedCount = s.ProcessedCount + 1,
                LastError = error ?? s.LastError
            });

        public IReadOnlyList<WorkerSnapshot> GetSnapshots() =>
            _workers.Values.OrderBy(w => w.Order).ToList();

        /// <summary>Trims text to a single short line for display next to the in-flight item.</summary>
        public static string? Snippet(string? text, int max = 80)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var oneLine = text.ReplaceLineEndings(" ").Trim();
            return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
        }

        // The update delegate must be pure on its input: ConcurrentDictionary may invoke it more
        // than once under contention, applying the winner. The add branch tolerates a worker that
        // reports before Register ran (shouldn't happen, but keeps reporting from ever throwing).
        private void Mutate(string key, Func<WorkerSnapshot, WorkerSnapshot> update) =>
            _workers.AddOrUpdate(
                key,
                _ => update(new WorkerSnapshot
                {
                    Key = key,
                    DisplayName = key,
                    Enabled = true,
                    State = WorkerRunState.Idle,
                    Order = Interlocked.Increment(ref _order)
                }),
                (_, existing) => update(existing));
    }
}
