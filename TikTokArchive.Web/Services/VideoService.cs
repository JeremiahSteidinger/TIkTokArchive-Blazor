using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TikTokArchive.Entities;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services;

/// <summary>Lightweight transcript + AI-summary status for one video, for live status polling.</summary>
public record VideoStatus(string VideoId, TranscriptStatus TranscriptStatus, AiSummaryStatus AiSummaryStatus);

/// <summary>
/// A creator's header info plus one page of their videos (newest first). <c>MatchingCount</c>
/// counts the currently filtered set (drives paging); <c>TotalCount</c> is the creator's overall
/// saved-video count (shown in the header, independent of any tag filter).
/// </summary>
public record CreatorProfileResult(Creator Creator, List<Video> Videos, int MatchingCount, int TotalCount);

/// <summary>One row in the creators index: identity plus how many videos are saved for them.</summary>
public record CreatorListItem(int Id, string TikTokId, string DisplayName, int VideoCount);

/// <summary>A page of the creators index plus the total number of creators matching the search.</summary>
public record CreatorListResult(List<CreatorListItem> Creators, int TotalCount);

public interface IVideoService
{
    Task<(List<Video> Videos, int TotalCount)> GetVideosAsync(int page = 1, int pageSize = 20, string? tagFilter = null, string? searchQuery = null, List<string>? searchFields = null);
    Task<Video?> GetVideoAsync(string id);
    Task DeleteVideoAsync(string id);

    /// <summary>
    /// Queues a re-fetch of the video's media from TikTok, replacing the files on disk. The
    /// archive entry itself — description, tags, transcript and AI summary — is left untouched.
    /// Returns the queued job so callers can await <see cref="IngestJob.Completion"/>.
    /// </summary>
    Task<IngestJob> QueueRedownloadAsync(string videoId, CancellationToken ct = default);

    /// <summary>
    /// The current transcript + AI-summary status of the given videos, without loading the full
    /// entities. Used by the videos page to poll for queued → completed/failed transitions.
    /// </summary>
    Task<List<VideoStatus>> GetStatusesAsync(IReadOnlyCollection<string> videoIds, CancellationToken ct = default);

    /// <summary>
    /// Remove an AI-generated tag from a video. Only AI-sourced associations are removable —
    /// TikTok tags are left intact (returns false). Re-indexes so the tag drops out of search.
    /// </summary>
    Task<bool> RemoveTagFromVideoAsync(string videoId, int tagId, CancellationToken ct = default);

    /// <summary>
    /// Load a creator and one page of their videos, newest first, or null if no creator has the
    /// given id. <paramref name="tagFilter"/> narrows to videos carrying that tag.
    /// </summary>
    Task<CreatorProfileResult?> GetCreatorProfileAsync(int creatorId, int page = 1, int pageSize = 20, string? tagFilter = null);

    /// <summary>
    /// One page of the creators index, ordered by saved-video count (most first). An optional
    /// <paramref name="search"/> matches the display name or @username.
    /// </summary>
    Task<CreatorListResult> GetCreatorsAsync(int page = 1, int pageSize = 30, string? search = null);
}

public class VideoService : IVideoService
{
    private readonly IDbContextFactory<TikTokArchiveDbContext> dbContextFactory;
    private readonly ILogger<VideoService> logger;
    private readonly ISearchService searchService;
    private readonly SearchIndexSignal searchSignal;
    private readonly VideoIngestQueue ingestQueue;
    private readonly MediaStorageOptions mediaOptions;

    public VideoService(
        IDbContextFactory<TikTokArchiveDbContext> dbContextFactory,
        ILogger<VideoService> logger,
        ISearchService searchService,
        SearchIndexSignal searchSignal,
        VideoIngestQueue ingestQueue,
        IOptions<MediaStorageOptions> mediaOptions)
    {
        this.dbContextFactory = dbContextFactory;
        this.logger = logger;
        this.searchService = searchService;
        this.searchSignal = searchSignal;
        this.ingestQueue = ingestQueue;
        this.mediaOptions = mediaOptions.Value;
    }

    public async Task<(List<Video> Videos, int TotalCount)> GetVideosAsync(int page = 1, int pageSize = 20, string? tagFilter = null, string? searchQuery = null, List<string>? searchFields = null)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        // If search query provided, use search service
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchResult = await searchService.SearchAsync(searchQuery, page, pageSize, searchFields);
            var matchedVideos = await LoadVideosByIdsAsync(dbContext, searchResult.VideoIds);
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

    public async Task<CreatorProfileResult?> GetCreatorProfileAsync(int creatorId, int page = 1, int pageSize = 20, string? tagFilter = null)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var creator = await dbContext.Creators.FirstOrDefaultAsync(c => c.Id == creatorId);
        if (creator == null)
        {
            return null;
        }

        // Overall saved-video count for the header — independent of any tag filter.
        var totalCount = await dbContext.Videos.CountAsync(v => v.Creator.Id == creatorId);

        var query = dbContext.Videos
            .Include(v => v.Creator)
            .Include(v => v.Tags)
                .ThenInclude(vt => vt.Tag)
            .Where(v => v.Creator.Id == creatorId);

        if (!string.IsNullOrEmpty(tagFilter))
        {
            query = query.Where(v => v.Tags.Any(vt => vt.Tag.Name == tagFilter));
        }

        var matchingCount = await query.CountAsync();
        var videos = await query
            .OrderByDescending(v => v.AddedToApp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new CreatorProfileResult(creator, videos, matchingCount, totalCount);
    }

    public async Task<CreatorListResult> GetCreatorsAsync(int page = 1, int pageSize = 30, string? search = null)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var query = dbContext.Creators.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(c => c.DisplayName.Contains(term) || c.TikTokId.Contains(term));
        }

        var totalCount = await query.CountAsync();

        // Order/page on the entity (EF can't sort by a projected record member), then project the
        // video count as a correlated subquery so we never load the Videos rows or ProfilePicture blob.
        var creators = await query
            .OrderByDescending(c => c.Videos.Count())
            .ThenBy(c => c.DisplayName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CreatorListItem(c.Id, c.TikTokId, c.DisplayName, c.Videos.Count()))
            .ToListAsync();

        return new CreatorListResult(creators, totalCount);
    }

    public async Task<List<VideoStatus>> GetStatusesAsync(IReadOnlyCollection<string> videoIds, CancellationToken ct = default)
    {
        if (videoIds.Count == 0)
        {
            return new List<VideoStatus>();
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(ct);
        return await dbContext.Videos
            .AsNoTracking()
            .Where(v => videoIds.Contains(v.TikTokVideoId))
            .Select(v => new VideoStatus(v.TikTokVideoId, v.TranscriptStatus, v.AiSummaryStatus))
            .ToListAsync(ct);
    }

    private static async Task<List<Video>> LoadVideosByIdsAsync(TikTokArchiveDbContext dbContext, List<string> videoIds)
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
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        return await dbContext.Videos
            .Include(v => v.Creator)
            .Include(v => v.Tags)
            .FirstOrDefaultAsync(v => v.TikTokVideoId == id);
    }

    public async Task DeleteVideoAsync(string id)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
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

    public async Task<IngestJob> QueueRedownloadAsync(string videoId, CancellationToken ct = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(ct);
        var video = await dbContext.Videos
            .Include(v => v.Creator)
            .FirstOrDefaultAsync(v => v.TikTokVideoId == videoId, ct);

        if (video == null)
        {
            throw new KeyNotFoundException($"Video with ID {videoId} not found.");
        }

        // Uses the stored SourceUrl when present; legacy rows without one get a URL rebuilt
        // from the creator handle + video id.
        return ingestQueue.Enqueue(PlatformUrl.ForVideo(video), redownload: true);
    }

    public async Task<bool> RemoveTagFromVideoAsync(string videoId, int tagId, CancellationToken ct = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(ct);
        var videoTag = await dbContext.VideoTags
            .FirstOrDefaultAsync(vt => vt.Video.TikTokVideoId == videoId && vt.TagId == tagId, ct);

        // Only AI-sourced tags are user-removable; a missing row or a TikTok tag is a no-op.
        if (videoTag == null || videoTag.Source != TagSource.Ai)
        {
            return false;
        }

        dbContext.VideoTags.Remove(videoTag);
        // Re-index in the same SaveChanges so the tag also drops out of search.
        await SearchIndexOutbox.EnqueueIndexAsync(dbContext, videoId, ct);
        await dbContext.SaveChangesAsync(ct);
        searchSignal.Notify();
        return true;
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
