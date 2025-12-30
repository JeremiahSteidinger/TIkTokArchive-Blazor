namespace TikTokArchive.Entities
{
    public class SearchIndexConfiguration
    {
        public int Id { get; set; }
        public int SyncIntervalMinutes { get; set; } = 30;
        public DateTime LastModified { get; set; }
    }
}
