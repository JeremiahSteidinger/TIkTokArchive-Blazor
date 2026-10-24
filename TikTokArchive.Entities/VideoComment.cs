namespace TikTokArchive.Entities
{
    /// <summary>
    /// A comment downloaded from the video's platform. Root comments have a null
    /// <see cref="ParentId"/>; replies point at their root comment. A video's comments are
    /// replaced wholesale on every download, so <see cref="Position"/> records the order they
    /// were saved in (the sort order configured in <see cref="CommentDownloadSettings"/>).
    /// </summary>
    public class VideoComment
    {
        public int Id { get; set; }
        public int VideoId { get; set; }
        public int? ParentId { get; set; }

        /// <summary>The platform's own comment id (TikTok "cid").</summary>
        public string PlatformCommentId { get; set; } = string.Empty;
        public string AuthorUsername { get; set; } = string.Empty;
        public string AuthorDisplayName { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public int LikeCount { get; set; }

        /// <summary>Total replies on the platform — can exceed the replies actually saved.</summary>
        public int ReplyCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public int Position { get; set; }

        public virtual Video Video { get; set; } = default!;
        public virtual VideoComment? Parent { get; set; }
        public virtual ICollection<VideoComment> Replies { get; set; } = new List<VideoComment>();
    }
}
