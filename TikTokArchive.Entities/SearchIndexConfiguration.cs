namespace TikTokArchive.Entities
{
    public class SearchIndexConfiguration
    {
        public int Id { get; set; }
        public int SyncIntervalMinutes { get; set; } = 30;
        public DateTime LastModified { get; set; }
        
        // Speech-to-Text Configuration
        public string SttProvider { get; set; } = "None";
        public string? WhisperUrl { get; set; }
        public string? WhisperModel { get; set; }
        public string? OpenAiApiKey { get; set; }
        public string? AzureSpeechKey { get; set; }
        public string? AzureSpeechRegion { get; set; }
    }
}
