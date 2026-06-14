namespace TikTokArchive.Web.Services
{
    public enum TranscriptionOutcome
    {
        /// <summary>Audio was transcribed to text.</summary>
        Success,

        /// <summary>The file had no audio track or was silent — nothing to transcribe.</summary>
        NoAudio,

        /// <summary>The video file could not be found on disk.</summary>
        NotFound,

        /// <summary>A transient error (service down, timeout, non-2xx) — caller should retry.</summary>
        Error
    }

    /// <param name="Confidence">0–1 average token confidence from the engine, or null if unknown.</param>
    public record TranscriptionResult(TranscriptionOutcome Outcome, string? Text, double? Confidence, string? Error);

    /// <summary>
    /// Transcribes the audio of a video file to text via an external speech-to-text service.
    /// </summary>
    public interface ISpeechToTextService
    {
        Task<TranscriptionResult> TranscribeAsync(string videoFilePath, CancellationToken ct = default);
    }
}
