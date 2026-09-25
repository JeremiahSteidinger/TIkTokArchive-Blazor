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

            // Blazor Server keeps a single DI scope per circuit, so a plain scoped DbContext is
            // shared across every operation on a page. Overlapping async work (e.g. the videos
            // page's status-poll timer firing while a button-click runs an ExecuteUpdate) would
            // then hit the same context concurrently and throw "a second operation was started on
            // this context". Register a factory so hot paths can take a fresh context per call,
            // and resolve the scoped DbContext from that same factory so existing scoped consumers
            // (controllers, background-worker scopes, startup migration) keep working unchanged.
            builder.Services.AddDbContextFactory<TikTokArchiveDbContext>(options =>
                options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
                    mySqlOptions => mySqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorNumbersToAdd: null)));
            builder.Services.AddScoped<TikTokArchiveDbContext>(sp =>
                sp.GetRequiredService<IDbContextFactory<TikTokArchiveDbContext>>().CreateDbContext());

            builder.Services.Configure<MediaStorageOptions>(
                builder.Configuration.GetSection(MediaStorageOptions.SectionName));
            builder.Services.Configure<YtDlpOptions>(
                builder.Configuration.GetSection(YtDlpOptions.SectionName));

            builder.Services.AddScoped<IVideoService, VideoService>();

            // Live, in-memory view of what each background worker is doing right now, surfaced
            // on the /monitor page. Workers report their activity to this shared instance.
            builder.Services.AddSingleton<BackgroundTaskMonitor>();

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

            // AI enrichment: a local LLM (Ollama) or Google Gemini — chosen at runtime via the
            // admin-editable AiProviderSettings row — generates a short summary and AI tags from
            // the transcript + description once transcription completes, then re-indexes through
            // the search outbox. Same shape as speech-to-text: the signal and management service
            // are always registered (so ingest's Notify() and the admin UI work when disabled);
            // the LLM clients + worker are gated behind the feature flag.
            builder.Services.Configure<AiEnrichmentOptions>(
                builder.Configuration.GetSection(AiEnrichmentOptions.SectionName));
            builder.Services.AddSingleton<AiEnrichmentSignal>();
            builder.Services.AddScoped<IAiEnrichmentService, AiEnrichmentService>();

            // The Gemini HTTP client + model catalog are always registered (not gated behind
            // AiEnrichment:Enabled) so the admin UI can validate a key and list/pick a model even
            // before the feature is switched on.
            builder.Services.AddHttpClient(GeminiVideoSummaryService.HttpClientName, (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<AiEnrichmentOptions>>().Value;
                client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
                client.Timeout = TimeSpan.FromMinutes(options.TimeoutMinutes);
            });
            builder.Services.AddSingleton<GeminiModelCatalogService>();

            if (builder.Configuration.GetValue<bool>("AiEnrichment:Enabled"))
            {
                builder.Services.AddHttpClient(OllamaVideoSummaryService.HttpClientName, (sp, client) =>
                {
                    var options = sp.GetRequiredService<IOptions<AiEnrichmentOptions>>().Value;
                    client.BaseAddress = new Uri(options.Url.TrimEnd('/') + "/");
                    // CPU inference of a small model takes seconds to tens of seconds.
                    client.Timeout = TimeSpan.FromMinutes(options.TimeoutMinutes);
                });
                // Both providers are registered as themselves (scoped, since Gemini reads its API
                // key from the scoped DbContext) and the router picks the active one from the
                // DB-backed AiProviderSettings row on every call.
                builder.Services.AddScoped<OllamaVideoSummaryService>();
                builder.Services.AddScoped<GeminiVideoSummaryService>();
                builder.Services.AddScoped<IVideoSummaryService, AiSummaryProviderRouter>();
                builder.Services.AddHostedService<AiEnrichmentBackgroundService>();
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

            // Declare the background workers so the monitor page lists them in pipeline order from
            // the start — including ones gated off by a feature flag, which show as Disabled. The
            // enabled flags must match the AddHostedService conditions above.
            var monitor = app.Services.GetRequiredService<BackgroundTaskMonitor>();
            monitor.Register("ingest", "Video Download", "Downloads TikTok and Instagram videos via yt-dlp.", enabled: true);
            monitor.Register("local-import", "Local Import", "Imports video files dropped into the watch folder.", enabled: true);
            monitor.Register("transcription", "Transcription", "Transcribes video audio to text via Whisper.",
                enabled: builder.Configuration.GetValue<bool>("SpeechToText:Enabled"));
            monitor.Register("ai-enrichment", "AI Enrichment", "Generates summaries and tags via a local LLM or Gemini.",
                enabled: builder.Configuration.GetValue<bool>("AiEnrichment:Enabled"));
            monitor.Register("search-index", "Search Indexer", "Applies queued OpenSearch index operations.", enabled: true);
            monitor.Register("search-sync", "Search Sync", "Reconciles the database with the search index.", enabled: true);

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
