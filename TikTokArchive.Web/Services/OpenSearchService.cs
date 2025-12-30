using Microsoft.EntityFrameworkCore;
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
        public DateTime CreatedAt { get; set; }
        public DateTime AddedToApp { get; set; }
    }

    public class OpenSearchService : ISearchService
    {
        private readonly IOpenSearchClient _client;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<OpenSearchService> _logger;
        private const string IndexName = "tiktok_videos";

        public OpenSearchService(IConfiguration configuration, IServiceProvider serviceProvider, ILogger<OpenSearchService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;

            var openSearchUrl = configuration.GetValue<string>("OpenSearch:Url") ?? "http://opensearch:9200";
            var settings = new ConnectionSettings(new Uri(openSearchUrl))
                .DefaultIndex(IndexName)
                .DisableDirectStreaming()
                .OnRequestCompleted(details =>
                {
                    if (!details.Success)
                    {
                        _logger.LogError("OpenSearch request to {Method} {Uri} failed: {DebugInformation}",
                            details.HttpMethod, details.Uri, details.DebugInformation);
                    }
                });

            _client = new OpenSearchClient(settings);
        }

        public async Task InitializeAsync()
        {
            try
            {
                var existsResponse = await _client.Indices.ExistsAsync(IndexName);
                if (existsResponse.Exists)
                {
                    _logger.LogInformation("OpenSearch index {IndexName} already exists", IndexName);
                    return;
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
                            .Keyword(k => k.Name(n => n.CreatorUsername))
                            .Keyword(k => k.Name(n => n.Tags))
                            .Date(d => d.Name(n => n.CreatedAt))
                            .Date(d => d.Name(n => n.AddedToApp))
                        )
                    )
                );

                if (createResponse.IsValid)
                {
                    _logger.LogInformation("OpenSearch index {IndexName} created successfully", IndexName);
                }
                else
                {
                    _logger.LogError("Failed to create OpenSearch index: {Error}", createResponse.DebugInformation);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing OpenSearch index");
            }
        }

        public async Task IndexVideoAsync(Video video)
        {
            try
            {
                var document = new VideoDocument
                {
                    VideoId = video.TikTokVideoId,
                    Description = video.Description ?? string.Empty,
                    CreatorName = video.Creator?.DisplayName ?? string.Empty,
                    CreatorUsername = video.Creator?.TikTokId ?? string.Empty,
                    Tags = video.Tags?.Select(vt => vt.Tag.Name).ToList() ?? new List<string>(),
                    CreatedAt = video.CreatedAt,
                    AddedToApp = video.AddedToApp
                };

                var response = await _client.IndexAsync(document, i => i
                    .Id(video.TikTokVideoId)
                    .Refresh(Refresh.False)
                );

                if (!response.IsValid)
                {
                    throw new Exception($"Failed to index video: {response.DebugInformation}");
                }

                _logger.LogDebug("Successfully indexed video {VideoId}", video.TikTokVideoId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error indexing video {VideoId}", video.TikTokVideoId);
                throw;
            }
        }

        public async Task DeleteVideoAsync(string videoId)
        {
            try
            {
                var response = await _client.DeleteAsync<VideoDocument>(videoId, d => d
                    .Refresh(Refresh.False)
                );

                if (!response.IsValid && response.Result != Result.NotFound)
                {
                    throw new Exception($"Failed to delete video from index: {response.DebugInformation}");
                }

                _logger.LogDebug("Successfully deleted video {VideoId} from index", videoId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting video {VideoId} from index", videoId);
                throw;
            }
        }

        public async Task<SearchResult> SearchAsync(string query, int page, int pageSize, List<string>? fields = null)
        {
            try
            {
                var shouldQueries = new List<Func<QueryContainerDescriptor<VideoDocument>, QueryContainer>>();

                if (fields == null || fields.Count == 0 || fields.Contains("all"))
                {
                    fields = new List<string> { "description", "creator", "tags" };
                }

                if (fields.Contains("description"))
                {
                    shouldQueries.Add(q => q.Match(m => m
                        .Field(f => f.Description)
                        .Query(query)
                        .Fuzziness(Fuzziness.Auto)
                        .Boost(2.0)
                    ));
                }

                if (fields.Contains("creator"))
                {
                    shouldQueries.Add(q => q.Match(m => m
                        .Field(f => f.CreatorName)
                        .Query(query)
                        .Fuzziness(Fuzziness.Auto)
                        .Boost(1.5)
                    ));
                    shouldQueries.Add(q => q.Wildcard(w => w
                        .Field(f => f.CreatorUsername)
                        .Value($"*{query.ToLower()}*")
                    ));
                }

                if (fields.Contains("tags"))
                {
                    shouldQueries.Add(q => q.Wildcard(w => w
                        .Field(f => f.Tags)
                        .Value($"*{query.ToLower()}*")
                        .Boost(1.0)
                    ));
                }

                var searchResponse = await _client.SearchAsync<VideoDocument>(s => s
                    .Query(q => q
                        .Bool(b => b
                            .Should(shouldQueries.ToArray())
                            .MinimumShouldMatch(1)
                        )
                    )
                    .Sort(sort => sort.Descending(d => d.AddedToApp))
                    .From((page - 1) * pageSize)
                    .Size(pageSize)
                );

                if (!searchResponse.IsValid)
                {
                    _logger.LogError("Search failed: {Error}", searchResponse.DebugInformation);
                    return new SearchResult { Videos = new List<Video>(), TotalCount = 0 };
                }

                var videoIds = searchResponse.Documents.Select(d => d.VideoId).ToList();

                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();

                var videos = await dbContext.Videos
                    .Include(v => v.Creator)
                    .Include(v => v.Tags).ThenInclude(vt => vt.Tag)
                    .Where(v => videoIds.Contains(v.TikTokVideoId))
                    .ToListAsync();

                var orderedVideos = videoIds
                    .Select(id => videos.FirstOrDefault(v => v.TikTokVideoId == id))
                    .Where(v => v != null)
                    .Cast<Video>()
                    .ToList();

                return new SearchResult
                {
                    Videos = orderedVideos,
                    TotalCount = (int)searchResponse.Total
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error performing search for query: {Query}", query);
                return new SearchResult { Videos = new List<Video>(), TotalCount = 0 };
            }
        }

        public async Task<List<string>> GetIndexedVideoIdsAsync()
        {
            try
            {
                var searchResponse = await _client.SearchAsync<VideoDocument>(s => s
                    .Size(10000)
                    .Source(src => src.Includes(i => i.Field(f => f.VideoId)))
                );

                if (!searchResponse.IsValid)
                {
                    _logger.LogError("Failed to get indexed video IDs: {Error}", searchResponse.DebugInformation);
                    return new List<string>();
                }

                return searchResponse.Documents.Select(d => d.VideoId).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting indexed video IDs");
                return new List<string>();
            }
        }

        public async Task BulkReindexAsync(IProgress<int> progress, CancellationToken cancellationToken = default)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();

                var totalVideos = await dbContext.Videos.CountAsync(cancellationToken);
                var processedCount = 0;
                const int batchSize = 100;

                _logger.LogInformation("Starting bulk reindex of {TotalVideos} videos", totalVideos);

                for (var skip = 0; skip < totalVideos; skip += batchSize)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var videos = await dbContext.Videos
                        .Include(v => v.Creator)
                        .Include(v => v.Tags).ThenInclude(vt => vt.Tag)
                        .OrderBy(v => v.Id)
                        .Skip(skip)
                        .Take(batchSize)
                        .ToListAsync(cancellationToken);

                    var bulkDescriptor = new BulkDescriptor();

                    foreach (var video in videos)
                    {
                        var document = new VideoDocument
                        {
                            VideoId = video.TikTokVideoId,
                            Description = video.Description ?? string.Empty,
                            CreatorName = video.Creator?.DisplayName ?? string.Empty,
                            CreatorUsername = video.Creator?.TikTokId ?? string.Empty,
                            Tags = video.Tags?.Select(vt => vt.Tag.Name).ToList() ?? new List<string>(),
                            CreatedAt = video.CreatedAt,
                            AddedToApp = video.AddedToApp
                        };

                        bulkDescriptor.Index<VideoDocument>(i => i
                            .Document(document)
                            .Id(video.TikTokVideoId)
                        );
                    }

                    var bulkResponse = await _client.BulkAsync(bulkDescriptor, cancellationToken);

                    if (!bulkResponse.IsValid)
                    {
                        _logger.LogError("Bulk index failed for batch: {Error}", bulkResponse.DebugInformation);
                    }

                    processedCount += videos.Count;
                    progress.Report(processedCount);
                }

                await _client.Indices.RefreshAsync(IndexName);
                _logger.LogInformation("Bulk reindex completed: {ProcessedCount} videos indexed", processedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during bulk reindex");
                throw;
            }
        }
    }
}
