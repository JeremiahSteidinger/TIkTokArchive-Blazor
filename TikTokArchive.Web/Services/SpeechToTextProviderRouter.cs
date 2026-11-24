using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Dispatches to the active transcription engine's <see cref="ISpeechToTextService"/>, based
    /// on the DB-backed <see cref="AiProviderSettings"/> row. This is the registered
    /// <see cref="ISpeechToTextService"/>, so an admin switching engines from the UI takes
    /// effect on the worker's next video — no restart needed. Mirrors
    /// <see cref="AiSummaryProviderRouter"/>.
    /// </summary>
    public class SpeechToTextProviderRouter : ISpeechToTextService
    {
        private readonly TikTokArchiveDbContext _dbContext;
        private readonly WhisperAsrSpeechToTextService _whisperService;
        private readonly GeminiSpeechToTextService _geminiService;
        private readonly GeminiTranscribeSpeechToTextService _geminiTranscribeService;

        public SpeechToTextProviderRouter(
            TikTokArchiveDbContext dbContext,
            WhisperAsrSpeechToTextService whisperService,
            GeminiSpeechToTextService geminiService,
            GeminiTranscribeSpeechToTextService geminiTranscribeService)
        {
            _dbContext = dbContext;
            _whisperService = whisperService;
            _geminiService = geminiService;
            _geminiTranscribeService = geminiTranscribeService;
        }

        public async Task<TranscriptionResult> TranscribeAsync(string videoFilePath, CancellationToken ct = default)
        {
            var engine = await _dbContext.AiProviderSettings
                .AsNoTracking()
                .Select(s => s.TranscriptionProvider)
                .FirstOrDefaultAsync(ct);

            ISpeechToTextService service = engine switch
            {
                TranscriptionEngine.Gemini => _geminiService,
                TranscriptionEngine.GeminiTranscribe => _geminiTranscribeService,
                _ => _whisperService
            };
            return await service.TranscribeAsync(videoFilePath, ct);
        }
    }
}
