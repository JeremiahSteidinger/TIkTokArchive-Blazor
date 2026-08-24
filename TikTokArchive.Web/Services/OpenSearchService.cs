using OpenSearch.Client;
using OpenSearch.Net;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    // Deliberately has no Platform field: nothing filters search results by platform yet, and
    // adding one is a doc-shape change that forces an index-name bump (tiktok_videos_v5) plus a
    // full reindex via the Admin page. When a platform filter is wanted, add it as a Keyword.
    public class VideoDocument
    {
        public string VideoId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string CreatorName { get; set; } = string.Empty;
        public string CreatorUsername { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public string Transcript { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
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
            Summary = video.Summary ?? string.Empty,
            CreatedAt = video.CreatedAt,
            AddedToApp = video.AddedToApp
        };
    }

    public class OpenSearchService : ISearchService
    {
        // v2: tags and usernames are ngram-analyzed text (previously keyword + wildcard
        // queries). v3: adds the speech-to-text Transcript field to the mapping. v4: adds the
        // AI-generated Summary field. A new name lets the new mapping apply on a fresh index
        // without migrating the old one; the sync service repopulates it from the database.
        public const string IndexName = "tiktok_videos_v4";

        // All versioned indices share this prefix. Index management is scoped to it so the admin
        // UI can neither list nor delete OpenSearch's own system indices (.plugins-*, top_queries-*).
        public const string IndexPrefix = "tiktok_videos";

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
                            .Tokenizers(tz => tz
                                // Query-time ngram tokenizer: emits each gram at its OWN position.
                                // The index-time ngram_filter (below) emits a word's grams all at the
                                // same position, so a multi-term query against it collapses into a
                                // Synonym query (match-ANY), which makes operator AND a no-op. Giving
                                // the search analyzer distinct positions lets operator AND require
                                // every gram, turning the query into a real substring match.
                                .NGram("ngram_tokenizer", ng => ng
                                    .MinGram(3)
                                    .MaxGram(4)
                                    .TokenChars(TokenChar.Letter, TokenChar.Digit)
                                )
                            )
                            .Analyzers(an => an
                                .Custom("ngram_analyzer", ca => ca
                                    .Tokenizer("standard")
                                    .Filters("lowercase", "ngram_filter")
                                )
                                .Custom("ngram_search_analyzer", ca => ca
                                    .Tokenizer("ngram_tokenizer")
                                    .Filters("lowercase")
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
                    // Text fields are ngram-indexed (ngram_analyzer) so any substring is stored,
                    // and searched with ngram_search_analyzer so the query is broken into the same
                    // grams. The earlier standard search analyzer left query words of 5+ chars
                    // un-ngrammed, so they could never match the 3-4 char grams and search returned
                    // nothing. SearchAsync pairs this with operator AND for a true substring match.
                    .Map<VideoDocument>(m => m
                        .Properties(p => p
                            .Keyword(k => k.Name(n => n.VideoId))
                            .Text(t => t
                                .Name(n => n.Description)
                                .Analyzer("ngram_analyzer")
                                .SearchAnalyzer("ngram_search_analyzer")
                            )
                            .Text(t => t
                                .Name(n => n.CreatorName)
                                .Analyzer("ngram_analyzer")
                                .SearchAnalyzer("ngram_search_analyzer")
                            )
                            .Text(t => t
                                .Name(n => n.CreatorUsername)
                                .Analyzer("ngram_analyzer")
                                .SearchAnalyzer("ngram_search_analyzer")
                                .Fields(f => f.Keyword(k => k.Name("raw")))
                            )
                            .Text(t => t
                                .Name(n => n.Tags)
                                .Analyzer("ngram_analyzer")
                                .SearchAnalyzer("ngram_search_analyzer")
                                .Fields(f => f.Keyword(k => k.Name("raw")))
                            )
                            .Text(t => t
                                .Name(n => n.Transcript)
                                .Analyzer("ngram_analyzer")
                                .SearchAnalyzer("ngram_search_analyzer")
                            )
                            .Text(t => t
                                .Name(n => n.Summary)
                                .Analyzer("ngram_analyzer")
                                .SearchAnalyzer("ngram_search_analyzer")
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
                    fields = new List<string> { "description", "creator", "tags", "transcript", "summary" };
                }

                // Each clause uses operator AND so all of the query term's ngrams must match:
                // with the ngram search analyzer that turns into a substring match rather than
                // an OR over individual grams (which would match almost everything).
                if (fields.Contains("description"))
                {
                    shouldQueries.Add(q => q.Match(m => m
                        .Field(f => f.Description)
                        .Query(query)
                        .Operator(Operator.And)
                        .Boost(2.0)
                    ));
                }

                if (fields.Contains("creator"))
                {
                    shouldQueries.Add(q => q.Match(m => m
                        .Field(f => f.CreatorName)
                        .Query(query)
                        .Operator(Operator.And)
                        .Boost(1.5)
                    ));
                    shouldQueries.Add(q => q.Match(m => m
                        .Field(f => f.CreatorUsername)
                        .Query(query)
                        .Operator(Operator.And)
                        .Boost(1.5)
                    ));
                }

                if (fields.Contains("tags"))
                {
                    shouldQueries.Add(q => q.Match(m => m
                        .Field(f => f.Tags)
                        .Query(query)
                        .Operator(Operator.And)
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
                        .Operator(Operator.And)
                        .Boost(0.75)
                    ));
                }

                if (fields.Contains("summary"))
                {
                    // An AI paraphrase — useful but a step removed from the source, so it ranks
                    // just above the raw transcript and below tags.
                    shouldQueries.Add(q => q.Match(m => m
                        .Field(f => f.Summary)
                        .Query(query)
                        .Operator(Operator.And)
                        .Boost(0.9)
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

        public async Task<List<SearchIndexInfo>> GetIndicesAsync(CancellationToken cancellationToken = default)
        {
            var response = await _client.Cat.IndicesAsync(c => c
                .Index($"{IndexPrefix}*"), cancellationToken);

            if (!response.IsValid)
            {
                throw new InvalidOperationException(
                    $"Failed to list OpenSearch indices: {response.DebugInformation}");
            }

            return response.Records
                .Select(r => new SearchIndexInfo
                {
                    Name = r.Index,
                    Health = r.Health ?? "unknown",
                    DocsCount = long.TryParse(r.DocsCount, out var docs) ? docs : 0,
                    StoreSize = r.StoreSize ?? "0b",
                    IsActive = string.Equals(r.Index, IndexName, StringComparison.Ordinal)
                })
                .OrderBy(i => i.Name, StringComparer.Ordinal)
                .ToList();
        }

        public async Task DeleteIndexAsync(string indexName, CancellationToken cancellationToken = default)
        {
            // Guard rails: only this app's own indices, and never the live one. Protects
            // OpenSearch's system indices and the index search/indexing currently depend on.
            if (string.IsNullOrWhiteSpace(indexName) ||
                !indexName.StartsWith(IndexPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete '{indexName}': outside the '{IndexPrefix}' index family.");
            }

            if (string.Equals(indexName, IndexName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Cannot delete the active index '{IndexName}'.");
            }

            var response = await _client.Indices.DeleteAsync(indexName, ct: cancellationToken);

            if (!response.IsValid && response.ServerError?.Error?.Type != "index_not_found_exception")
            {
                throw new InvalidOperationException(
                    $"Failed to delete index {indexName}: {response.DebugInformation}");
            }

            _logger.LogInformation("Deleted OpenSearch index {IndexName}", indexName);
        }
    }
}
