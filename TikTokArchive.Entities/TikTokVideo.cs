using Newtonsoft.Json;

namespace TikTokArchive.Entities;

// Field semantics differ by platform: TikTok fills uploader_id with the @handle and uploader
// with the display name; Instagram fills channel with the username, uploader with the full
// name, and uploader_id is often a numeric account id. The ingest worker maps per platform.
public class TikTokVideo
{
    [JsonProperty("id")]
    public string VideoId { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("uploader_id")]
    public string Uploader { get; set; } = string.Empty;

    [JsonProperty("uploader")]
    public string Channel { get; set; } = string.Empty;

    [JsonProperty("channel")]
    public string ChannelHandle { get; set; } = string.Empty;

    [JsonProperty("webpage_url")]
    public string? WebpageUrl { get; set; }

    [JsonProperty("timestamp")]
    public long Timestamp { get; set; }

    [JsonProperty("thumbnail")]
    public string Thumbnail { get; set; } = string.Empty;
}
