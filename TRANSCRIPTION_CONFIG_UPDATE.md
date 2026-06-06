# Transcription Configuration Update

## Overview
This update adds the ability to configure Speech-to-Text providers through the admin interface with runtime changes, rather than requiring environment variables and application restart.

## Changes Made

### 1. Database Schema
**File:** `TikTokArchive.Entities/SearchIndexConfiguration.cs`
- Added STT configuration fields:
  - `SttProvider` (string, default "None")
  - `WhisperUrl` (string, nullable)
  - `WhisperModel` (string, nullable)
  - `OpenAiApiKey` (string, nullable)
  - `AzureSpeechKey` (string, nullable)
  - `AzureSpeechRegion` (string, nullable)

**Migration:** `AddSttConfigurationFields`
- Adds new columns to `SearchIndexConfigurations` table
- Run migration with: `dotnet ef database update --startup-project TikTokArchive.Web`

### 2. Provider Factory Updates
**File:** `TikTokArchive.Web/Services/SpeechToTextProviderFactory.cs`
- Now reads configuration from database first, falls back to environment variables
- Detects configuration changes and reinitializes providers automatically
- Creates providers with database-backed configuration using temporary IConfiguration objects
- Changed `IsEnabled()` to `IsEnabledAsync()` for database access
- Changed `GetConfiguredProviderType()` to `GetConfiguredProviderTypeAsync()`

### 3. Admin API Endpoints
**File:** `TikTokArchive.Web/Controllers/AdminController.cs`

#### New Endpoints:
- `GET /api/admin/transcription/config` - Get current STT configuration
  - Returns provider type and credentials (API keys are masked as "***")
  
- `POST /api/admin/transcription/config` - Update STT configuration
  - Accepts all STT settings
  - Only updates API keys if not masked ("***")
  - Configuration takes effect immediately on next transcription

#### Updated Endpoints:
- All transcription endpoints now use async methods for provider checks

### 4. Admin UI Updates
**File:** `TikTokArchive.Web/Components/Pages/Admin.razor`

#### New Configuration Section:
- **Provider Dropdown**: Select None, WhisperLocal, OpenAI, or Azure
- **Conditional Input Fields**: Show relevant fields based on selected provider
  - WhisperLocal: URL and Model
  - OpenAI: API Key
  - Azure: Speech Key and Region
- **Save Button**: Persist configuration to database
- **API Key Masking**: Existing keys shown as "***" to prevent exposure

#### Layout Changes:
- Configuration moved to 8-column width section
- Statistics moved to 6-column width
- Removed redundant status section

### 5. Service Updates
Updated all services to use async configuration methods:
- `VideoService.cs` - Video transcription queueing
- `TranscriptionBackgroundService.cs` - Background processing startup
- `AdminController.cs` - Status and batch endpoints

## Configuration Priority
1. **Database Configuration** (highest priority)
2. **Environment Variables** (fallback)
3. **Default Values** (None/disabled)

## Usage

### Via Admin UI (Recommended)
1. Navigate to `/admin`
2. Click "Transcription" tab
3. Select desired STT provider from dropdown
4. Enter required credentials for that provider
5. Click "Save Transcription Configuration"
6. Configuration takes effect immediately

### Via Environment Variables (Legacy)
Still supported for initial setup or container configuration:
```bash
STT_PROVIDER=WhisperLocal
WHISPER_URL=http://whisper:9000
WHISPER_MODEL=base
```

### Via Database (Advanced)
```sql
UPDATE SearchIndexConfigurations 
SET SttProvider = 'OpenAI', 
    OpenAiApiKey = 'sk-...',
    LastModified = UTC_TIMESTAMP();
```

## API Key Security
- API keys stored in database are plain text (consider encryption in production)
- Keys masked in API responses to prevent exposure in logs/network
- Use "***" placeholder to keep existing key when updating other settings
- Environment variable fallback still available if database is empty

## Migration Path
1. Run `dotnet ef database update` to apply schema changes
2. Existing environment variables continue to work
3. Configure via UI to store in database
4. Database configuration overrides environment variables

## Testing Configuration
1. Save a configuration via admin UI
2. Check "Current Status" section shows correct provider
3. Queue a test video for transcription
4. Verify transcription uses new provider
5. Change provider and verify immediate effect on next transcription

## Backwards Compatibility
- ✅ Environment variables still work if database is empty
- ✅ Existing transcriptions are not affected
- ✅ No changes to transcription message format or queue structure
- ✅ All existing endpoints remain functional

## Future Enhancements
- Encrypt API keys in database
- Test configuration button (verify provider connectivity)
- Configuration history/audit log
- Per-video provider selection
- Batch change provider for pending items
