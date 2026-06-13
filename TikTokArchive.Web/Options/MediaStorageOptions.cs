namespace TikTokArchive.Web.Options
{
    public class MediaStorageOptions
    {
        public const string SectionName = "MediaStorage";

        public string VideosPath { get; set; } = "/media/videos";
        public string ThumbnailsPath { get; set; } = "/media/thumbnails";
    }
}
