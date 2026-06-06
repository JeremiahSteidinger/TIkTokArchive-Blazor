namespace TikTokArchive.Web.Services
{
    public interface ISpeechToTextProvider
    {
        Task<string> TranscribeAsync(string audioPath, string language = "en");
        Task<bool> IsAvailableAsync();
        string ProviderName { get; }
    }
}
