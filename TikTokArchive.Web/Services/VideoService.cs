using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TikTokArchive.Entities;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services;

public interface IVideoService
{
    Task<(List<Video> Videos, int TotalCount)> GetVideosAsync(int page = 1, int pageSize = 20, string? tagFilter = null, string? searchQuery = null, List<string>? searchFields = null);
    Task<Video?> GetVideoAsync(string id);
    Task DeleteVideoAsync(string id);
}

public class VideoService : IVideoService
{
    private readonly TikTokArchiveDbContext dbContext;
    private readonly ILogger<VideoService> logger;
    private readonly ISearchService searchService;
    private readonly SearchIndexSignal searchSignal;
    private readonly MediaStorageOptions mediaOptions;

    public VideoService(
        TikTokArchiveDbContext dbContext,
        ILogger<VideoService> logger,
        ISearchService searchService,
        SearchIndexSignal searchSignal,
        IOptions<MediaStorageOptions> mediaOptions)
    {
        this.dbContext = dbContext;
        this.logger = logger;
        this.searchService = searchService;
        this.searchSignal = searchSignal;
        this.mediaOptions = mediaOptions.Value;
    }

    public async Task<(List<Video> Videos, int TotalCount)> GetVideosAsync(int page = 1, int pageSize = 20, string? tagFilter = null, string? searchQuery = null, List<string>? searchFields = null)
    {
        // If search query provided, use search service
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchResult = await searchService.SearchAsync(searchQuery, page, pageSize, searchFields);
            var matchedVideos = await LoadVideosByIdsAsync(searchResult.VideoIds);
            return (matchedVideos, (int)searchResult.TotalCount);
        }

        var query = dbContext.Videos
            .Include(v => v.Creator)
            .Include(v => v.Tags)
                .ThenInclude(vt => vt.Tag)
            .AsQueryable();

        // Apply tag filter if specified
        if (!string.IsNullOrEmpty(tagFilter))
        {
            query = query.Where(v => v.Tags.Any(vt => vt.Tag.Name == tagFilter));
        }

        // Get total count before paging
        var totalCount = await query.CountAsync();

        // Apply paging
        var videos = await query
            .OrderByDescending(v => v.AddedToApp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (videos, totalCount);
    }

    private async Task<List<Video>> LoadVideosByIdsAsync(List<string> videoIds)
    {
        if (videoIds.Count == 0)
        {
            return new List<Video>();
        }

        var videos = await dbContext.Videos
            .Include(v => v.Creator)
            .Include(v => v.Tags).ThenInclude(vt => vt.Tag)
            .Where(v => videoIds.Contains(v.TikTokVideoId))
            .ToListAsync();

        // Preserve the search engine's relevance ordering
        return videoIds
            .Select(id => videos.FirstOrDefault(v => v.TikTokVideoId == id))
            .Where(v => v != null)
            .Cast<Video>()
            .ToList();
    }

    public async Task<Video?> GetVideoAsync(string id)
    {
        return await dbContext.Videos
            .Include(v => v.Creator)
            .Include(v => v.Tags)
            .FirstOrDefaultAsync(v => v.TikTokVideoId == id);
    }

    public async Task DeleteVideoAsync(string id)
    {
        var video = await dbContext.Videos
            .Include(v => v.Tags)
            .FirstOrDefaultAsync(v => v.TikTokVideoId == id);

        if (video == null)
        {
            throw new KeyNotFoundException($"Video with ID {id} not found.");
        }

        // Remove the video and queue the index deletion atomically; the outbox worker
        // applies it to OpenSearch afterwards.
        dbContext.Videos.Remove(video);
        dbContext.SearchIndexOperations.Add(new SearchIndexOperation
        {
            OperationType = SearchIndexOperationType.Delete,
            VideoId = id,
            CreatedAt = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();
        searchSignal.Notify();

        // Media files go last: an orphaned file is invisible, but a database row pointing
        // at a deleted file would show up as a broken video in the UI.
        DeleteMediaFiles(mediaOptions.VideosPath, id);
        DeleteMediaFiles(mediaOptions.ThumbnailsPath, id);

        var sanitizedId = id.Replace("\r", string.Empty).Replace("\n", string.Empty);
        logger.LogInformation("Successfully deleted video with ID {VideoId}", sanitizedId);
    }

    private void DeleteMediaFiles(string directory, string videoId)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var file in Directory.GetFiles(directory, $"{videoId}.*"))
        {
            try
            {
                File.Delete(file);
                logger.LogInformation("Deleted media file: {File}", file);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete media file {File}", file);
            }
        }
    }
}
