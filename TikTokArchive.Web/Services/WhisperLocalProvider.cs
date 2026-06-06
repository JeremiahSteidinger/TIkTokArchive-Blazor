using System.Text.Json;

namespace TikTokArchive.Web.Services
{
    public class WhisperLocalProvider : ISpeechToTextProvider
    {
        private readonly HttpClient _httpClient;
        private readonly string _whisperUrl;
        private readonly ILogger<WhisperLocalProvider> _logger;

        public string ProviderName => "WhisperLocal";

        public WhisperLocalProvider(HttpClient httpClient, IConfiguration configuration, ILogger<WhisperLocalProvider> logger)
        {
            _httpClient = httpClient;
            _whisperUrl = configuration["WHISPER_URL"] ?? "http://whisper:9000";
            _logger = logger;
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_whisperUrl}/");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Whisper service is not available at {WhisperUrl}", _whisperUrl);
                return false;
            }
        }

        public async Task<string> TranscribeAsync(string audioPath, string language = "en")
        {
            using var content = new MultipartFormDataContent();
            
            // Read the audio file
            var audioBytes = await File.ReadAllBytesAsync(audioPath);
            var audioContent = new ByteArrayContent(audioBytes);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            content.Add(audioContent, "audio_file", Path.GetFileName(audioPath));
            
            // Add language parameter
            content.Add(new StringContent(language), "language");
            
            // Add task parameter (transcribe vs translate)
            content.Add(new StringContent("transcribe"), "task");

            var response = await _httpClient.PostAsync($"{_whisperUrl}/asr?output=json", content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<WhisperResponse>(responseJson);

            return result?.Text ?? string.Empty;
        }

        private class WhisperResponse
        {
            public string? Text { get; set; }
        }
    }
}
