using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenSearch.Client;

namespace TikTokArchive.Web.HealthChecks
{
    public class OpenSearchHealthCheck : IHealthCheck
    {
        private readonly IOpenSearchClient _client;

        public OpenSearchHealthCheck(IOpenSearchClient client)
        {
            _client = client;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var response = await _client.PingAsync(ct: cancellationToken);
            return response.IsValid
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded($"OpenSearch is unreachable: {response.DebugInformation}");
        }
    }
}
