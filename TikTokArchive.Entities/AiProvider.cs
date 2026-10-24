namespace TikTokArchive.Entities
{
    /// <summary>
    /// Which backend does a piece of AI work for a video — summaries/tags
    /// (<see cref="AiProviderSettings.Provider"/>) or transcription
    /// (<see cref="AiProviderSettings.TranscriptionProvider"/>).
    /// </summary>
    public enum AiProvider
    {
        /// <summary>The local container: Ollama for summaries, Whisper ASR for transcription. The CLR default (0).</summary>
        Local = 0,

        /// <summary>The Google Gemini API, using the key stored on <see cref="AiProviderSettings"/>.</summary>
        Gemini = 1
    }
}
