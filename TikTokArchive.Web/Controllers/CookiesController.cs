using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Controllers
{
    /// <summary>
    /// Receives TikTok and Instagram cookies from the Firefox companion extension and stores
    /// them in Netscape cookies.txt format at <see cref="YtDlpOptions.CookiesFile"/>, where
    /// yt-dlp picks them up for age-restricted/login-gated posts. Cookies for a platform that
    /// isn't in the push are kept from the existing jar, so syncing from one browser doesn't
    /// wipe the other platform's login.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class CookiesController : ControllerBase
    {
        private readonly YtDlpOptions _options;
        private readonly ILogger<CookiesController> _logger;

        public CookiesController(IOptions<YtDlpOptions> options, ILogger<CookiesController> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public class BrowserCookie
        {
            public string Name { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
            public string Domain { get; set; } = string.Empty;
            public string Path { get; set; } = "/";
            public bool Secure { get; set; }
            public bool HostOnly { get; set; }
            public double? ExpirationDate { get; set; }
        }

        [HttpPost]
        public async Task<IActionResult> Update([FromBody] List<BrowserCookie> cookies)
        {
            if (string.IsNullOrEmpty(_options.CookiesFile))
            {
                return StatusCode(StatusCodes.Status501NotImplemented, "YtDlp:CookiesFile is not configured");
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var lines = new List<string>
            {
                "# Netscape HTTP Cookie File",
                "# Synced from the TikTok Archive browser extension"
            };

            var count = 0;
            var pushedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cookie in cookies)
            {
                var domain = cookie.Domain.TrimStart('.');
                var group = DomainGroup(domain);
                if (group == null) continue;
                pushedGroups.Add(group);

                // Expired cookies and values that would corrupt the tab-separated jar are skipped.
                if (cookie.ExpirationDate.HasValue && cookie.ExpirationDate.Value < now) continue;
                if (ContainsJarBreakingChars(cookie.Name) || ContainsJarBreakingChars(cookie.Value) ||
                    ContainsJarBreakingChars(cookie.Path) || ContainsJarBreakingChars(domain)) continue;

                var includeSubdomains = !cookie.HostOnly;
                var domainField = includeSubdomains ? "." + domain : domain;
                var expiry = cookie.ExpirationDate.HasValue ? ((long)cookie.ExpirationDate.Value).ToString() : "0";

                lines.Add(string.Join('\t',
                    domainField,
                    includeSubdomains ? "TRUE" : "FALSE",
                    cookie.Path,
                    cookie.Secure ? "TRUE" : "FALSE",
                    expiry,
                    cookie.Name,
                    cookie.Value));
                count++;
            }

            if (count == 0)
            {
                return BadRequest("No valid TikTok or Instagram cookies in the request");
            }

            // Keep existing jar lines for platforms this push didn't include, so a TikTok-only
            // sync can't log the archive out of Instagram (or vice versa).
            lines.AddRange(await ReadRetainedLinesAsync(pushedGroups));

            var directory = System.IO.Path.GetDirectoryName(_options.CookiesFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write-then-move so a concurrent yt-dlp run never sees a half-written jar.
            // UTF-8 without BOM: a BOM breaks yt-dlp's Netscape header check.
            var tempPath = _options.CookiesFile + ".tmp";
            await System.IO.File.WriteAllTextAsync(tempPath, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
            System.IO.File.Move(tempPath, _options.CookiesFile, overwrite: true);

            _logger.LogInformation(
                "Stored {Count} cookies for yt-dlp ({Groups})", count, string.Join(", ", pushedGroups));
            return Ok(new { count });
        }

        [HttpGet("status")]
        public IActionResult Status()
        {
            var fileInfo = new FileInfo(_options.CookiesFile);
            return Ok(new
            {
                exists = fileInfo.Exists,
                lastModified = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : (DateTime?)null
            });
        }

        /// <summary>
        /// The platform family a cookie domain belongs to, or null for domains the archive has
        /// no use for. The group name doubles as the retention key when merging jars.
        /// </summary>
        private static string? DomainGroup(string domain)
        {
            foreach (var root in new[] { "tiktok.com", "instagram.com" })
            {
                if (domain.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    domain.EndsWith("." + root, StringComparison.OrdinalIgnoreCase))
                {
                    return root;
                }
            }
            return null;
        }

        /// <summary>
        /// Cookie lines from the existing jar whose platform wasn't part of this push.
        /// </summary>
        private async Task<List<string>> ReadRetainedLinesAsync(IReadOnlySet<string> pushedGroups)
        {
            var retained = new List<string>();
            if (!System.IO.File.Exists(_options.CookiesFile))
            {
                return retained;
            }

            foreach (var line in await System.IO.File.ReadAllLinesAsync(_options.CookiesFile))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

                var domain = line.Split('\t')[0].TrimStart('.');
                var group = DomainGroup(domain);
                if (group != null && !pushedGroups.Contains(group))
                {
                    retained.Add(line);
                }
            }
            return retained;
        }

        private static bool ContainsJarBreakingChars(string value) =>
            value.AsSpan().IndexOfAny('\t', '\n', '\r') >= 0;
    }
}
