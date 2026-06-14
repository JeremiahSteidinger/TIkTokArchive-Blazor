namespace TikTokArchive.Entities
{
    /// <summary>
    /// Tracks the speech-to-text transcription state of a <see cref="Video"/>.
    /// The transcription worker only ever claims <see cref="Pending"/> and
    /// <see cref="Failed"/> rows; the other states are terminal.
    /// </summary>
    public enum TranscriptStatus
    {
        /// <summary>Queued for transcription. The CLR default (0), so new videos start here.</summary>
        Pending = 0,

        /// <summary>Transcribed successfully; <see cref="Video.Transcript"/> is populated.</summary>
        Completed = 1,

        /// <summary>A transient error occurred; the worker retries under capped exponential backoff.</summary>
        Failed = 2,

        /// <summary>Attempted but no audio / silent (or the file went missing) — terminal, never retried.</summary>
        Skipped = 3,

        /// <summary>
        /// Never queued for transcription (e.g. the existing back-catalog when the feature was
        /// switched on for new videos only). An admin action can flip these to <see cref="Pending"/>.
        /// </summary>
        NotRequested = 4
    }
}
