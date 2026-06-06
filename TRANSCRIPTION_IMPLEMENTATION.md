# Speech-to-Text Transcription Feature - Implementation Summary

## Overview
Successfully implemented optional speech-to-text transcription for TikTok videos using RabbitMQ queue processing with multiple configurable STT providers (local Whisper, OpenAI, Azure). Videos are transcribed asynchronously with transcripts stored in the database and immediately reindexed for search.

## Features Implemented

### 1. Database Schema
- **VideoTranscript** entity with properties: VideoId, TranscriptText, Language, ProcessedAt, ProcessingTimeSeconds, Provider
- **TranscriptionQueueItem** entity with properties: VideoId, Status (enum), QueuedAt, ProcessedAt, RetryCount, ErrorMessage, Provider
- **TranscriptionStatus** enum: Pending, Processing, Completed, Failed
- One-to-one relationship between Video and VideoTranscript
- Migration created: `AddTranscriptionEntities`

### 2. Infrastructure
- **Docker Services Added:**
  - RabbitMQ (ports 5672, 15672) with management plugin
  - Whisper (onerahmet/openai-whisper-asr-webservice) on port 9000
  - FFmpeg installed in web container via Dockerfile

- **Environment Variables:**
  - `RABBITMQ_CONNECTION_STRING` (default: amqp://guest:guest@rabbitmq:5672)
  - `STT_PROVIDER` (None/WhisperLocal/OpenAI/Azure, default: None)
  - `WHISPER_URL` (default: http://whisper:9000)
  - `WHISPER_MODEL` (tiny/base/small/medium/large, default: base)
  - `OPENAI_API_KEY` (for OpenAI provider)
  - `AZURE_SPEECH_KEY` and `AZURE_SPEECH_REGION` (for Azure provider)

### 3. STT Provider Architecture
- **ISpeechToTextProvider** interface with TranscribeAsync() and IsAvailableAsync()
- **WhisperLocalProvider** - HTTP client to local Whisper container
- **OpenAIWhisperProvider** - OpenAI Whisper API client
- **AzureSpeechProvider** - Placeholder for Azure Speech SDK (not yet implemented)
- **SpeechToTextProviderFactory** - Validates provider availability on startup, logs warnings if unavailable

### 4. Services
- **RabbitMQService** - Singleton for connection/channel management with dead-letter exchange, prefetch=1
- **AudioExtractionService** - Extracts MP3 audio using ffmpeg (quality: -q:a 2)
- **TranscriptionService** - Orchestrates extraction, transcription, cleanup, persistence, and search reindexing
- **TranscriptionBackgroundService** - Consumer with manual acknowledgment, retry logic (max 3), status tracking

### 5. Workflow Integration
- **VideoService.AddVideo()** - Automatically queues transcription if STT enabled after successful video download
- **OpenSearchService** - Extended with TranscriptText field (boost: 1.5), included in search queries and bulk reindex
- **AdminController** - New endpoints:
  - `GET /api/admin/transcription/status` - Queue stats, provider config, recent failures
  - `POST /api/admin/transcription/batch` - Queue all videos without transcripts
  - `POST /api/admin/transcription/retry/{videoId}` - Retry failed transcription

### 6. Reliability Features
- Database-backed queue items for durability across restarts
- Startup re-queuing of pending/failed items
- Dead-letter queue for messages exceeding max retries
- Audio files extracted fresh on each attempt (no disk space issues)
- Temporary MP3 files cleaned up after processing
- Graceful degradation if provider unavailable
- Non-blocking async operations

## Configuration Examples

### Enable Local Whisper (Base Model)
```yaml
environment:
  - STT_PROVIDER=WhisperLocal
  - WHISPER_MODEL=base
```

### Enable OpenAI Whisper
```yaml
environment:
  - STT_PROVIDER=OpenAI
  - OPENAI_API_KEY=your-api-key
```

### Disable Transcription (Default)
```yaml
environment:
  - STT_PROVIDER=None
```

## Admin API Usage

### Check Transcription Status
```bash
curl http://localhost:8080/api/admin/transcription/status
```

### Queue All Videos Without Transcripts
```bash
curl -X POST http://localhost:8080/api/admin/transcription/batch
```

### Retry Failed Transcription
```bash
curl -X POST http://localhost:8080/api/admin/transcription/retry/123
```

## Search Integration
Transcripts are automatically included in search queries when using the "all" or "transcript" field filter. Boost score of 1.5 balances relevance with description (2.0) and creator name (1.5).

## Next Steps
1. Test with actual videos after starting Docker Compose
2. Monitor RabbitMQ management UI at http://localhost:15672
3. Consider implementing Azure Speech provider if needed
4. Adjust Whisper model size based on performance/accuracy needs
5. Add UI components to display transcripts (currently search-only)

## Files Modified/Created
- **Entities:** VideoTranscript.cs, TranscriptionQueueItem.cs, Video.cs, TikTokArchiveDbContext.cs
- **Services:** ISpeechToTextProvider.cs, WhisperLocalProvider.cs, OpenAIWhisperProvider.cs, AzureSpeechProvider.cs, SpeechToTextProviderFactory.cs, RabbitMQService.cs, AudioExtractionService.cs, TranscriptionService.cs, TranscriptionBackgroundService.cs, VideoService.cs, OpenSearchService.cs
- **Controllers:** AdminController.cs
- **Infrastructure:** Dockerfile, docker-compose.yml, Program.cs
- **Migration:** 20251230XXXXXX_AddTranscriptionEntities.cs
