using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Dispatches to the active provider's <see cref="IVideoSummaryService"/>, based on the
    /// DB-backed <see cref="AiProviderSettings"/> row. This is the registered
    /// <see cref="IVideoSummaryService"/>, so an admin switching Local/Gemini from the UI takes
    /// effect on the worker's next poll — no restart needed, unlike the appsettings-bound options.
    /// </summary>
    public class AiSummaryProviderRouter : IVideoSummaryService
    {
        private readonly TikTokArchiveDbContext _dbContext;
        private readonly OllamaVideoSummaryService _localService;
        private readonly GeminiVideoSummaryService _geminiService;

        public AiSummaryProviderRouter(
            TikTokArchiveDbContext dbContext,
            OllamaVideoSummaryService localService,
            GeminiVideoSummaryService geminiService)
        {
            _dbContext = dbContext;
            _localService = localService;
            _geminiService = geminiService;
        }

        public async Task<SummaryResult> SummarizeAsync(string description, string? transcript, CancellationToken ct = default)
        {
            var provider = await _dbContext.AiProviderSettings
                .AsNoTracking()
                .Select(s => s.Provider)
                .FirstOrDefaultAsync(ct);

            IVideoSummaryService service = provider == AiProvider.Gemini ? _geminiService : _localService;
            return await service.SummarizeAsync(description, transcript, ct);
        }
    }
}
