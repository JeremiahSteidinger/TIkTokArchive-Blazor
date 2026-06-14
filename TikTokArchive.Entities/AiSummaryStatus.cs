namespace TikTokArchive.Entities
{
    /// <summary>
    /// Tracks the AI-enrichment state of a <see cref="Video"/> — a single LLM pass that
    /// produces both the <see cref="Video.Summary"/> and the video's AI-sourced tags.
    /// The enrichment worker only ever claims <see cref="Pending"/> and <see cref="Failed"/>
    /// rows; the other states are terminal. Mirrors <see cref="TranscriptStatus"/>.
    /// </summary>
    public enum AiSummaryStatus
    {
        /// <summary>Queued for enrichment. The CLR default (0), so new videos start here.</summary>
        Pending = 0,

        /// <summary>Enriched successfully; <see cref="Video.Summary"/> and AI tags are populated.</summary>
        Completed = 1,

        /// <summary>A transient error occurred; the worker retries under capped exponential backoff.</summary>
        Failed = 2,

        /// <summary>No usable text to summarize (empty description and no transcript) — terminal, never retried.</summary>
        Skipped = 3,

        /// <summary>
        /// Never queued for enrichment (e.g. the existing back-catalog when the feature was
        /// switched on for new videos only). An admin action can flip these to <see cref="Pending"/>.
        /// </summary>
        NotRequested = 4
    }
}
