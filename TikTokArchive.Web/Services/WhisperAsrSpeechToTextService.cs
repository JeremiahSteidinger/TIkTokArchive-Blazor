using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Speech-to-text client for onerahmet/openai-whisper-asr-webservice. The ASR container
    /// bundles ffmpeg, so we POST the raw video file and it extracts/decodes the audio itself —
    /// no ffmpeg needed in this app. The file is streamed (not buffered) so large videos don't
    /// load into memory. We request JSON output so we can derive a confidence score from the
    /// per-segment average log-probability the engine reports.
    /// </summary>
    public class WhisperAsrSpeechToTextService : ISpeechToTextService
    {
        public const string HttpClientName = "stt";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SpeechToTextOptions _options;
        private readonly ILogger<WhisperAsrSpeechToTextService> _logger;

        public WhisperAsrSpeechToTextService(
            IHttpClientFactory httpClientFactory,
            IOptions<SpeechToTextOptions> options,
            ILogger<WhisperAsrSpeechToTextService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<TranscriptionResult> TranscribeAsync(string videoFilePath, CancellationToken ct = default)
        {
            if (!File.Exists(videoFilePath))
            {
                return new TranscriptionResult(TranscriptionOutcome.NotFound, null, null, "Video file not found");
            }

            // task=transcribe (not translate), output=json so we get per-segment metadata
            // (avg_logprob) for the confidence score, encode=true lets the service re-encode
            // the audio with ffmpeg before inference.
            var url = "asr?task=transcribe&output=json&encode=true";
            if (!string.IsNullOrWhiteSpace(_options.Language))
            {
                url += $"&language={Uri.EscapeDataString(_options.Language)}";
            }

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);

                using var content = new MultipartFormDataContent();
                await using var fileStream = File.OpenRead(videoFilePath);
                using var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Add(fileContent, "audio_file", Path.GetFileName(videoFilePath));

                using var response = await client.PostAsync(url, content, ct);
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
                _logger.LogWarning(ex, "Transcription request failed for {Path}", videoFilePath);
                return new TranscriptionResult(TranscriptionOutcome.Error, null, null, ex.GetFullMessage());
            }
        }

        private TranscriptionResult ParseResponse(string body)
        {
            AsrResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<AsrResponse>(body);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse ASR JSON response");
                return new TranscriptionResult(TranscriptionOutcome.Error, null, null, "Malformed ASR response");
            }

            var text = parsed?.Text?.Trim();

            // The engine returns empty/whitespace text for silent or audio-less input.
            if (string.IsNullOrWhiteSpace(text))
            {
                return new TranscriptionResult(TranscriptionOutcome.NoAudio, null, null, null);
            }

            return new TranscriptionResult(
                TranscriptionOutcome.Success, text, ComputeConfidence(parsed!.Segments), null);
        }

        // Whisper reports avg_logprob per segment (the mean token log-probability — closer to 0 is
        // more confident). We duration-weight the segments and convert the mean back to a 0–1
        // probability with exp(). Laughter/music/noise tend to decode with very negative logprobs,
        // so they land near zero.
        private static double? ComputeConfidence(List<AsrSegment>? segments)
        {
            if (segments == null || segments.Count == 0) return null;

            var totalWeight = segments.Sum(s => Math.Max(s.End - s.Start, 0.0));
            double meanLogProb = totalWeight > 0
                ? segments.Sum(s => s.AvgLogprob * Math.Max(s.End - s.Start, 0.0)) / totalWeight
                : segments.Average(s => s.AvgLogprob);

            return Math.Exp(meanLogProb);
        }

        private sealed class AsrResponse
        {
            [JsonPropertyName("text")] public string? Text { get; set; }
            [JsonPropertyName("segments")] public List<AsrSegment>? Segments { get; set; }
        }

        private sealed class AsrSegment
        {
            [JsonPropertyName("start")] public double Start { get; set; }
            [JsonPropertyName("end")] public double End { get; set; }
            [JsonPropertyName("avg_logprob")] public double AvgLogprob { get; set; }
            [JsonPropertyName("no_speech_prob")] public double NoSpeechProb { get; set; }
        }
    }
}
