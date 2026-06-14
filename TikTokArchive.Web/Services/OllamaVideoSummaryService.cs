using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
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
        private readonly AiEnrichmentOptions _options;
        private readonly ILogger<OllamaVideoSummaryService> _logger;

        public OllamaVideoSummaryService(
            IHttpClientFactory httpClientFactory,
            IOptions<AiEnrichmentOptions> options,
            ILogger<OllamaVideoSummaryService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<SummaryResult> SummarizeAsync(string description, string? transcript, CancellationToken ct = default)
        {
            var userContent = BuildUserContent(description, transcript);
            if (string.IsNullOrWhiteSpace(userContent))
            {
                // Nothing to work from — terminal, the worker marks it Skipped.
                return new SummaryResult(SummaryOutcome.Skipped, null, null, null);
            }

            var request = new OllamaChatRequest
            {
                Model = _options.Model,
                Stream = false,
                Format = "json",
                Options = new OllamaOptions { Temperature = 0.2 },
                Messages =
                [
                    new OllamaMessage { Role = "system", Content = BuildSystemPrompt() },
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
                return ParseResponse(body);
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

        private string BuildSystemPrompt() =>
            $"You summarize short videos from their caption and spoken transcript. " +
            $"Respond with ONLY a JSON object of the form {{\"summary\": string, \"tags\": string[]}}. " +
            $"The summary must be {_options.MaxSummarySentences} sentences or fewer, describing what the video is about. " +
            $"Provide between {_options.MinTags} and {_options.MaxTags} topical tags that best fit the video. " +
            $"Each tag must be lowercase, a single word or hyphenated-phrase (no spaces), and contain no '#'. " +
            $"Do not add any text outside the JSON object.";

        private string BuildUserContent(string description, string? transcript)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(description))
            {
                builder.Append("Caption: ").AppendLine(description.Trim());
            }

            if (!string.IsNullOrWhiteSpace(transcript))
            {
                var trimmed = transcript.Trim();
                if (trimmed.Length > _options.TranscriptCharLimit)
                {
                    trimmed = trimmed[.._options.TranscriptCharLimit];
                }

                builder.Append("Transcript: ").AppendLine(trimmed);
            }

            return builder.ToString().Trim();
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

            SummaryPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<SummaryPayload>(inner, SerializerOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse LLM JSON content: {Content}", inner);
                return new SummaryResult(SummaryOutcome.Error, null, null, "Malformed LLM response");
            }

            var summary = payload?.Summary?.Trim();
            var tags = CleanTags(payload?.Tags);

            // The model produced nothing usable — terminal, not a transient error.
            if (string.IsNullOrWhiteSpace(summary) && tags.Count == 0)
            {
                return new SummaryResult(SummaryOutcome.Skipped, null, null, null);
            }

            return new SummaryResult(SummaryOutcome.Success, summary, tags, null);
        }

        // Normalize LLM tags to share identity with TikTok tags: lowercase, '#'-stripped,
        // spaces collapsed to hyphens, deduplicated, length-bounded (Tag.Name is MaxLength 100),
        // and capped at MaxTags.
        private List<string> CleanTags(IEnumerable<string>? rawTags)
        {
            var cleaned = new List<string>();
            if (rawTags == null) return cleaned;

            var seen = new HashSet<string>();
            foreach (var raw in rawTags)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var tag = raw.Trim().TrimStart('#').Trim().ToLowerInvariant();
                tag = string.Join('-', tag.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

                if (tag.Length == 0 || tag.Length > 100) continue;
                if (!seen.Add(tag)) continue;

                cleaned.Add(tag);
                if (cleaned.Count >= _options.MaxTags) break;
            }

            return cleaned;
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

        private sealed class SummaryPayload
        {
            [JsonPropertyName("summary")] public string? Summary { get; set; }
            [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        }
    }
}
