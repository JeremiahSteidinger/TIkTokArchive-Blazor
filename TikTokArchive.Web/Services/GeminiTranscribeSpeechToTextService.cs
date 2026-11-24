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
    /// Speech-to-text backed by Google's dedicated transcription model (gemini-3.5-transcribe).
    /// That model accepts audio only and is called through the Interactions API rather than
    /// generateContent, so the audio track is first extracted with ffmpeg
    /// (<see cref="AudioExtractor"/>), uploaded through the Files API, transcribed, and the
    /// upload and temp file deleted. Billed per second of audio, it's far cheaper than sending
    /// the whole video to a general model (<see cref="GeminiSpeechToTextService"/>). It reports
    /// no confidence score. The API key comes from the DB-backed <see cref="AiProviderSettings"/> row.
    /// </summary>
    public class GeminiTranscribeSpeechToTextService : ISpeechToTextService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly TikTokArchiveDbContext _dbContext;
        private readonly AudioExtractor _audioExtractor;
        private readonly SpeechToTextOptions _options;
        private readonly ILogger<GeminiTranscribeSpeechToTextService> _logger;

        public GeminiTranscribeSpeechToTextService(
            IHttpClientFactory httpClientFactory,
            TikTokArchiveDbContext dbContext,
            AudioExtractor audioExtractor,
            IOptions<SpeechToTextOptions> options,
            ILogger<GeminiTranscribeSpeechToTextService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _dbContext = dbContext;
            _audioExtractor = audioExtractor;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<TranscriptionResult> TranscribeAsync(string videoFilePath, CancellationToken ct = default)
        {
            if (!File.Exists(videoFilePath))
            {
                return new TranscriptionResult(TranscriptionOutcome.NotFound, null, null, "Video file not found");
            }

            var apiKey = await _dbContext.AiProviderSettings
                .AsNoTracking()
                .Select(s => s.GeminiApiKey)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                // Error (not a terminal outcome) so the worker keeps retrying under backoff — an
                // admin fixing the key later should still get this video transcribed.
                return new TranscriptionResult(TranscriptionOutcome.Error, null, null, "Gemini API key is not configured");
            }

            var client = _httpClientFactory.CreateClient(GeminiVideoSummaryService.HttpClientName);
            string? audioPath = null;
            GeminiUploadedFile? file = null;

            try
            {
                audioPath = await _audioExtractor.ExtractAsync(videoFilePath, ct);
                if (audioPath == null)
                {
                    // No audio stream at all — nothing to transcribe, and nothing to pay for.
                    return new TranscriptionResult(TranscriptionOutcome.NoAudio, null, null, null);
                }

                file = await GeminiFilesApi.UploadAsync(client, apiKey, audioPath, AudioExtractor.MimeType, _logger, ct);

                var request = new InteractionRequest
                {
                    Model = _options.GeminiTranscribeModel,
                    Input = [new AudioInput { Uri = file.Uri, MimeType = AudioExtractor.MimeType }],
                    GenerationConfig = new InteractionGenerationConfig
                    {
                        TranscriptionConfig = new TranscriptionConfig
                        {
                            // Empty list = automatic language detection (and code-switching).
                            LanguageCodes = string.IsNullOrWhiteSpace(_options.Language) ? [] : [_options.Language],
                            Mode = _options.GeminiTranscribeMode
                        }
                    },
                    Store = false
                };

                using var content = new StringContent(
                    JsonSerializer.Serialize(request, SerializerOptions), Encoding.UTF8, "application/json");
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1beta/interactions") { Content = content };
                httpRequest.Headers.Add("x-goog-api-key", apiKey);

                using var response = await client.SendAsync(httpRequest, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Gemini Transcribe request failed with {StatusCode}: {Body}", response.StatusCode, body);
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
                // ffmpeg failure, unreachable, HTTP timeout, upload failure, etc. — the worker retries.
                _logger.LogWarning(ex, "Gemini Transcribe failed for {Path}", videoFilePath);
                return new TranscriptionResult(TranscriptionOutcome.Error, null, null, ex.GetFullMessage());
            }
            finally
            {
                if (file != null)
                {
                    await GeminiFilesApi.DeleteAsync(client, apiKey, file.Name, _logger);
                }
                if (audioPath != null)
                {
                    _audioExtractor.TryDelete(audioPath);
                }
            }
        }

        private TranscriptionResult ParseResponse(string body)
        {
            InteractionResponse? interaction;
            try
            {
                interaction = JsonSerializer.Deserialize<InteractionResponse>(body, SerializerOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse Gemini Transcribe response");
                return new TranscriptionResult(TranscriptionOutcome.Error, null, null, "Malformed Gemini response");
            }

            var status = interaction?.Status;
            if (status != "completed" && status != "incomplete")
            {
                _logger.LogWarning("Gemini Transcribe interaction ended as {Status}: {Body}", status, body);
                return new TranscriptionResult(TranscriptionOutcome.Error, null, null, $"Gemini transcription {status ?? "returned no status"}");
            }

            // The transcript is the text content of the model_output step(s) — what the SDKs expose as output_text.
            var text = string.Concat(
                    (interaction!.Steps ?? [])
                    .Where(s => s.Type == "model_output")
                    .SelectMany(s => s.Content ?? [])
                    .Where(c => c.Type == "text")
                    .Select(c => c.Text))
                .Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                // Audio stream present but nothing spoken (music, silence).
                return new TranscriptionResult(TranscriptionOutcome.NoAudio, null, null, null);
            }

            if (status == "incomplete")
            {
                _logger.LogWarning("Gemini Transcribe returned an incomplete transcript ({Length} chars); keeping it", text.Length);
            }

            return new TranscriptionResult(TranscriptionOutcome.Success, text, null, null);
        }

        private sealed class InteractionRequest
        {
            [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
            [JsonPropertyName("input")] public List<AudioInput> Input { get; set; } = [];
            [JsonPropertyName("generation_config")] public InteractionGenerationConfig? GenerationConfig { get; set; }
            [JsonPropertyName("store")] public bool? Store { get; set; }
        }

        private sealed class AudioInput
        {
            [JsonPropertyName("type")] public string Type { get; set; } = "audio";
            [JsonPropertyName("uri")] public string? Uri { get; set; }
            [JsonPropertyName("mime_type")] public string? MimeType { get; set; }
        }

        private sealed class InteractionGenerationConfig
        {
            [JsonPropertyName("transcription_config")] public TranscriptionConfig? TranscriptionConfig { get; set; }
        }

        private sealed class TranscriptionConfig
        {
            [JsonPropertyName("language_codes")] public List<string> LanguageCodes { get; set; } = [];
            [JsonPropertyName("mode")] public string? Mode { get; set; }
        }

        private sealed class InteractionResponse
        {
            [JsonPropertyName("status")] public string? Status { get; set; }
            [JsonPropertyName("steps")] public List<InteractionStep>? Steps { get; set; }
        }

        private sealed class InteractionStep
        {
            [JsonPropertyName("type")] public string? Type { get; set; }
            [JsonPropertyName("content")] public List<InteractionContent>? Content { get; set; }
        }

        private sealed class InteractionContent
        {
            [JsonPropertyName("type")] public string? Type { get; set; }
            [JsonPropertyName("text")] public string? Text { get; set; }
        }
    }
}
