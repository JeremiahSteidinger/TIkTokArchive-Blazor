namespace TikTokArchive.Entities
{
    /// <summary>Which backend generates AI summaries and tags for a video.</summary>
    public enum AiProvider
    {
        /// <summary>The local Ollama server. The CLR default (0).</summary>
        Local = 0,

        /// <summary>The Google Gemini API, using the key stored on <see cref="AiProviderSettings"/>.</summary>
        Gemini = 1
    }
}
