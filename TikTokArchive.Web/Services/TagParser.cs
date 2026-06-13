namespace TikTokArchive.Web.Services
{
    public static class TagParser
    {
        /// <summary>
        /// Extracts #hashtags from a video description and returns the description with
        /// the hashtags removed alongside the distinct, lowercased tag names.
        /// </summary>
        public static (string CleanedDescription, List<string> TagNames) Parse(string? description)
        {
            if (string.IsNullOrEmpty(description))
            {
                return (string.Empty, new List<string>());
            }

            var words = description.Split(' ');

            // Matches the [MaxLength(100)] on Tag.Name — longer "hashtags" are garbage
            // runs of text and would fail the insert.
            const int maxTagLength = 100;

            var tagNames = words
                .Where(word => word.StartsWith('#'))
                .Select(tag => tag.TrimStart('#').ToLowerInvariant())
                .Where(tagName => !string.IsNullOrWhiteSpace(tagName) && tagName.Length <= maxTagLength)
                .Distinct()
                .ToList();

            var cleanedDescription = string.Join(' ', words.Where(word => !word.StartsWith('#'))).Trim();

            return (cleanedDescription, tagNames);
        }
    }
}
