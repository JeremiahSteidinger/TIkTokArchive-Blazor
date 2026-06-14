using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Shared tag-resolution idiom used by both video ingest (TikTok hashtags) and AI
    /// enrichment (LLM-suggested tags). Tag identity follows the database's accent/case-
    /// insensitive collation, not ordinal equality: "#françoisarnaud" and "#francoisarnaud"
    /// are the same tag to the unique index on <see cref="Tag.Name"/>.
    /// </summary>
    public static class TagUpsertHelper
    {
        /// <summary>
        /// Resolves each name to an existing <see cref="Tag"/> (matched by the database's
        /// collation) or a new one, and returns <see cref="VideoTag"/> rows carrying
        /// <paramref name="source"/>. Names whose accent-insensitive key appears in
        /// <paramref name="skipKeys"/> are skipped (e.g. a tag the video already has), as are
        /// in-batch duplicates. The returned VideoTags have only Tag and Source set; the caller
        /// attaches them to a video (via the navigation on a new video, or by setting VideoId on
        /// an existing one) and saves.
        /// </summary>
        public static async Task<List<VideoTag>> BuildVideoTagsAsync(
            TikTokArchiveDbContext dbContext,
            IEnumerable<string> tagNames,
            TagSource source,
            ISet<string>? skipKeys = null,
            CancellationToken cancellationToken = default)
        {
            var videoTags = new List<VideoTag>();
            var tagsByKey = new Dictionary<string, Tag>();
            foreach (var tagName in tagNames)
            {
                var key = AccentInsensitiveKey(tagName);
                if (skipKeys != null && skipKeys.Contains(key)) continue;
                if (tagsByKey.ContainsKey(key)) continue;

                var tag = await dbContext.Tags
                    .FirstOrDefaultAsync(t => t.Name == tagName, cancellationToken)
                    ?? new Tag { Name = tagName };
                tagsByKey[key] = tag;
                videoTags.Add(new VideoTag { Tag = tag, Source = source });
            }

            return videoTags;
        }

        /// <summary>
        /// Accent-stripped, lowercased form of a tag name — the key used to deduplicate tags
        /// that the database collation would treat as equal.
        /// </summary>
        public static string AccentInsensitiveKey(string value)
        {
            var decomposed = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }
    }
}
