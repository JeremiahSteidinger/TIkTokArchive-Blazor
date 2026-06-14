using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    public class SearchResult
    {
        public List<string> VideoIds { get; set; } = new();
        public long TotalCount { get; set; }
    }

    /// <summary>Summary stats for one OpenSearch index, for the admin management view.</summary>
    public class SearchIndexInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Health { get; set; } = string.Empty;
        public long DocsCount { get; set; }
        public string StoreSize { get; set; } = string.Empty;
        /// <summary>True for the index search/indexing currently use (not deletable).</summary>
        public bool IsActive { get; set; }
    }

    public interface ISearchService
    {
        Task IndexVideoAsync(Video video, CancellationToken cancellationToken = default);
        Task IndexVideosAsync(IReadOnlyCollection<Video> videos, CancellationToken cancellationToken = default);
        Task DeleteVideoAsync(string videoId, CancellationToken cancellationToken = default);
        Task<SearchResult> SearchAsync(string query, int page, int pageSize, List<string>? fields = null, CancellationToken cancellationToken = default);
        Task<List<string>> GetIndexedVideoIdsAsync(CancellationToken cancellationToken = default);

        /// <summary>Lists this app's OpenSearch indices (the IndexPrefix family) with basic stats.</summary>
        Task<List<SearchIndexInfo>> GetIndicesAsync(CancellationToken cancellationToken = default);

        /// <summary>Deletes one of this app's indices. Refuses the active index and anything outside the family.</summary>
        Task DeleteIndexAsync(string indexName, CancellationToken cancellationToken = default);
    }
}
