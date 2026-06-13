using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Controllers
{
    /// <summary>
    /// Receives TikTok cookies from the Firefox companion extension and stores them in
    /// Netscape cookies.txt format at <see cref="YtDlpOptions.CookiesFile"/>, where
    /// yt-dlp picks them up for age-restricted/login-gated posts.
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
            foreach (var cookie in cookies)
            {
                var domain = cookie.Domain.TrimStart('.');
                var isTikTok = domain.Equals("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
                               domain.EndsWith(".tiktok.com", StringComparison.OrdinalIgnoreCase);
                if (!isTikTok) continue;

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
                return BadRequest("No valid TikTok cookies in the request");
            }

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

            _logger.LogInformation("Stored {Count} TikTok cookies for yt-dlp", count);
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

        private static bool ContainsJarBreakingChars(string value) =>
            value.AsSpan().IndexOfAny('\t', '\n', '\r') >= 0;
    }
}
