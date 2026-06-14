using OpenSearch.Client;
using OpenSearch.Net;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    public class VideoDocument
    {
        public string VideoId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string CreatorName { get; set; } = string.Empty;
        public string CreatorUsername { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public string Transcript { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime AddedToApp { get; set; }

        public static VideoDocument FromVideo(Video video) => new()
        {
            VideoId = video.TikTokVideoId,
            Description = video.Description ?? string.Empty,
            CreatorName = video.Creator?.DisplayName ?? string.Empty,
            CreatorUsername = video.Creator?.TikTokId ?? string.Empty,
            Tags = video.Tags?.Select(vt => vt.Tag.Name).ToList() ?? new List<string>(),
            Transcript = video.Transcript ?? string.Empty,
            CreatedAt = video.CreatedAt,
            AddedToApp = video.AddedToApp
        };
    }

    public class OpenSearchService : ISearchService
    {
        // v2: tags and usernames are ngram-analyzed text (previously keyword + wildcard
        // queries). v3: adds the speech-to-text Transcript field to the mapping. A new name
        // lets the new mapping apply on a fresh index without migrating the old one; the sync
        // service repopulates it from the database.
        public const string IndexName = "tiktok_videos_v3";

        private readonly IOpenSearchClient _client;
        private readonly ILogger<OpenSearchService> _logger;
        private readonly SemaphoreSlim _indexInitLock = new(1, 1);
        private volatile bool _indexEnsured;

        public OpenSearchService(IOpenSearchClient client, ILogger<OpenSearchService> logger)
        {
            _client = client;
            _logger = logger;
        }

        // The index must never be auto-created by a stray write: dynamic mapping would lack
        // the ngram analyzer and silently break substring search. Every operation funnels
        // through here so the first one to reach OpenSearch creates it correctly, and a
        // failure surfaces to the caller (the outbox worker retries) instead of being
        // swallowed at startup.
        private async Task EnsureIndexAsync(CancellationToken cancellationToken)
        {
            if (_indexEnsured) return;

            await _indexInitLock.WaitAsync(cancellationToken);
            try
            {
                if (_indexEnsured) return;

                var existsResponse = await _client.Indices.ExistsAsync(IndexName, ct: cancellationToken);
                if (existsResponse.Exists)
                {
                    _indexEnsured = true;
                    return;
                }

                if (existsResponse.OriginalException != null)
                {
                    throw new InvalidOperationException(
                        $"Could not reach OpenSearch to check index {IndexName}", existsResponse.OriginalException);
                }

                var createResponse = await _client.Indices.CreateAsync(IndexName, c => c
                    .Settings(s => s
                        .Analysis(a => a
                            .Analyzers(an => an
                                .Custom("ngram_analyzer", ca => ca
                                    .Tokenizer("standard")
                                    .Filters("lowercase", "ngram_filter")
                                )
                            )
                            .TokenFilters(tf => tf
                                .NGram("ngram_filter", ng => ng
                                    .MinGram(3)
                                    .MaxGram(4)
                                )
                            )
                        )
                    )
                    .Map<VideoDocument>(m => m
                        .Properties(p => p
                            .Keyword(k => k.Name(n => n.VideoId))
                            .Text(t => t
                                .Name(n => n.Description)
                                .Analyzer("ngram_analyzer")
                                .SearchAnalyzer("standard")
                            )
                            .Text(t => t
                                .Name(n => n.CreatorName)
                                .Analyzer("ngram_analyzer")
                                .SearchAnalyzer("standard")
                            )
                            .Text(t => t
                                .Name(n => n.CreatorUsername)
                                .Analyzer("ngram_analyzer")
                                .SearchAnalyzer("standard")
                                .Fields(f => f.Keyword(k => k.Name("raw")))
                            )
                            .Text(t => t
                                .Name(n => n.Tags)
                                .Analyzer("ngram_analyzer")
                                .SearchAnalyzer("standard")
                                .Fields(f => f.Keyword(k => k.Name("raw")))
                            )
                            .Text(t => t
                                .Name(n => n.Transcript)
                                .Analyzer("ngram_analyzer")
                                .SearchAnalyzer("standard")
                            )
                            .Date(d => d.Name(n => n.CreatedAt))
                            .Date(d => d.Name(n => n.AddedToApp))
                        )
                    ), cancellationToken);

                if (!createResponse.IsValid &&
                    createResponse.ServerError?.Error?.Type != "resource_already_exists_exception")
                {
                    throw new InvalidOperationException(
                        $"Failed to create OpenSearch index {IndexName}: {createResponse.DebugInformation}");
                }

                _logger.LogInformation("OpenSearch index {IndexName} is ready", IndexName);
                _indexEnsured = true;
            }
            finally
            {
                _indexInitLock.Release();
            }
        }

        public async Task IndexVideoAsync(Video video, CancellationToken cancellationToken = default)
        {
            await EnsureIndexAsync(cancellationToken);

            var document = VideoDocument.FromVideo(video);
            var response = await _client.IndexAsync(document, i => i
                .Id(video.TikTokVideoId)
                .Refresh(Refresh.False), cancellationToken);

            if (!response.IsValid)
            {
                throw new InvalidOperationException(
                    $"Failed to index video {video.TikTokVideoId}: {response.DebugInformation}");
            }

            _logger.LogDebug("Indexed video {VideoId}", video.TikTokVideoId);
        }

        public async Task IndexVideosAsync(IReadOnlyCollection<Video> videos, CancellationToken cancellationToken = default)
        {
            if (videos.Count == 0) return;

            await EnsureIndexAsync(cancellationToken);

            var bulkDescriptor = new BulkDescriptor();
            foreach (var video in videos)
            {
                var document = VideoDocument.FromVideo(video);
                bulkDescriptor.Index<VideoDocument>(i => i
                    .Document(document)
                    .Id(document.VideoId));
            }

            var bulkResponse = await _client.BulkAsync(bulkDescriptor, cancellationToken);

            if (!bulkResponse.IsValid)
            {
                throw new InvalidOperationException($"Bulk index failed: {bulkResponse.DebugInformation}");
            }

            if (bulkResponse.Errors)
            {
                var failedIds = bulkResponse.ItemsWithErrors.Select(i => i.Id).ToList();
                throw new InvalidOperationException(
                    $"Bulk index reported errors for {failedIds.Count} videos: {string.Join(", ", failedIds.Take(5))}");
            }
        }

        public async Task DeleteVideoAsync(string videoId, CancellationToken cancellationToken = default)
        {
            await EnsureIndexAsync(cancellationToken);

            var response = await _client.DeleteAsync<VideoDocument>(videoId, d => d
                .Refresh(Refresh.False), cancellationToken);

            if (!response.IsValid && response.Result != Result.NotFound)
            {
                throw new InvalidOperationException(
                    $"Failed to delete video {videoId} from index: {response.DebugInformation}");
            }

            _logger.LogDebug("Deleted video {VideoId} from index", videoId);
        }

        public async Task<SearchResult> SearchAsync(string query, int page, int pageSize, List<string>? fields = null, CancellationToken cancellationToken = default)
        {
            try
            {
                await EnsureIndexAsync(cancellationToken);

                var shouldQueries = new List<Func<QueryContainerDescriptor<VideoDocument>, QueryContainer>>();

                if (fields == null || fields.Count == 0 || fields.Contains("all"))
                {
                    fields = new List<string> { "description", "creator", "tags", "transcript" };
                }

                if (fields.Contains("description"))
                {
                    shouldQueries.Add(q => q.Match(m => m
                        .Field(f => f.Description)
                        .Query(query)
                        .Boost(2.0)
                    ));
                }

                if (fields.Contains("creator"))
                {
                    shouldQueries.Add(q => q.Match(m => m
                        .Field(f => f.CreatorName)
                        .Query(query)
                        .Boost(1.5)
                    ));
                    shouldQueries.Add(q => q.Match(m => m
                        .Field(f => f.CreatorUsername)
                        .Query(query)
                        .Boost(1.5)
                    ));
                }

                if (fields.Contains("tags"))
                {
                    shouldQueries.Add(q => q.Match(m => m
                        .Field(f => f.Tags)
                        .Query(query)
                        .Boost(1.0)
                    ));
                }

                if (fields.Contains("transcript"))
                {
                    // Spoken content is noisier than a hand-written caption, so it ranks
                    // below tags.
                    shouldQueries.Add(q => q.Match(m => m
                        .Field(f => f.Transcript)
                        .Query(query)
                        .Boost(0.75)
                    ));
                }

                var searchResponse = await _client.SearchAsync<VideoDocument>(s => s
                    .Query(q => q
                        .Bool(b => b
                            .Should(shouldQueries.ToArray())
                            .MinimumShouldMatch(1)
                        )
                    )
                    .Sort(sort => sort
                        .Descending(SortSpecialField.Score)
                        .Descending(d => d.AddedToApp))
                    .Source(src => src.Includes(i => i.Field(f => f.VideoId)))
                    .From((page - 1) * pageSize)
                    .Size(pageSize), cancellationToken);

                if (!searchResponse.IsValid)
                {
                    _logger.LogError("Search failed: {Error}", searchResponse.DebugInformation);
                    return new SearchResult();
                }

                return new SearchResult
                {
                    VideoIds = searchResponse.Documents.Select(d => d.VideoId).ToList(),
                    TotalCount = searchResponse.Total
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error performing search for query: {Query}", query);
                return new SearchResult();
            }
        }

        public async Task<List<string>> GetIndexedVideoIdsAsync(CancellationToken cancellationToken = default)
        {
            await EnsureIndexAsync(cancellationToken);

            var ids = new List<string>();
            const string scrollTimeout = "2m";

            var response = await _client.SearchAsync<VideoDocument>(s => s
                .Size(1000)
                .Scroll(scrollTimeout)
                .Source(src => src.Includes(i => i.Field(f => f.VideoId))), cancellationToken);

            try
            {
                while (true)
                {
                    if (!response.IsValid)
                    {
                        throw new InvalidOperationException(
                            $"Failed to enumerate indexed video IDs: {response.DebugInformation}");
                    }

                    if (!response.Documents.Any()) break;

                    ids.AddRange(response.Documents.Select(d => d.VideoId));
                    response = await _client.ScrollAsync<VideoDocument>(scrollTimeout, response.ScrollId, ct: cancellationToken);
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(response.ScrollId))
                {
                    await _client.ClearScrollAsync(c => c.ScrollId(response.ScrollId), cancellationToken);
                }
            }

            return ids;
        }
    }
}
