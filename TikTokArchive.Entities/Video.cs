using System.ComponentModel.DataAnnotations.Schema;

namespace TikTokArchive.Entities
{
    public class Video
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public Platform Platform { get; set; }
        // The platform's video id (TikTok numeric id or Instagram shortcode), regardless of
        // platform — the column predates Instagram support and keeps its original name.
        public string TikTokVideoId { get; set; }
        // Canonical watch URL captured at ingest (yt-dlp webpage_url, else the submitted URL).
        // Null on rows archived before this column existed; PlatformUrl reconstructs those.
        public string? SourceUrl { get; set; }
        public string Description { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime AddedToApp { get; set; }

        // Speech-to-text. The transcription worker fills these in asynchronously after
        // ingest; Transcript is indexed by the search engine so spoken content is searchable.
        public TranscriptStatus TranscriptStatus { get; set; }
        public string? Transcript { get; set; }
        // 0–1 average token confidence from the speech-to-text engine; null if unknown
        // (not transcribed, or transcribed before confidence was captured).
        public double? TranscriptConfidence { get; set; }
        public int TranscriptRetryCount { get; set; }
        public DateTime? TranscriptLastAttempt { get; set; }
        public string? TranscriptErrorMessage { get; set; }

        // AI enrichment. After the transcript is ready, the enrichment worker makes a single
        // LLM call (Local/Ollama or Gemini, per AiProviderSettings) that fills in Summary and the
        // video's AI-sourced tags (VideoTags with Source = Ai). One status covers both outputs
        // since they come from the same call.
        public AiSummaryStatus AiSummaryStatus { get; set; }
        public string? Summary { get; set; }
        public int AiSummaryRetryCount { get; set; }
        public DateTime? AiSummaryLastAttempt { get; set; }
        public string? AiSummaryErrorMessage { get; set; }

        // Which provider/model produced the current Summary — set on a successful enrichment,
        // left as-is (not cleared) while a re-run is pending/failed. Null means never successfully
        // enriched. Lets the admin UI find/re-queue videos enriched by a specific provider, e.g.
        // to re-run everything the local model summarized through Gemini instead.
        public AiProvider? AiSummaryProvider { get; set; }
        public string? AiSummaryModel { get; set; }

        public virtual Creator Creator { get; set; }
        public virtual IEnumerable<VideoTag> Tags { get; set; }
    }
}
