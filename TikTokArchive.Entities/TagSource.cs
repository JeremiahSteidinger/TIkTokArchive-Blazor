namespace TikTokArchive.Entities
{
    /// <summary>
    /// Where a <see cref="VideoTag"/> association came from. Lives on the join (not on
    /// <see cref="Tag"/>) because the same tag name can be a TikTok tag on one video and an
    /// AI-generated tag on another — provenance is per (video, tag) pair.
    /// </summary>
    public enum TagSource
    {
        /// <summary>
        /// Parsed from the source platform's description hashtags at ingest, whichever platform
        /// that is. The CLR default (0); the name predates Instagram support.
        /// </summary>
        TikTok = 0,

        /// <summary>Suggested by the AI enrichment pass. Visually distinct and user-removable.</summary>
        Ai = 1
    }
}
