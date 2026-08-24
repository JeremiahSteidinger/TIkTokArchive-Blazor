namespace TikTokArchive.Entities;

/// <summary>Builds the external watch URL for an archived video on any platform.</summary>
public static class PlatformUrl
{
    /// <summary>
    /// The watch URL for a video: the <see cref="Video.SourceUrl"/> captured at ingest when
    /// available, otherwise reconstructed from the platform and video id. Used both for the
    /// "open on platform" link and to re-fetch media with yt-dlp, so for legacy TikTok rows
    /// (null SourceUrl) the creator must be loaded on <paramref name="video"/>.
    /// </summary>
    public static string ForVideo(Video video)
    {
        if (!string.IsNullOrWhiteSpace(video.SourceUrl))
        {
            return video.SourceUrl;
        }

        // Instagram links resolve by shortcode alone; TikTok's fallback needs the @handle.
        return video.Platform == Platform.Instagram
            ? $"https://www.instagram.com/reel/{video.TikTokVideoId}/"
            : TikTokUrl.ForVideo(video);
    }

    public static string DisplayName(Platform platform) =>
        platform == Platform.Instagram ? "Instagram" : "TikTok";
}
