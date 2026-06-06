using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;
using TikTokArchive.Web.Components;
using MudBlazor.Services;
using TikTokArchive.Web.Middleware;

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

            builder.Services.AddScoped<Services.IVideoService, Services.VideoService>();

            // Register search services
            builder.Services.AddSingleton<Services.ISearchService, Services.OpenSearchService>();
            builder.Services.AddSingleton<Services.SearchIndexQueue>();
            builder.Services.AddHostedService<Services.SearchIndexBackgroundService>();
            builder.Services.AddHostedService<Services.SearchSyncBackgroundService>();

            // Register transcription services
            builder.Services.AddSingleton<Services.RabbitMQService>();
            builder.Services.AddSingleton<Services.SpeechToTextProviderFactory>();
            builder.Services.AddSingleton<Services.WhisperLocalProvider>();
            builder.Services.AddSingleton<Services.OpenAIWhisperProvider>();
            builder.Services.AddSingleton<Services.AzureSpeechProvider>();
            builder.Services.AddScoped<Services.AudioExtractionService>();
            builder.Services.AddScoped<Services.TranscriptionService>();
            builder.Services.AddHostedService<Services.TranscriptionBackgroundService>();
            builder.Services.AddHostedService<Services.LocalImportBackgroundService>();

            builder.Services.AddControllers();
            builder.Services.AddHttpClient();

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.AddMudServices();

            var app = builder.Build();

            using (var scope = app.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();
                dbContext.Database.Migrate();

                // Initialize OpenSearch index
                var searchService = app.Services.GetRequiredService<Services.ISearchService>();
                searchService.InitializeAsync().Wait();

                // Initialize RabbitMQ (if configured)
                var rabbitMQService = app.Services.GetService<Services.RabbitMQService>();
                if (rabbitMQService != null)
                {
                    rabbitMQService.InitializeAsync().Wait();
                }

                // Initialize STT provider factory (validates availability)
                var sttFactory = app.Services.GetService<Services.SpeechToTextProviderFactory>();
                if (sttFactory != null)
                {
                    sttFactory.GetProviderAsync().Wait();
                }
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
            
            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
