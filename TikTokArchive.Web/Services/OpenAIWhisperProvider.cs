using System.Net.Http.Headers;
using System.Text.Json;

namespace TikTokArchive.Web.Services
{
    public class OpenAIWhisperProvider : ISpeechToTextProvider
    {
        private readonly HttpClient _httpClient;
        private readonly string? _apiKey;
        private readonly ILogger<OpenAIWhisperProvider> _logger;

        public string ProviderName => "OpenAI";

        public OpenAIWhisperProvider(HttpClient httpClient, IConfiguration configuration, ILogger<OpenAIWhisperProvider> logger)
        {
            _httpClient = httpClient;
            _apiKey = configuration["OPENAI_API_KEY"];
            _logger = logger;

            if (!string.IsNullOrEmpty(_apiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }
        }

        public async Task<bool> IsAvailableAsync()
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                _logger.LogWarning("OpenAI API key is not configured");
                return false;
            }

            try
            {
                // Test API key validity with a simple request
                var response = await _httpClient.GetAsync("https://api.openai.com/v1/models");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenAI API is not available");
                return false;
            }
        }

        public async Task<string> TranscribeAsync(string audioPath, string language = "en")
        {
            using var content = new MultipartFormDataContent();

            // Read the audio file
            var audioBytes = await File.ReadAllBytesAsync(audioPath);
            var audioContent = new ByteArrayContent(audioBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            content.Add(audioContent, "file", Path.GetFileName(audioPath));

            // Add model parameter
            content.Add(new StringContent("whisper-1"), "model");

            // Add language parameter
            content.Add(new StringContent(language), "language");

            var response = await _httpClient.PostAsync("https://api.openai.com/v1/audio/transcriptions", content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<OpenAIResponse>(responseJson);

            return result?.Text ?? string.Empty;
        }

        private class OpenAIResponse
        {
            public string? Text { get; set; }
        }
    }
}
