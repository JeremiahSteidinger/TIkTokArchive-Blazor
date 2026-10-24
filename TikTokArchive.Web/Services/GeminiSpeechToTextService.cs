using System.Net.Http.Headers;
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
    /// Speech-to-text backed by the Google Gemini API. This app has no ffmpeg, so — like
    /// <see cref="WhisperAsrSpeechToTextService"/> — the raw video file is sent as-is: it's
    /// streamed to the Gemini Files API (inline base64 requests are size-capped), referenced by
    /// URI from one generateContent call that returns the transcript as JSON, then deleted.
    /// Gemini reports no per-segment log-probability, so the confidence is always null. The API
    /// key and model come from the DB-backed <see cref="AiProviderSettings"/> row, shared with
    /// <see cref="GeminiVideoSummaryService"/>.
    /// </summary>
    public class GeminiSpeechToTextService : ISpeechToTextService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly TimeSpan FilePollInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan FileProcessingTimeout = TimeSpan.FromMinutes(5);

        // Transcribing someone else's speech shouldn't be refused for its content; anything Gemini
        // still blocks surfaces as TranscriptionOutcome.Rejected.
        private static readonly string[] SafetyCategories =
        [
            "HARM_CATEGORY_HARASSMENT",
            "HARM_CATEGORY_HATE_SPEECH",
            "HARM_CATEGORY_SEXUALLY_EXPLICIT",
            "HARM_CATEGORY_DANGEROUS_CONTENT"
        ];

        private static readonly string[] BlockedFinishReasons =
            ["SAFETY", "RECITATION", "PROHIBITED_CONTENT", "BLOCKLIST", "SPII"];

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly TikTokArchiveDbContext _dbContext;
        private readonly SpeechToTextOptions _options;
        private readonly AiEnrichmentOptions _aiOptions;
        private readonly ILogger<GeminiSpeechToTextService> _logger;

        public GeminiSpeechToTextService(
            IHttpClientFactory httpClientFactory,
            TikTokArchiveDbContext dbContext,
            IOptions<SpeechToTextOptions> options,
            IOptions<AiEnrichmentOptions> aiOptions,
            ILogger<GeminiSpeechToTextService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _dbContext = dbContext;
            _options = options.Value;
            _aiOptions = aiOptions.Value;
            _logger = logger;
        }

        public async Task<TranscriptionResult> TranscribeAsync(string videoFilePath, CancellationToken ct = default)
        {
            if (!File.Exists(videoFilePath))
            {
                return new TranscriptionResult(TranscriptionOutcome.NotFound, null, null, "Video file not found");
            }

            var settings = await _dbContext.AiProviderSettings
                .AsNoTracking()
                .Select(s => new { s.GeminiApiKey, s.GeminiTranscriptionModel, s.GeminiModel })
                .FirstOrDefaultAsync(ct);
            var apiKey = settings?.GeminiApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                // Error (not a terminal outcome) so the worker keeps retrying under backoff — an
                // admin fixing the key later should still get this video transcribed.
                return new TranscriptionResult(TranscriptionOutcome.Error, null, null, "Gemini API key is not configured");
            }

            // The transcription-specific pick wins, then the summary model, then the appsettings default.
            var model = !string.IsNullOrWhiteSpace(settings?.GeminiTranscriptionModel) ? settings.GeminiTranscriptionModel
                : !string.IsNullOrWhiteSpace(settings?.GeminiModel) ? settings.GeminiModel
                : _aiOptions.GeminiModel;

            var client = _httpClientFactory.CreateClient(GeminiVideoSummaryService.HttpClientName);
            string? fileName = null;

            try
            {
                var mimeType = MimeTypeFor(videoFilePath);
                var file = await UploadAsync(client, apiKey, videoFilePath, mimeType, ct);
                fileName = file.Name;

                file = await WaitUntilActiveAsync(client, apiKey, file, ct);

                var request = new GeminiRequest
                {
                    Contents =
                    [
                        new GeminiContent
                        {
                            Parts =
                            [
                                new GeminiPart { FileData = new GeminiFileData { MimeType = file.MimeType ?? mimeType, FileUri = file.Uri } },
                                new GeminiPart { Text = BuildPrompt() }
                            ]
                        }
                    ],
                    GenerationConfig = new GeminiGenerationConfig(),
                    SafetySettings = SafetyCategories
                        .Select(c => new GeminiSafetySetting { Category = c, Threshold = "BLOCK_NONE" })
                        .ToList()
                };

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
                    _logger.LogWarning("Gemini transcription request failed with {StatusCode}: {Body}", response.StatusCode, body);
                    return new TranscriptionResult(TranscriptionOutcome.Error, null, null, $"Gemini returned {(int)response.StatusCode}");
                }

                return ParseResponse(body);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Real application shutdown — propagate so the worker stops cleanly.
                throw;
            }
            catch (Exception ex)
            {
                // Unreachable, HTTP timeout, upload/processing failure, etc. — transient; the worker retries.
                _logger.LogWarning(ex, "Gemini transcription failed for {Path}", videoFilePath);
                return new TranscriptionResult(TranscriptionOutcome.Error, null, null, ex.GetFullMessage());
            }
            finally
            {
                if (fileName != null)
                {
                    await DeleteFileAsync(client, apiKey, fileName);
                }
            }
        }

        private string BuildPrompt()
        {
            var language = string.IsNullOrWhiteSpace(_options.Language)
                ? "Transcribe in the language that is spoken; do not translate."
                : $"The spoken language is \"{_options.Language}\"; do not translate.";

            return "Transcribe the speech in this video verbatim. " + language + " "
                 + "Include only words that are actually spoken or sung — no descriptions of sounds, music or visuals, "
                 + "no on-screen text, no timestamps and no speaker labels. "
                 + "Respond with a JSON object of the form {\"hasSpeech\": boolean, \"transcript\": string}. "
                 + "If nothing is spoken, return {\"hasSpeech\": false, \"transcript\": \"\"}.";
        }

        // Resumable upload: a "start" request returns the session URL, then the bytes are streamed
        // (not buffered) in a single "upload, finalize" request.
        private async Task<GeminiFile> UploadAsync(
            HttpClient client, string apiKey, string videoFilePath, string mimeType, CancellationToken ct)
        {
            var length = new FileInfo(videoFilePath).Length;

            using var startRequest = new HttpRequestMessage(HttpMethod.Post, "upload/v1beta/files")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new GeminiFileEnvelope { File = new GeminiFile { DisplayName = Path.GetFileName(videoFilePath) } },
                        SerializerOptions),
                    Encoding.UTF8, "application/json")
            };
            startRequest.Headers.Add("x-goog-api-key", apiKey);
            startRequest.Headers.Add("X-Goog-Upload-Protocol", "resumable");
            startRequest.Headers.Add("X-Goog-Upload-Command", "start");
            startRequest.Headers.Add("X-Goog-Upload-Header-Content-Length", length.ToString());
            startRequest.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);

            using var startResponse = await client.SendAsync(startRequest, ct);
            if (!startResponse.IsSuccessStatusCode)
            {
                var startBody = await startResponse.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Gemini upload start failed with {StatusCode}: {Body}", startResponse.StatusCode, startBody);
                throw new InvalidOperationException($"Gemini upload start returned {(int)startResponse.StatusCode}");
            }

            if (!startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var urls)
                || urls.FirstOrDefault() is not { Length: > 0 } uploadUrl)
            {
                throw new InvalidOperationException("Gemini upload start returned no upload URL");
            }

            await using var fileStream = File.OpenRead(videoFilePath);
            using var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            fileContent.Headers.ContentLength = length;

            using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = fileContent };
            uploadRequest.Headers.Add("x-goog-api-key", apiKey);
            uploadRequest.Headers.Add("X-Goog-Upload-Offset", "0");
            uploadRequest.Headers.Add("X-Goog-Upload-Command", "upload, finalize");

            using var uploadResponse = await client.SendAsync(uploadRequest, ct);
            var body = await uploadResponse.Content.ReadAsStringAsync(ct);
            if (!uploadResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini upload failed with {StatusCode}: {Body}", uploadResponse.StatusCode, body);
                throw new InvalidOperationException($"Gemini upload returned {(int)uploadResponse.StatusCode}");
            }

            var file = JsonSerializer.Deserialize<GeminiFileEnvelope>(body, SerializerOptions)?.File;
            if (string.IsNullOrWhiteSpace(file?.Name) || string.IsNullOrWhiteSpace(file.Uri))
            {
                throw new InvalidOperationException("Gemini upload returned no file");
            }

            return file;
        }

        // Uploaded video is PROCESSING for a few seconds before it can be referenced.
        private async Task<GeminiFile> WaitUntilActiveAsync(
            HttpClient client, string apiKey, GeminiFile file, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + FileProcessingTimeout;

            while (true)
            {
                if (file.State == "ACTIVE") return file;
                if (file.State == "FAILED")
                {
                    throw new InvalidOperationException("Gemini could not process the video file");
                }
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("Timed out waiting for Gemini to process the video file");
                }

                await Task.Delay(FilePollInterval, ct);

                using var request = new HttpRequestMessage(HttpMethod.Get, $"v1beta/{file.Name}");
                request.Headers.Add("x-goog-api-key", apiKey);

                using var response = await client.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Gemini file status failed with {StatusCode}: {Body}", response.StatusCode, body);
                    throw new InvalidOperationException($"Gemini file status returned {(int)response.StatusCode}");
                }

                file = JsonSerializer.Deserialize<GeminiFile>(body, SerializerOptions)
                    ?? throw new InvalidOperationException("Gemini returned an unexpected file status");
            }
        }

        // Best-effort: Gemini expires uploaded files on its own after 48 hours, so a failed delete
        // only wastes storage quota. Not tied to the caller's token so it still runs on shutdown.
        private async Task DeleteFileAsync(HttpClient client, string apiKey, string fileName)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"v1beta/{fileName}");
                request.Headers.Add("x-goog-api-key", apiKey);

                using var response = await client.SendAsync(request, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Could not delete Gemini file {File}: {StatusCode}", fileName, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete Gemini file {File}", fileName);
            }
        }

        private TranscriptionResult ParseResponse(string body)
        {
            GeminiResponse? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<GeminiResponse>(body, SerializerOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse Gemini envelope");
                return new TranscriptionResult(TranscriptionOutcome.Error, null, null, "Malformed Gemini response");
            }

            if (!string.IsNullOrWhiteSpace(envelope?.PromptFeedback?.BlockReason))
            {
                return new TranscriptionResult(TranscriptionOutcome.Rejected, null, null,
                    $"Gemini blocked the video ({envelope.PromptFeedback.BlockReason})");
            }

            var candidate = envelope?.Candidates?.FirstOrDefault();
            if (candidate?.FinishReason != null && BlockedFinishReasons.Contains(candidate.FinishReason))
            {
                return new TranscriptionResult(TranscriptionOutcome.Rejected, null, null,
                    $"Gemini blocked the transcript ({candidate.FinishReason})");
            }

            var inner = candidate?.Content?.Parts?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Text))?.Text;
            if (string.IsNullOrWhiteSpace(inner))
            {
                return new TranscriptionResult(TranscriptionOutcome.Error, null, null, "Empty Gemini response");
            }

            TranscriptPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<TranscriptPayload>(inner, SerializerOptions);
            }
            catch (JsonException ex)
            {
                // Includes a transcript cut off by MAX_TOKENS mid-string.
                _logger.LogWarning(ex, "Could not parse Gemini transcript JSON (finish reason {FinishReason})", candidate?.FinishReason);
                return new TranscriptionResult(TranscriptionOutcome.Error, null, null, "Malformed Gemini response");
            }

            var text = payload?.Transcript?.Trim();
            if (payload?.HasSpeech == false || string.IsNullOrWhiteSpace(text))
            {
                return new TranscriptionResult(TranscriptionOutcome.NoAudio, null, null, null);
            }

            return new TranscriptionResult(TranscriptionOutcome.Success, text, null, null);
        }

        private static string MimeTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".flv" => "video/x-flv",
            _ => "video/mp4" // .mp4, .m4v
        };

        private sealed class TranscriptPayload
        {
            [JsonPropertyName("hasSpeech")] public bool? HasSpeech { get; set; }
            [JsonPropertyName("transcript")] public string? Transcript { get; set; }
        }

        private sealed class GeminiFileEnvelope
        {
            [JsonPropertyName("file")] public GeminiFile? File { get; set; }
        }

        private sealed class GeminiFile
        {
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
            [JsonPropertyName("uri")] public string? Uri { get; set; }
            [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
            [JsonPropertyName("state")] public string? State { get; set; }
        }

        private sealed class GeminiRequest
        {
            [JsonPropertyName("contents")] public List<GeminiContent> Contents { get; set; } = [];
            [JsonPropertyName("generationConfig")] public GeminiGenerationConfig? GenerationConfig { get; set; }
            [JsonPropertyName("safetySettings")] public List<GeminiSafetySetting>? SafetySettings { get; set; }
        }

        private sealed class GeminiGenerationConfig
        {
            [JsonPropertyName("responseMimeType")] public string ResponseMimeType { get; set; } = "application/json";

            // Only the audio matters; low resolution cuts the per-frame token cost of the video track.
            [JsonPropertyName("mediaResolution")] public string MediaResolution { get; set; } = "MEDIA_RESOLUTION_LOW";
        }

        private sealed class GeminiSafetySetting
        {
            [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
            [JsonPropertyName("threshold")] public string Threshold { get; set; } = string.Empty;
        }

        private sealed class GeminiContent
        {
            [JsonPropertyName("parts")] public List<GeminiPart> Parts { get; set; } = [];
        }

        private sealed class GeminiPart
        {
            [JsonPropertyName("text")] public string? Text { get; set; }
            [JsonPropertyName("fileData")] public GeminiFileData? FileData { get; set; }
        }

        private sealed class GeminiFileData
        {
            [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
            [JsonPropertyName("fileUri")] public string? FileUri { get; set; }
        }

        private sealed class GeminiResponse
        {
            [JsonPropertyName("candidates")] public List<GeminiCandidate>? Candidates { get; set; }
            [JsonPropertyName("promptFeedback")] public GeminiPromptFeedback? PromptFeedback { get; set; }
        }

        private sealed class GeminiCandidate
        {
            [JsonPropertyName("content")] public GeminiContent? Content { get; set; }
            [JsonPropertyName("finishReason")] public string? FinishReason { get; set; }
        }

        private sealed class GeminiPromptFeedback
        {
            [JsonPropertyName("blockReason")] public string? BlockReason { get; set; }
        }
    }
}
