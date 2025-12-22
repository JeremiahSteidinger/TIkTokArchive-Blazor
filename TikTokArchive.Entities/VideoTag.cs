using System.ComponentModel.DataAnnotations.Schema;

namespace TikTokArchive.Entities
{
    public class VideoTag
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public string Tag { get; set; }
        public virtual Video Video { get; set; }
    }
}
