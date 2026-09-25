using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TikTokArchive.Entities;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Summary + tag generator backed by a local Ollama server. One non-streaming /api/chat
    /// call with format=json yields a single JSON object holding both the summary and the tags,
    /// so a video is enriched in one inference. Mirrors <see cref="WhisperAsrSpeechToTextService"/>:
    /// transient failures (service down, timeout, malformed JSON) surface as
    /// <see cref="SummaryOutcome.Error"/> so the worker retries under backoff.
    /// </summary>
    public class OllamaVideoSummaryService : IVideoSummaryService
    {
        public const string HttpClientName = "ai-enrichment";

        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly TikTokArchiveDbContext _dbContext;
        private readonly AiEnrichmentOptions _options;
        private readonly ILogger<OllamaVideoSummaryService> _logger;

        public OllamaVideoSummaryService(
            IHttpClientFactory httpClientFactory,
            TikTokArchiveDbContext dbContext,
            IOptions<AiEnrichmentOptions> options,
            ILogger<OllamaVideoSummaryService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _dbContext = dbContext;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<SummaryResult> SummarizeAsync(string description, string? transcript, CancellationToken ct = default)
        {
            var userContent = AiSummaryPromptHelper.BuildUserContent(_options, description, transcript);
            if (string.IsNullOrWhiteSpace(userContent))
            {
                // Nothing to work from — terminal, the worker marks it Skipped.
                return new SummaryResult(SummaryOutcome.Skipped, null, null, null);
            }

            var customPrompt = await _dbContext.AiProviderSettings
                .AsNoTracking()
                .Select(s => s.SystemPrompt)
                .FirstOrDefaultAsync(ct);

            var request = new OllamaChatRequest
            {
                Model = _options.Model,
                Stream = false,
                Format = "json",
                Options = new OllamaOptions { Temperature = 0.2 },
                Messages =
                [
                    new OllamaMessage { Role = "system", Content = AiSummaryPromptHelper.ResolveSystemPrompt(_options, customPrompt) },
                    new OllamaMessage { Role = "user", Content = userContent }
                ]
            };

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);

                using var content = new StringContent(
                    JsonSerializer.Serialize(request, SerializerOptions), Encoding.UTF8, "application/json");

                using var response = await client.PostAsync("api/chat", content, ct);
                response.EnsureSuccessStatusCode();

                var body = await response.Content.ReadAsStringAsync(ct);
                var result = ParseResponse(body);
                return result.Outcome == SummaryOutcome.Success
                    ? result with { Provider = AiProvider.Local, Model = _options.Model }
                    : result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Real application shutdown — propagate so the worker stops cleanly.
                throw;
            }
            catch (Exception ex)
            {
                // Service unreachable, HTTP timeout, non-2xx, etc. — transient; the worker retries.
                _logger.LogWarning(ex, "AI enrichment request failed");
                return new SummaryResult(SummaryOutcome.Error, null, null, ex.GetFullMessage());
            }
        }

        private SummaryResult ParseResponse(string body)
        {
            OllamaChatResponse? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<OllamaChatResponse>(body, SerializerOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse Ollama envelope");
                return new SummaryResult(SummaryOutcome.Error, null, null, "Malformed LLM response");
            }

            var inner = envelope?.Message?.Content;
            if (string.IsNullOrWhiteSpace(inner))
            {
                return new SummaryResult(SummaryOutcome.Error, null, null, "Empty LLM response");
            }

            AiSummaryPromptHelper.SummaryPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<AiSummaryPromptHelper.SummaryPayload>(inner, SerializerOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse LLM JSON content: {Content}", inner);
                return new SummaryResult(SummaryOutcome.Error, null, null, "Malformed LLM response");
            }

            var summary = payload?.Summary?.Trim();
            var tags = AiSummaryPromptHelper.CleanTags(_options, payload?.Tags);

            // The model produced nothing usable — terminal, not a transient error.
            if (string.IsNullOrWhiteSpace(summary) && tags.Count == 0)
            {
                return new SummaryResult(SummaryOutcome.Skipped, null, null, null);
            }

            return new SummaryResult(SummaryOutcome.Success, summary, tags, null);
        }

        private sealed class OllamaChatRequest
        {
            [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
            [JsonPropertyName("messages")] public List<OllamaMessage> Messages { get; set; } = [];
            [JsonPropertyName("stream")] public bool Stream { get; set; }
            [JsonPropertyName("format")] public string Format { get; set; } = "json";
            [JsonPropertyName("options")] public OllamaOptions? Options { get; set; }
        }

        private sealed class OllamaOptions
        {
            [JsonPropertyName("temperature")] public double Temperature { get; set; }
        }

        private sealed class OllamaMessage
        {
            [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
            [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
        }

        private sealed class OllamaChatResponse
        {
            [JsonPropertyName("message")] public OllamaMessage? Message { get; set; }
        }
    }
}
