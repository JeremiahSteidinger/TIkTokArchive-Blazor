using System.Text.Json;
using System.Text.Json.Serialization;

namespace TikTokArchive.Web.Services
{
    public record GeminiModelInfo(string Id, string DisplayName);

    /// <summary>
    /// Lists the Gemini models available to a given API key via the ListModels endpoint, so the
    /// admin UI can offer a picker instead of a hardcoded model name — which Google can (and did)
    /// deprecate out from under <see cref="GeminiVideoSummaryService"/>. Shares the "gemini" named
    /// HttpClient with that service but is registered unconditionally, so a key can be validated
    /// and a model chosen even before the AiEnrichment feature flag is switched on.
    /// </summary>
    public class GeminiModelCatalogService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<GeminiModelCatalogService> _logger;

        public GeminiModelCatalogService(IHttpClientFactory httpClientFactory, ILogger<GeminiModelCatalogService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>Throws with a user-facing message on failure (bad key, network error, etc.).</summary>
        public async Task<IReadOnlyList<GeminiModelInfo>> ListModelsAsync(string apiKey, CancellationToken ct = default)
        {
            var client = _httpClientFactory.CreateClient(GeminiVideoSummaryService.HttpClientName);
            var models = new List<GeminiModelInfo>();
            string? pageToken = null;

            do
            {
                var url = "v1beta/models?pageSize=200"
                          + (pageToken != null ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : "");

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-goog-api-key", apiKey);

                using var response = await client.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Gemini ListModels failed with {StatusCode}: {Body}", response.StatusCode, body);
                    throw new InvalidOperationException(ExtractErrorMessage(body) ?? $"Gemini returned {(int)response.StatusCode}");
                }

                ListModelsResponse? page;
                try
                {
                    page = JsonSerializer.Deserialize<ListModelsResponse>(body, SerializerOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Could not parse Gemini ListModels response");
                    throw new InvalidOperationException("Gemini returned an unexpected response");
                }

                foreach (var model in page?.Models ?? [])
                {
                    // Keep only models this app can actually call from GeminiVideoSummaryService
                    // (some catalog entries are embedding-only, vision-only, etc.).
                    if (model.SupportedGenerationMethods == null
                        || !model.SupportedGenerationMethods.Contains("generateContent"))
                    {
                        continue;
                    }

                    var id = model.Name?.StartsWith("models/", StringComparison.Ordinal) == true
                        ? model.Name["models/".Length..]
                        : model.Name;
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    models.Add(new GeminiModelInfo(id, string.IsNullOrWhiteSpace(model.DisplayName) ? id : model.DisplayName!));
                }

                pageToken = page?.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            return models.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string? ExtractErrorMessage(string body)
        {
            try
            {
                var error = JsonSerializer.Deserialize<GeminiErrorEnvelope>(body, SerializerOptions);
                return error?.Error?.Message;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private sealed class ListModelsResponse
        {
            [JsonPropertyName("models")] public List<GeminiModelEntry>? Models { get; set; }
            [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
        }

        private sealed class GeminiModelEntry
        {
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
            [JsonPropertyName("supportedGenerationMethods")] public List<string>? SupportedGenerationMethods { get; set; }
        }

        private sealed class GeminiErrorEnvelope
        {
            [JsonPropertyName("error")] public GeminiError? Error { get; set; }
        }

        private sealed class GeminiError
        {
            [JsonPropertyName("message")] public string? Message { get; set; }
        }
    }
}
