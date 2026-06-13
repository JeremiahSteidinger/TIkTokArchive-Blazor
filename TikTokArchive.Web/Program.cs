using Microsoft.EntityFrameworkCore;
using OpenSearch.Client;
using TikTokArchive.Entities;
using TikTokArchive.Web.Components;
using MudBlazor.Services;
using TikTokArchive.Web.HealthChecks;
using TikTokArchive.Web.Options;
using TikTokArchive.Web.Services;

namespace TikTokArchive.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Configure MySQL connection
            var connectionString = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING");

            if(string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("MYSQL_CONNECTION_STRING environment variable is not set.");
            }

            // Add connection timeout and pooling settings if not already in connection string
            if (!connectionString.Contains("Connection Timeout", StringComparison.OrdinalIgnoreCase))
            {
                connectionString += ";Connection Timeout=30;";
            }
            if (!connectionString.Contains("Command Timeout", StringComparison.OrdinalIgnoreCase))
            {
                connectionString += ";Command Timeout=60;";
            }
            if (!connectionString.Contains("Keepalive", StringComparison.OrdinalIgnoreCase))
            {
                connectionString += ";Keepalive=30;";
            }

            builder.Services.AddDbContext<TikTokArchiveDbContext>(options =>
                options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
                    mySqlOptions => mySqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorNumbersToAdd: null)));

            builder.Services.Configure<MediaStorageOptions>(
                builder.Configuration.GetSection(MediaStorageOptions.SectionName));
            builder.Services.Configure<YtDlpOptions>(
                builder.Configuration.GetSection(YtDlpOptions.SectionName));

            builder.Services.AddScoped<IVideoService, VideoService>();

            // Search: OpenSearch client + service, and the outbox worker that applies
            // queued SearchIndexOperations rows to the index.
            builder.Services.AddSingleton<IOpenSearchClient>(sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("OpenSearch");
                var openSearchUrl = builder.Configuration.GetValue<string>("OpenSearch:Url") ?? "http://opensearch:9200";
                var settings = new ConnectionSettings(new Uri(openSearchUrl))
                    .DefaultIndex(OpenSearchService.IndexName)
                    .DisableDirectStreaming()
                    .OnRequestCompleted(details =>
                    {
                        if (!details.Success)
                        {
                            logger.LogError("OpenSearch request to {Method} {Uri} failed: {DebugInformation}",
                                details.HttpMethod, details.Uri, details.DebugInformation);
                        }
                    });
                return new OpenSearchClient(settings);
            });
            builder.Services.AddSingleton<ISearchService, OpenSearchService>();
            builder.Services.AddSingleton<SearchIndexSignal>();
            builder.Services.AddSingleton<ReindexCoordinator>();
            builder.Services.AddHostedService<SearchIndexBackgroundService>();
            builder.Services.AddHostedService<SearchSyncBackgroundService>();

            // Video ingestion: downloads run in the background, off the UI circuit.
            builder.Services.AddSingleton<VideoIngestQueue>();
            builder.Services.AddSingleton<IYtDlpService, YtDlpService>();
            builder.Services.AddHostedService<VideoIngestBackgroundService>();

            builder.Services.AddControllers();
            builder.Services.AddHttpClient();

            builder.Services.AddHealthChecks()
                .AddCheck<DatabaseHealthCheck>("mysql")
                .AddCheck<OpenSearchHealthCheck>("opensearch");

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.AddMudServices();

            var app = builder.Build();

            using (var scope = app.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();
                dbContext.Database.Migrate();
            }

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            //app.UseHttpsRedirection();

            app.UseAntiforgery();

            // Map controllers first before static files
            app.MapControllers();
            app.MapHealthChecks("/health");

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
