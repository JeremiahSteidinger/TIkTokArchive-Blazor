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
    /// Summary + tag generator backed by the Google Gemini API. One generateContent call with
    /// responseMimeType=application/json yields a single JSON object holding both the summary
    /// and the tags, mirroring <see cref="OllamaVideoSummaryService"/>. The API key is read from
    /// the DB-backed <see cref="AiProviderSettings"/> row (admin-editable at runtime) rather than
    /// IOptions, since it can change without an app restart. Transient failures (unreachable,
    /// timeout, malformed JSON, missing key) surface as <see cref="SummaryOutcome.Error"/> so the
    /// worker retries under backoff.
    /// </summary>
    public class GeminiVideoSummaryService : IVideoSummaryService
    {
        public const string HttpClientName = "gemini";

        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly TikTokArchiveDbContext _dbContext;
        private readonly AiEnrichmentOptions _options;
        private readonly ILogger<GeminiVideoSummaryService> _logger;

        public GeminiVideoSummaryService(
            IHttpClientFactory httpClientFactory,
            TikTokArchiveDbContext dbContext,
            IOptions<AiEnrichmentOptions> options,
            ILogger<GeminiVideoSummaryService> logger)
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

            var settings = await _dbContext.AiProviderSettings
                .AsNoTracking()
                .Select(s => new { s.GeminiApiKey, s.GeminiModel, s.SystemPrompt })
                .FirstOrDefaultAsync(ct);
            var apiKey = settings?.GeminiApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                // Not a transient condition, but Error (not Skipped) so the worker keeps retrying
                // under backoff — an admin fixing the key later should still get this video enriched.
                return new SummaryResult(SummaryOutcome.Error, null, null, "Gemini API key is not configured");
            }

            // The admin-picked model (from GeminiModelCatalogService) wins; falls back to the
            // appsettings-bound default when nothing has been picked yet.
            var model = string.IsNullOrWhiteSpace(settings?.GeminiModel) ? _options.GeminiModel : settings.GeminiModel;

            var request = new GeminiRequest
            {
                SystemInstruction = new GeminiContent { Parts = [new GeminiPart { Text = AiSummaryPromptHelper.ResolveSystemPrompt(_options, settings?.SystemPrompt) }] },
                Contents = [new GeminiContent { Parts = [new GeminiPart { Text = userContent }] }],
                GenerationConfig = new GeminiGenerationConfig { Temperature = 0.2, ResponseMimeType = "application/json" }
            };

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);

                using var content = new StringContent(
                    JsonSerializer.Serialize(request, SerializerOptions), Encoding.UTF8, "application/json");

                using var httpRequest = new HttpRequestMessage(
                    HttpMethod.Post, $"v1beta/models/{model}:generateContent")
                {
                    Content = content
                };
                httpRequest.Headers.Add("x-goog-api-key", apiKey);

                using var response = await client.SendAsync(httpRequest, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Gemini request failed with {StatusCode}: {Body}", response.StatusCode, body);
                    return new SummaryResult(SummaryOutcome.Error, null, null, $"Gemini returned {(int)response.StatusCode}");
                }

                var result = ParseResponse(body);
                return result.Outcome == SummaryOutcome.Success
                    ? result with { Provider = AiProvider.Gemini, Model = model }
                    : result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Real application shutdown — propagate so the worker stops cleanly.
                throw;
            }
            catch (Exception ex)
            {
                // Unreachable, HTTP timeout, etc. — transient; the worker retries.
                _logger.LogWarning(ex, "Gemini enrichment request failed");
                return new SummaryResult(SummaryOutcome.Error, null, null, ex.GetFullMessage());
            }
        }

        private SummaryResult ParseResponse(string body)
        {
            GeminiResponse? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<GeminiResponse>(body, SerializerOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse Gemini envelope");
                return new SummaryResult(SummaryOutcome.Error, null, null, "Malformed LLM response");
            }

            var inner = envelope?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
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

        private sealed class GeminiRequest
        {
            [JsonPropertyName("systemInstruction")] public GeminiContent? SystemInstruction { get; set; }
            [JsonPropertyName("contents")] public List<GeminiContent> Contents { get; set; } = [];
            [JsonPropertyName("generationConfig")] public GeminiGenerationConfig? GenerationConfig { get; set; }
        }

        private sealed class GeminiGenerationConfig
        {
            [JsonPropertyName("temperature")] public double Temperature { get; set; }
            [JsonPropertyName("responseMimeType")] public string ResponseMimeType { get; set; } = "application/json";
        }

        private sealed class GeminiContent
        {
            [JsonPropertyName("parts")] public List<GeminiPart> Parts { get; set; } = [];
        }

        private sealed class GeminiPart
        {
            [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
        }

        private sealed class GeminiResponse
        {
            [JsonPropertyName("candidates")] public List<GeminiCandidate>? Candidates { get; set; }
        }

        private sealed class GeminiCandidate
        {
            [JsonPropertyName("content")] public GeminiContent? Content { get; set; }
        }
    }
}
