namespace TikTokArchive.Entities
{
    /// <summary>Which backend transcribes a video's audio (<see cref="AiProviderSettings.TranscriptionProvider"/>).</summary>
    public enum TranscriptionEngine
    {
        /// <summary>The local Whisper ASR container. The CLR default (0).</summary>
        Whisper = 0,

        /// <summary>A general Gemini model, sent the whole video via generateContent.</summary>
        Gemini = 1,

        /// <summary>Google's dedicated speech-to-text model (gemini-3.5-transcribe), sent only the extracted audio.</summary>
        GeminiTranscribe = 2
    }
}
