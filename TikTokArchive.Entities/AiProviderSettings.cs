namespace TikTokArchive.Entities
{
    /// <summary>
    /// Single-row, admin-editable configuration for the AI enrichment provider. Unlike the
    /// appsettings-bound AiEnrichmentOptions, this lives in the DB so the active provider (and
    /// the Gemini key) can be changed from the UI without restarting the app.
    /// Mirrors <see cref="SearchIndexConfiguration"/>.
    /// </summary>
    public class AiProviderSettings
    {
        public int Id { get; set; }
        public AiProvider Provider { get; set; } = AiProvider.Local;

        /// <summary>API key for the Gemini API. Only required when <see cref="Provider"/> is Gemini.</summary>
        public string? GeminiApiKey { get; set; }

        /// <summary>
        /// Gemini model id (e.g. "gemini-3.8-flash"), picked from the admin UI's fetched model
        /// list. Null falls back to AiEnrichmentOptions.GeminiModel's appsettings-bound default.
        /// </summary>
        public string? GeminiModel { get; set; }

        /// <summary>
        /// Admin-edited system prompt sent to whichever provider is active. Null/empty falls back
        /// to AiSummaryPromptHelper.BuildSystemPrompt's generated default. Shared across providers
        /// (it's the instruction, not a provider setting), unlike GeminiApiKey/GeminiModel.
        /// </summary>
        public string? SystemPrompt { get; set; }

        public DateTime LastModified { get; set; }
    }
}
