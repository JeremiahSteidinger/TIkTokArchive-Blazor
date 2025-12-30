namespace TikTokArchive.Entities
{
    public enum SearchIndexOperationType
    {
        Index,
        Delete
    }

    public class SearchIndexOperation
    {
        public int Id { get; set; }
        public SearchIndexOperationType OperationType { get; set; }
        public string VideoId { get; set; } = string.Empty;
        public int RetryCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastAttempt { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
