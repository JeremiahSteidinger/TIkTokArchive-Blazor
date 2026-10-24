using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Dispatches to the active transcription engine's <see cref="ISpeechToTextService"/>, based
    /// on the DB-backed <see cref="AiProviderSettings"/> row. This is the registered
    /// <see cref="ISpeechToTextService"/>, so an admin switching Whisper/Gemini from the UI takes
    /// effect on the worker's next video — no restart needed. Mirrors
    /// <see cref="AiSummaryProviderRouter"/>.
    /// </summary>
    public class SpeechToTextProviderRouter : ISpeechToTextService
    {
        private readonly TikTokArchiveDbContext _dbContext;
        private readonly WhisperAsrSpeechToTextService _whisperService;
        private readonly GeminiSpeechToTextService _geminiService;

        public SpeechToTextProviderRouter(
            TikTokArchiveDbContext dbContext,
            WhisperAsrSpeechToTextService whisperService,
            GeminiSpeechToTextService geminiService)
        {
            _dbContext = dbContext;
            _whisperService = whisperService;
            _geminiService = geminiService;
        }

        public async Task<TranscriptionResult> TranscribeAsync(string videoFilePath, CancellationToken ct = default)
        {
            var provider = await _dbContext.AiProviderSettings
                .AsNoTracking()
                .Select(s => s.TranscriptionProvider)
                .FirstOrDefaultAsync(ct);

            ISpeechToTextService service = provider == AiProvider.Gemini ? _geminiService : _whisperService;
            return await service.TranscribeAsync(videoFilePath, ct);
        }
    }
}
