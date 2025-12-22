using System.ComponentModel.DataAnnotations.Schema;

namespace TikTokArchive.Entities
{
    public class Video
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public string TikTokVideoId { get; set; }
        public string Description { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime AddedToApp { get; set; }

        public virtual Creator Creator { get; set; }
        public virtual IEnumerable<VideoTag> Tags { get; set; }
    }
}
