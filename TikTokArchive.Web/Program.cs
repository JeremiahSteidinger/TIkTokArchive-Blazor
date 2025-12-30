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
