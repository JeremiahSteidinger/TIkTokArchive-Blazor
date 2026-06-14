namespace TikTokArchive.Web.Services
{
    public enum SummaryOutcome
    {
        /// <summary>The LLM produced a summary and/or tags.</summary>
        Success,

        /// <summary>There was no usable text to summarize, or the model returned nothing — terminal.</summary>
        Skipped,

        /// <summary>A transient error (service down, timeout, malformed response) — caller should retry.</summary>
        Error
    }

    /// <param name="Tags">Cleaned AI tags (lowercased, '#'-stripped, ≤100 chars, capped), or null.</param>
    public record SummaryResult(SummaryOutcome Outcome, string? Summary, IReadOnlyList<string>? Tags, string? Error);

    /// <summary>
    /// Generates a short summary and a handful of topical tags for a video from its
    /// description and (when available) transcript, via a local LLM.
    /// </summary>
    public interface IVideoSummaryService
    {
        Task<SummaryResult> SummarizeAsync(string description, string? transcript, CancellationToken ct = default);
    }
}
