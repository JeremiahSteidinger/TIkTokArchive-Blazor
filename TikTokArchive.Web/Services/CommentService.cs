using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services;

public interface ICommentService
{
    /// <summary>Only TikTok comments can be downloaded; Instagram has no comment source yet.</summary>
    bool SupportsComments(Platform platform);

    /// <summary>The saved settings row, or the defaults when none has been saved yet.</summary>
    Task<CommentDownloadSettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the video's comments using the current settings and replaces any previously
    /// saved ones. Returns the number of comments saved (top-level plus replies). Throws when
    /// the video doesn't exist, its platform isn't supported, or TikTok refuses the request.
    /// </summary>
    Task<int> DownloadCommentsAsync(string videoId, CancellationToken ct = default);

    /// <summary>The video's saved top-level comments, in saved order, each with its replies.</summary>
    Task<List<VideoComment>> GetCommentsAsync(string videoId, CancellationToken ct = default);
}

public class CommentService : ICommentService
{
    public const int MaxRootCommentsLimit = 500;
    public const int MaxRepliesPerCommentLimit = 100;

    // TikTok's API has no sort parameter — it always returns its own ranking. For any other
    // order we read a larger pool and pick from that, so "Newest" means the newest of the first
    // N×multiplier comments rather than of the first N. Capped so a huge thread stays bounded.
    private const int SortPoolMultiplier = 5;
    private const int MaxSortPool = 1000;

    private readonly IDbContextFactory<TikTokArchiveDbContext> dbContextFactory;
    private readonly ITikTokCommentClient tikTokClient;
    private readonly ILogger<CommentService> logger;

    public CommentService(
        IDbContextFactory<TikTokArchiveDbContext> dbContextFactory,
        ITikTokCommentClient tikTokClient,
        ILogger<CommentService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.tikTokClient = tikTokClient;
        this.logger = logger;
    }

    public bool SupportsComments(Platform platform) => platform == Platform.TikTok;

    public async Task<CommentDownloadSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(ct);
        return await dbContext.CommentDownloadSettings.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? new CommentDownloadSettings();
    }

    public async Task<int> DownloadCommentsAsync(string videoId, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        var maxRoots = Math.Clamp(settings.MaxRootComments, 1, MaxRootCommentsLimit);
        var maxReplies = Math.Clamp(settings.MaxRepliesPerComment, 0, MaxRepliesPerCommentLimit);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(ct);
        var video = await dbContext.Videos.FirstOrDefaultAsync(v => v.TikTokVideoId == videoId, ct)
            ?? throw new InvalidOperationException($"Video {videoId} not found");
        if (!SupportsComments(video.Platform))
        {
            throw new NotSupportedException($"Downloading comments isn't supported for {PlatformUrl.DisplayName(video.Platform)} videos");
        }

        // Fetch everything before touching the database, so a failure part-way keeps the old comments.
        var roots = Select(
            await tikTokClient.GetCommentsAsync(videoId, PoolSize(maxRoots, settings.SortOrder), ct),
            settings.SortOrder, maxRoots);

        var newComments = new List<VideoComment>();
        var position = 0;
        foreach (var root in roots)
        {
            var entity = ToEntity(root, video, position++);
            if (maxReplies > 0 && root.ReplyCount > 0)
            {
                var replies = Select(
                    await tikTokClient.GetRepliesAsync(videoId, root.Id, PoolSize(maxReplies, settings.SortOrder), ct),
                    settings.SortOrder, maxReplies);
                var replyPosition = 0;
                foreach (var reply in replies)
                {
                    entity.Replies.Add(ToEntity(reply, video, replyPosition++));
                }
            }
            newComments.Add(entity);
        }

        // Swap old for new in one SaveChanges so readers never see a half-replaced set.
        var existing = await dbContext.VideoComments.Where(c => c.VideoId == video.Id).ToListAsync(ct);
        dbContext.VideoComments.RemoveRange(existing);
        dbContext.VideoComments.AddRange(newComments);
        video.CommentsFetchedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        var saved = newComments.Count + newComments.Sum(c => c.Replies.Count);
        logger.LogInformation("Saved {Count} comment(s) for video {VideoId}", saved, videoId);
        return saved;
    }

    public async Task<List<VideoComment>> GetCommentsAsync(string videoId, CancellationToken ct = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(ct);
        return await dbContext.VideoComments
            .AsNoTracking()
            .Where(c => c.Video.TikTokVideoId == videoId && c.ParentId == null)
            .OrderBy(c => c.Position)
            .Include(c => c.Replies.OrderBy(r => r.Position))
            .ToListAsync(ct);
    }

    private static int PoolSize(int limit, CommentSortOrder sortOrder) =>
        sortOrder == CommentSortOrder.Top ? limit : Math.Min(limit * SortPoolMultiplier, Math.Max(limit, MaxSortPool));

    private static IEnumerable<TikTokComment> Select(List<TikTokComment> pool, CommentSortOrder sortOrder, int limit)
    {
        IEnumerable<TikTokComment> ordered = sortOrder switch
        {
            CommentSortOrder.MostLiked => pool.OrderByDescending(c => c.LikeCount),
            CommentSortOrder.Newest => pool.OrderByDescending(c => c.CreatedAt),
            CommentSortOrder.Oldest => pool.OrderBy(c => c.CreatedAt),
            _ => pool
        };
        // The API can repeat a comment across pages; keep the first occurrence.
        return ordered.DistinctBy(c => c.Id).Take(limit);
    }

    private static VideoComment ToEntity(TikTokComment comment, Video video, int position) => new()
    {
        Video = video,
        PlatformCommentId = comment.Id,
        AuthorUsername = comment.AuthorUsername,
        AuthorDisplayName = comment.AuthorDisplayName,
        Text = comment.Text,
        LikeCount = comment.LikeCount,
        ReplyCount = comment.ReplyCount,
        CreatedAt = comment.CreatedAt,
        Position = position
    };
}
