using System.Text;
using System.Text.Json.Serialization;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Prompt construction and response-shaping shared by every <see cref="IVideoSummaryService"/>
    /// implementation, so the summary/tag rules stay identical across providers regardless of which
    /// LLM backend answers the request.
    /// </summary>
    internal static class AiSummaryPromptHelper
    {
        public sealed class SummaryPayload
        {
            [JsonPropertyName("summary")] public string? Summary { get; set; }
            [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        }

        public static string BuildSystemPrompt(AiEnrichmentOptions options) =>
            $"You summarize short videos from their caption and spoken transcript. " +
            $"Respond with ONLY a JSON object of the form {{\"summary\": string, \"tags\": string[]}}. " +
            $"The summary must be {options.MaxSummarySentences} sentences or fewer, describing what the video is about. " +
            $"Provide between {options.MinTags} and {options.MaxTags} topical tags that best fit the video. " +
            $"Each tag must be lowercase, a single word or hyphenated-phrase (no spaces), and contain no '#'. " +
            $"Do not add any text outside the JSON object.";

        /// <summary>The admin-edited prompt (AiProviderSettings.SystemPrompt) if set, else the generated default.</summary>
        public static string ResolveSystemPrompt(AiEnrichmentOptions options, string? customPrompt) =>
            string.IsNullOrWhiteSpace(customPrompt) ? BuildSystemPrompt(options) : customPrompt;

        public static string BuildUserContent(AiEnrichmentOptions options, string description, string? transcript)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(description))
            {
                builder.Append("Caption: ").AppendLine(description.Trim());
            }

            if (!string.IsNullOrWhiteSpace(transcript))
            {
                var trimmed = transcript.Trim();
                if (trimmed.Length > options.TranscriptCharLimit)
                {
                    trimmed = trimmed[..options.TranscriptCharLimit];
                }

                builder.Append("Transcript: ").AppendLine(trimmed);
            }

            return builder.ToString().Trim();
        }

        // Normalize LLM tags to share identity with TikTok tags: lowercase, '#'-stripped,
        // spaces collapsed to hyphens, deduplicated, length-bounded (Tag.Name is MaxLength 100),
        // and capped at MaxTags.
        public static List<string> CleanTags(AiEnrichmentOptions options, IEnumerable<string>? rawTags)
        {
            var cleaned = new List<string>();
            if (rawTags == null) return cleaned;

            var seen = new HashSet<string>();
            foreach (var raw in rawTags)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var tag = raw.Trim().TrimStart('#').Trim().ToLowerInvariant();
                tag = string.Join('-', tag.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

                if (tag.Length == 0 || tag.Length > 100) continue;
                if (!seen.Add(tag)) continue;

                cleaned.Add(tag);
                if (cleaned.Count >= options.MaxTags) break;
            }

            return cleaned;
        }
    }
}
