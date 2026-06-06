using System.ComponentModel.DataAnnotations.Schema;

namespace TikTokArchive.Entities
{
    public enum TranscriptionStatus
    {
        Pending,
        Processing,
        Completed,
        Failed
    }

    public class TranscriptionQueueItem
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public int VideoId { get; set; }
        public TranscriptionStatus Status { get; set; } = TranscriptionStatus.Pending;
        public DateTime QueuedAt { get; set; }
        public DateTime? ProcessedAt { get; set; }
        public int RetryCount { get; set; } = 0;
        public string? ErrorMessage { get; set; }
        public string Provider { get; set; }

        public virtual Video Video { get; set; }
    }
}
