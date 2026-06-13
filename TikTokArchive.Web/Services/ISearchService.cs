using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    public class SearchResult
    {
        public List<string> VideoIds { get; set; } = new();
        public long TotalCount { get; set; }
    }

    public interface ISearchService
    {
        Task IndexVideoAsync(Video video, CancellationToken cancellationToken = default);
        Task IndexVideosAsync(IReadOnlyCollection<Video> videos, CancellationToken cancellationToken = default);
        Task DeleteVideoAsync(string videoId, CancellationToken cancellationToken = default);
        Task<SearchResult> SearchAsync(string query, int page, int pageSize, List<string>? fields = null, CancellationToken cancellationToken = default);
        Task<List<string>> GetIndexedVideoIdsAsync(CancellationToken cancellationToken = default);
    }
}
