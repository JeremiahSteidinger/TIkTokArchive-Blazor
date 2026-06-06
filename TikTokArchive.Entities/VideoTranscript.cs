using System.ComponentModel.DataAnnotations.Schema;

namespace TikTokArchive.Entities
{
    public class VideoTranscript
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public int VideoId { get; set; }
        public string TranscriptText { get; set; }
        public string Language { get; set; } = "en";
        public DateTime ProcessedAt { get; set; }
        public int ProcessingTimeSeconds { get; set; }
        public string Provider { get; set; }

        public virtual Video Video { get; set; }
    }
}
