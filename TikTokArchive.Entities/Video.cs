using System.ComponentModel.DataAnnotations.Schema;

namespace TikTokArchive.Entities
{
    public class Video
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public string TikTokVideoId { get; set; }
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
        // local-LLM call that fills in Summary and the video's AI-sourced tags (VideoTags with
        // Source = Ai). One status covers both outputs since they come from the same call.
        public AiSummaryStatus AiSummaryStatus { get; set; }
        public string? Summary { get; set; }
        public int AiSummaryRetryCount { get; set; }
        public DateTime? AiSummaryLastAttempt { get; set; }
        public string? AiSummaryErrorMessage { get; set; }

        public virtual Creator Creator { get; set; }
        public virtual IEnumerable<VideoTag> Tags { get; set; }
    }
}
