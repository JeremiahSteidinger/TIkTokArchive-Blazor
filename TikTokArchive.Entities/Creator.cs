using System.ComponentModel.DataAnnotations.Schema;

namespace TikTokArchive.Entities
{
    public class Creator
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public Platform Platform { get; set; }
        // The creator's @handle on their platform — the column predates Instagram support
        // and keeps its original name. Creators are unique per (Platform, TikTokId).
        public string TikTokId { get; set; }
        public string DisplayName { get; set; }
        public byte[]? ProfilePicture { get; set; }

        public virtual IEnumerable<Video> Videos { get; set; }
    }
}
