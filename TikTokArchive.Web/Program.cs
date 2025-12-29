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

            builder.Services.AddDbContext<TikTokArchiveDbContext>(options =>
                options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

            builder.Services.AddScoped<Services.IVideoService, Services.VideoService>();

            builder.Services.AddControllers();
            builder.Services.AddHttpClient();
            
            // Add response caching for thumbnails
            builder.Services.AddResponseCaching();
            
            // Add response compression for faster transfers
            builder.Services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
            });

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
            
            // Enable response compression (must be early in pipeline)
            app.UseResponseCompression();

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            //app.UseHttpsRedirection();
            
            // Enable response caching for API endpoints
            app.UseResponseCaching();

            // Validate media file access before serving
            // app.UseMiddleware<MediaFileValidationMiddleware>();

            app.UseAntiforgery();
            // Serve media files with caching
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider("/media"),
                RequestPath = "/media",
                OnPrepareResponse = ctx =>
                {
                    // Cache media files for 7 days
                    ctx.Context.Response.Headers.Append("Cache-Control", "public, max-age=604800");
                }
            });

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.MapControllers();

            app.Run();
        }
    }
}
