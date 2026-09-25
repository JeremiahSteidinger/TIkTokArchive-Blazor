namespace TikTokArchive.Web.Options
{
    public class AiEnrichmentOptions
    {
        public const string SectionName = "AiEnrichment";

        /// <summary>Master feature flag. When false, no enrichment worker or LLM client is registered.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Base URL of the Ollama server (the ollama container).</summary>
        public string Url { get; set; } = "http://ollama:11434";

        /// <summary>Ollama model tag used for summarization + tagging. A small instruct model is plenty.</summary>
        public string Model { get; set; } = "llama3.2:3b";

        /// <summary>Gemini model used for summarization + tagging when the Gemini provider is selected.</summary>
        public string GeminiModel { get; set; } = "gemini-3.8-flash";

        /// <summary>HTTP timeout for a single enrichment. CPU inference of a small model takes seconds to tens of seconds.</summary>
        public int TimeoutMinutes { get; set; } = 10;

        /// <summary>How many videos a single poll cycle processes before looping back.</summary>
        public int BatchSize { get; set; } = 4;

        /// <summary>Upper bound on AI tags kept per video; the LLM is asked for this many at most.</summary>
        public int MaxTags { get; set; } = 5;

        /// <summary>Lower bound communicated to the LLM (it's asked for this many at least).</summary>
        public int MinTags { get; set; } = 3;

        /// <summary>Target sentence count for the summary, communicated to the LLM.</summary>
        public int MaxSummarySentences { get; set; } = 5;

        /// <summary>
        /// Transcripts are truncated to this many characters before being sent to the LLM, to
        /// bound prompt size (and inference time) on very long videos.
        /// </summary>
        public int TranscriptCharLimit { get; set; } = 8000;
    }
}
