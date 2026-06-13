namespace TikTokArchive.Web.Options
{
    public class YtDlpOptions
    {
        public const string SectionName = "YtDlp";

        /// <summary>
        /// Path to a Netscape-format cookies.txt passed to yt-dlp via --cookies.
        /// Required for age-restricted/login-gated TikTok posts. The default points
        /// into the container's data directory, where POST /api/cookies (used by the
        /// Firefox companion extension) writes synced browser cookies. When the file
        /// doesn't exist, yt-dlp runs without cookies.
        /// </summary>
        public string CookiesFile { get; set; } = "/app/data/cookies.txt";
    }
}
