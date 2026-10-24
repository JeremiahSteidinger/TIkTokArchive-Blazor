using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services
{
    /// <summary>One comment as returned by TikTok, before it's mapped to an entity.</summary>
    public record TikTokComment(
        string Id, string AuthorUsername, string AuthorDisplayName, string Text,
        int LikeCount, int ReplyCount, DateTime CreatedAt);

    public interface ITikTokCommentClient
    {
        /// <summary>Top-level comments in TikTok's own ranking, up to <paramref name="limit"/>.</summary>
        Task<List<TikTokComment>> GetCommentsAsync(string videoId, int limit, CancellationToken cancellationToken = default);

        /// <summary>Replies to one comment in the order TikTok returns them, up to <paramref name="limit"/>.</summary>
        Task<List<TikTokComment>> GetRepliesAsync(string videoId, string commentId, int limit, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Reads comments from TikTok's web comment API. yt-dlp has no TikTok comment extractor,
    /// so unlike the rest of the ingest pipeline this calls TikTok directly. The synced
    /// browser cookies (<see cref="YtDlpOptions.CookiesFile"/>) are sent along when present —
    /// TikTok often answers anonymous requests with an empty body.
    /// </summary>
    public class TikTokCommentClient : ITikTokCommentClient
    {
        public const string HttpClientName = "TikTokComments";

        // TikTok's page-size ceiling; it may return fewer per page than asked.
        private const int PageSize = 50;

        // Space out requests so a video with many threads doesn't hammer the API.
        private static readonly TimeSpan RequestDelay = TimeSpan.FromMilliseconds(300);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string? _cookiesFile;
        private readonly ILogger<TikTokCommentClient> _logger;

        public TikTokCommentClient(
            IHttpClientFactory httpClientFactory, IOptions<YtDlpOptions> options, ILogger<TikTokCommentClient> logger)
        {
            _httpClientFactory = httpClientFactory;
            _cookiesFile = options.Value.CookiesFile;
            _logger = logger;
        }

        public Task<List<TikTokComment>> GetCommentsAsync(string videoId, int limit, CancellationToken cancellationToken = default) =>
            FetchPagedAsync(
                cursor => $"api/comment/list/?aid=1988&aweme_id={Uri.EscapeDataString(videoId)}&count={PageSize}&cursor={cursor}",
                limit, cancellationToken);

        public Task<List<TikTokComment>> GetRepliesAsync(string videoId, string commentId, int limit, CancellationToken cancellationToken = default) =>
            FetchPagedAsync(
                cursor => $"api/comment/list/reply/?aid=1988&item_id={Uri.EscapeDataString(videoId)}" +
                          $"&comment_id={Uri.EscapeDataString(commentId)}&count={PageSize}&cursor={cursor}",
                limit, cancellationToken);

        private async Task<List<TikTokComment>> FetchPagedAsync(
            Func<long, string> buildPath, int limit, CancellationToken cancellationToken)
        {
            var results = new List<TikTokComment>();
            if (limit <= 0) return results;

            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            var cookieHeader = await ReadCookieHeaderAsync(cancellationToken);
            long cursor = 0;

            while (results.Count < limit)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, buildPath(cursor));
                if (cookieHeader != null)
                {
                    request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                }

                using var response = await httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(body))
                {
                    throw new InvalidOperationException(
                        "TikTok returned an empty response — sync cookies from the browser extension and retry.");
                }

                var page = JsonConvert.DeserializeObject<CommentListResponse>(body)
                    ?? throw new InvalidOperationException("TikTok returned an unreadable comment response");
                if (page.StatusCode != 0)
                {
                    throw new InvalidOperationException(
                        $"TikTok comment API error {page.StatusCode}: {page.StatusMessage ?? "unknown error"}");
                }

                foreach (var comment in page.Comments ?? [])
                {
                    if (results.Count >= limit) break;
                    if (string.IsNullOrEmpty(comment.Id)) continue;
                    results.Add(new TikTokComment(
                        comment.Id,
                        comment.User?.UniqueId ?? string.Empty,
                        comment.User?.Nickname ?? string.Empty,
                        comment.Text ?? string.Empty,
                        comment.LikeCount,
                        comment.ReplyCount,
                        DateTimeOffset.FromUnixTimeSeconds(comment.CreateTime).UtcDateTime));
                }

                // Stop on an empty page too: a cursor that doesn't advance would otherwise loop.
                if (page.HasMore == 0 || page.Comments is not { Count: > 0 } || page.Cursor <= cursor)
                {
                    break;
                }

                cursor = page.Cursor;
                await Task.Delay(RequestDelay, cancellationToken);
            }

            return results;
        }

        /// <summary>
        /// Builds a Cookie header from the tiktok.com entries in the Netscape cookies file, or
        /// null when there's no file or no TikTok cookies in it.
        /// </summary>
        private async Task<string?> ReadCookieHeaderAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_cookiesFile) || !File.Exists(_cookiesFile)) return null;

            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var pairs = new List<string>();
                foreach (var rawLine in await File.ReadAllLinesAsync(_cookiesFile, cancellationToken))
                {
                    // HttpOnly cookies are written with a "#HttpOnly_" prefix; other # lines are comments.
                    var line = rawLine.StartsWith("#HttpOnly_", StringComparison.Ordinal)
                        ? rawLine["#HttpOnly_".Length..]
                        : rawLine;
                    if (line.Length == 0 || line.StartsWith('#')) continue;

                    // domain, include-subdomains, path, secure, expiry, name, value
                    var fields = line.Split('\t');
                    if (fields.Length < 7) continue;

                    var domain = fields[0].TrimStart('.');
                    if (!domain.Equals("tiktok.com", StringComparison.OrdinalIgnoreCase) &&
                        !domain.EndsWith(".tiktok.com", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (long.TryParse(fields[4], out var expiry) && expiry > 0 && expiry < now) continue;

                    pairs.Add($"{fields[5]}={fields[6]}");
                }

                return pairs.Count > 0 ? string.Join("; ", pairs) : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to read cookies file {CookiesFile}; fetching comments without cookies", _cookiesFile);
                return null;
            }
        }

        /// <summary>Configures the named HttpClient to look like the TikTok web app.</summary>
        public static void ConfigureHttpClient(HttpClient client)
        {
            client.BaseAddress = new Uri("https://www.tiktok.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Referrer = new Uri("https://www.tiktok.com/");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private class CommentListResponse
        {
            [JsonProperty("status_code")] public int StatusCode { get; set; }
            [JsonProperty("status_msg")] public string? StatusMessage { get; set; }
            [JsonProperty("comments")] public List<CommentDto>? Comments { get; set; }
            [JsonProperty("cursor")] public long Cursor { get; set; }
            [JsonProperty("has_more")] public int HasMore { get; set; }
        }

        private class CommentDto
        {
            [JsonProperty("cid")] public string? Id { get; set; }
            [JsonProperty("text")] public string? Text { get; set; }
            [JsonProperty("create_time")] public long CreateTime { get; set; }
            [JsonProperty("digg_count")] public int LikeCount { get; set; }
            [JsonProperty("reply_comment_total")] public int ReplyCount { get; set; }
            [JsonProperty("user")] public CommentUserDto? User { get; set; }
        }

        private class CommentUserDto
        {
            [JsonProperty("unique_id")] public string? UniqueId { get; set; }
            [JsonProperty("nickname")] public string? Nickname { get; set; }
        }
    }
}
