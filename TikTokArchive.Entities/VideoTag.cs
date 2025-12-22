using System.ComponentModel.DataAnnotations.Schema;

namespace TikTokArchive.Entities
{
    /// <summary>
    /// Join table for many-to-many relationship between Videos and Tags
    /// </summary>
    public class VideoTag
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        
        public int VideoId { get; set; }
        public virtual Video Video { get; set; }
        
        public int TagId { get; set; }
        public virtual Tag Tag { get; set; }
    }
}
