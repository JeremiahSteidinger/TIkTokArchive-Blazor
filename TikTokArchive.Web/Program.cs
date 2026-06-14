using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenSearch.Client;
using TikTokArchive.Entities;
using TikTokArchive.Web.Components;
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

            // Local import: watches a drop folder and imports video files found there.
            builder.Services.AddHostedService<LocalImportBackgroundService>();

            // Speech-to-text: transcribes video audio off the ingest path and re-indexes
            // through the existing search outbox. The signal is always registered so the
            // ingest services' Notify() is a harmless no-op when the feature is disabled;
            // only the client + worker are gated behind the feature flag.
            builder.Services.Configure<SpeechToTextOptions>(
                builder.Configuration.GetSection(SpeechToTextOptions.SectionName));
            builder.Services.AddSingleton<TranscriptionSignal>();
            // Always registered so the management UI works even when the worker is disabled
            // (queued videos simply wait until it's enabled).
            builder.Services.AddScoped<ITranscriptionService, TranscriptionService>();

            if (builder.Configuration.GetValue<bool>("SpeechToText:Enabled"))
            {
                builder.Services.AddHttpClient(WhisperAsrSpeechToTextService.HttpClientName, (sp, client) =>
                {
                    var options = sp.GetRequiredService<IOptions<SpeechToTextOptions>>().Value;
                    client.BaseAddress = new Uri(options.Url.TrimEnd('/') + "/");
                    // Transcription takes minutes, not the default 100 seconds.
                    client.Timeout = TimeSpan.FromMinutes(options.TimeoutMinutes);
                });
                builder.Services.AddSingleton<ISpeechToTextService, WhisperAsrSpeechToTextService>();
                builder.Services.AddHostedService<TranscriptionBackgroundService>();
            }

            builder.Services.AddControllers();
            builder.Services.AddHttpClient();

            builder.Services.AddHealthChecks()
                .AddCheck<DatabaseHealthCheck>("mysql")
                .AddCheck<OpenSearchHealthCheck>("opensearch");

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            // Per-circuit toast notifications (replaces MudBlazor's ISnackbar).
            builder.Services.AddScoped<ToastService>();

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
