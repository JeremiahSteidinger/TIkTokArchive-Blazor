using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    public class SearchResult
    {
        public List<Video> Videos { get; set; } = new();
        public int TotalCount { get; set; }
    }

    public interface ISearchService
    {
        Task InitializeAsync();
        Task IndexVideoAsync(Video video);
        Task DeleteVideoAsync(string videoId);
        Task<SearchResult> SearchAsync(string query, int page, int pageSize, List<string>? fields = null);
        Task<List<string>> GetIndexedVideoIdsAsync();
        Task BulkReindexAsync(IProgress<int> progress, CancellationToken cancellationToken = default);
    }
}
