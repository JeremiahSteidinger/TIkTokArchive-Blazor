using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TikTokArchive.Entities
{
    /// <summary>
    /// Represents a unique tag/hashtag
    /// </summary>
    public class Tag
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        
        [Required]
        [MaxLength(100)]
        public string Name { get; set; }
        
        public virtual ICollection<VideoTag> VideoTags { get; set; }
    }
}
