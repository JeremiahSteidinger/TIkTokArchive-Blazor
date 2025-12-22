using System.ComponentModel.DataAnnotations.Schema;

namespace TikTokArchive.Entities
{
    public class Creator
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public string TikTokId { get; set; }
        public string DisplayName { get; set; }
        public byte[]? ProfilePicture { get; set; }

        public virtual IEnumerable<Video> Videos { get; set; }
    }
}
