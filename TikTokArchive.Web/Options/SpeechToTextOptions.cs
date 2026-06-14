namespace TikTokArchive.Web.Options
{
    public class SpeechToTextOptions
    {
        public const string SectionName = "SpeechToText";

        /// <summary>Master feature flag. When false, no transcription worker or client is registered.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Base URL of the Whisper ASR web service (the whisper-asr container).</summary>
        public string Url { get; set; } = "http://whisper-asr:9000";

        /// <summary>Forced language code (e.g. "en"). Null/empty lets Whisper auto-detect per video.</summary>
        public string? Language { get; set; }

        /// <summary>HTTP timeout for a single transcription. CPU transcription takes minutes, not seconds.</summary>
        public int TimeoutMinutes { get; set; } = 30;

        /// <summary>How many videos a single poll cycle processes before looping back.</summary>
        public int BatchSize { get; set; } = 4;

        /// <summary>
        /// Transcripts whose 0–1 average confidence is below this are flagged "low confidence"
        /// (logged and surfaced in the UI) — typically laughter, music, or background noise that
        /// the engine transcribes poorly. ~exp(-0.7); raise it to flag more aggressively.
        /// </summary>
        public double LowConfidenceThreshold { get; set; } = 0.5;
    }
}
