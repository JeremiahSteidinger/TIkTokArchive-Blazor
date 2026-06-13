using System.ComponentModel.DataAnnotations.Schema;

namespace TikTokArchive.Entities
{
    public enum LocalImportStatus
    {
        Success,
        Failed
    }

    public class LocalImportLog
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>Original file name (no path).</summary>
        public string FileName { get; set; } = string.Empty;

        public LocalImportStatus Status { get; set; }

        /// <summary>The TikTokVideoId assigned on success; null on failure.</summary>
        public string? VideoId { get; set; }

        /// <summary>The CreatedAt date that ended up being stored for the video.</summary>
        public DateTime? DateCreatedUsed { get; set; }

        /// <summary>When the import was attempted.</summary>
        public DateTime ImportedAt { get; set; }

        /// <summary>Full error message when Status == Failed.</summary>
        public string? ErrorMessage { get; set; }
    }
}
