namespace TikTokArchive.Entities
{
    /// <summary>
    /// Single-row, admin-editable configuration for comment downloads. Lives in the DB so the
    /// limits can be changed from the UI without restarting the app.
    /// Mirrors <see cref="AiProviderSettings"/>.
    /// </summary>
    public class CommentDownloadSettings
    {
        public int Id { get; set; }

        /// <summary>Download comments automatically when a video is archived.</summary>
        public bool DownloadOnIngest { get; set; }

        /// <summary>Maximum number of top-level comments saved per video.</summary>
        public int MaxRootComments { get; set; } = 50;

        /// <summary>Maximum number of replies saved per top-level comment. 0 skips replies.</summary>
        public int MaxRepliesPerComment { get; set; } = 5;

        /// <summary>Applies to both top-level comments and the replies under each one.</summary>
        public CommentSortOrder SortOrder { get; set; } = CommentSortOrder.Top;

        public DateTime LastModified { get; set; }
    }
}
