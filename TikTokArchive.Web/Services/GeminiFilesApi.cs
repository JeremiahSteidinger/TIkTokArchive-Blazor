using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TikTokArchive.Web.Services
{
    public record GeminiUploadedFile(string Name, string Uri, string MimeType);

    /// <summary>
    /// Minimal client for the Gemini Files API, shared by the Gemini speech-to-text engines:
    /// streamed resumable upload, wait for processing, delete. Uses the "gemini" named HttpClient
    /// (base address generativelanguage.googleapis.com).
    /// </summary>
    public static class GeminiFilesApi
    {
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);

        /// <summary>Uploads the file and waits until it is ACTIVE. Throws on any failure.</summary>
        public static async Task<GeminiUploadedFile> UploadAsync(
            HttpClient client, string apiKey, string filePath, string mimeType, ILogger logger, CancellationToken ct)
        {
            var file = await StartAndUploadAsync(client, apiKey, filePath, mimeType, logger, ct);
            try
            {
                file = await WaitUntilActiveAsync(client, apiKey, file, logger, ct);
                return new GeminiUploadedFile(file.Name!, file.Uri!, file.MimeType ?? mimeType);
            }
            catch
            {
                await DeleteAsync(client, apiKey, file.Name!, logger);
                throw;
            }
        }

        // Best-effort: Gemini expires uploaded files on its own after 48 hours, so a failed delete
        // only wastes storage quota. Not tied to the caller's token so it still runs on shutdown.
        public static async Task DeleteAsync(HttpClient client, string apiKey, string fileName, ILogger logger)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"v1beta/{fileName}");
                request.Headers.Add("x-goog-api-key", apiKey);

                using var response = await client.SendAsync(request, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Could not delete Gemini file {File}: {StatusCode}", fileName, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete Gemini file {File}", fileName);
            }
        }

        // Resumable upload: a "start" request returns the session URL, then the bytes are streamed
        // (not buffered) in a single "upload, finalize" request.
        private static async Task<GeminiFile> StartAndUploadAsync(
            HttpClient client, string apiKey, string filePath, string mimeType, ILogger logger, CancellationToken ct)
        {
            var length = new FileInfo(filePath).Length;

            using var startRequest = new HttpRequestMessage(HttpMethod.Post, "upload/v1beta/files")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new GeminiFileEnvelope { File = new GeminiFile { DisplayName = Path.GetFileName(filePath) } },
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
                logger.LogWarning("Gemini upload start failed with {StatusCode}: {Body}", startResponse.StatusCode, startBody);
                throw new InvalidOperationException($"Gemini upload start returned {(int)startResponse.StatusCode}");
            }

            if (!startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var urls)
                || urls.FirstOrDefault() is not { Length: > 0 } uploadUrl)
            {
                throw new InvalidOperationException("Gemini upload start returned no upload URL");
            }

            await using var fileStream = File.OpenRead(filePath);
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
                logger.LogWarning("Gemini upload failed with {StatusCode}: {Body}", uploadResponse.StatusCode, body);
                throw new InvalidOperationException($"Gemini upload returned {(int)uploadResponse.StatusCode}");
            }

            var file = JsonSerializer.Deserialize<GeminiFileEnvelope>(body, SerializerOptions)?.File;
            if (string.IsNullOrWhiteSpace(file?.Name) || string.IsNullOrWhiteSpace(file.Uri))
            {
                throw new InvalidOperationException("Gemini upload returned no file");
            }

            return file;
        }

        // Uploaded media can be PROCESSING for a few seconds before it can be referenced.
        private static async Task<GeminiFile> WaitUntilActiveAsync(
            HttpClient client, string apiKey, GeminiFile file, ILogger logger, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + ProcessingTimeout;

            while (true)
            {
                if (file.State == "ACTIVE") return file;
                if (file.State == "FAILED")
                {
                    throw new InvalidOperationException("Gemini could not process the uploaded file");
                }
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("Timed out waiting for Gemini to process the uploaded file");
                }

                await Task.Delay(PollInterval, ct);

                using var request = new HttpRequestMessage(HttpMethod.Get, $"v1beta/{file.Name}");
                request.Headers.Add("x-goog-api-key", apiKey);

                using var response = await client.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Gemini file status failed with {StatusCode}: {Body}", response.StatusCode, body);
                    throw new InvalidOperationException($"Gemini file status returned {(int)response.StatusCode}");
                }

                file = JsonSerializer.Deserialize<GeminiFile>(body, SerializerOptions)
                    ?? throw new InvalidOperationException("Gemini returned an unexpected file status");
            }
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
    }
}
