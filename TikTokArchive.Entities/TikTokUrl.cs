namespace TikTokArchive.Entities;

/// <summary>Builds canonical tiktok.com links for archived videos.</summary>
public static class TikTokUrl
{
    /// <summary>
    /// The canonical watch URL for a video: <c>https://www.tiktok.com/@{handle}/video/{id}</c>.
    /// Used both for the "open on TikTok" link and to re-fetch media with yt-dlp, so the
    /// creator must be loaded on <paramref name="video"/>.
    /// </summary>
    public static string ForVideo(Video video) =>
        ForVideo(video.Creator?.TikTokId, video.TikTokVideoId);

    /// <param name="creatorHandle">
    /// The creator's @handle — <see cref="Creator.TikTokId"/>, with or without the leading @.
    /// Falls back to the "_" placeholder that TikTok and yt-dlp both resolve by video id alone,
    /// so the link still works when the handle is missing.
    /// </param>
    public static string ForVideo(string? creatorHandle, string videoId)
    {
        var handle = creatorHandle?.TrimStart('@');
        return $"https://www.tiktok.com/@{(string.IsNullOrWhiteSpace(handle) ? "_" : handle)}/video/{videoId}";
    }
}
