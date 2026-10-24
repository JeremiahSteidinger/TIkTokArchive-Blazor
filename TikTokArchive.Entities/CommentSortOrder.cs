namespace TikTokArchive.Entities
{
    /// <summary>Which comments are kept (and in what order) when downloading a video's comments.</summary>
    public enum CommentSortOrder
    {
        /// <summary>The platform's own ranking — what the app shows first.</summary>
        Top = 0,
        MostLiked = 1,
        Newest = 2,
        Oldest = 3
    }
}
