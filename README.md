# TikTok Archive (Blazor)

A self-hosted TikTok video archive. Paste a TikTok URL and the app downloads the video and thumbnail with `yt-dlp`, extracts metadata and hashtags, and stores everything in MySQL with media on disk. Videos are browsable, streamable, taggable, and searchable through a Blazor Server UI styled with Tailwind CSS, with full-text search backed by OpenSearch — including the **spoken content** of each video, transcribed by a self-hosted Whisper service. Runs in Docker; not internet-facing, so there is no authentication.

**Stack:** .NET 10 / Blazor Server · Tailwind CSS v4 · EF Core (Pomelo MySQL) · OpenSearch 2.x · yt-dlp · Whisper (speech-to-text) · Docker Compose

---

## Architecture

### Search indexing: transactional outbox

MySQL is the source of truth; the OpenSearch index is a disposable projection that can always be rebuilt from the database. The two are kept in sync through a transactional outbox:

- Any change that affects the index (video added, video deleted) writes a row to the `SearchIndexOperations` table **in the same `SaveChanges` as the data change**, so the operation can never be lost or observed without its data.
- `SearchIndexBackgroundService` is the outbox worker: it polls the table (woken early by `SearchIndexSignal` when new work arrives), applies each operation to OpenSearch, and deletes the row on success. Failures are retried in place with capped exponential backoff (30s doubling up to 1h) — rows are never duplicated and never permanently abandoned, so an OpenSearch outage heals itself once the cluster returns.
- `SearchSyncBackgroundService` is a periodic reconciliation sweep (interval configurable on the Admin page). It diffs database IDs against index IDs and enqueues outbox rows for the difference, skipping videos that already have a pending row. Its main jobs are populating a fresh index and recovering from a wiped OpenSearch volume.

### Search service

`OpenSearchService` is a thin search client: it maps `Video` → `VideoDocument` (one mapper for single and bulk indexing), executes queries, and returns matched video IDs — entity hydration happens in `VideoService` against the database. Queries use `match` against ngram-analyzed fields (description, creator name/username, tags, transcript) with relevance-first sorting, date as tiebreak.

The index (`tiktok_videos_v3`) is created lazily with the correct mapping before any operation, so a slow-starting OpenSearch can never cause an auto-created index with wrong dynamic mappings. The Admin page offers a bulk reindex, coordinated by `ReindexCoordinator` (one job at a time, observable progress).

### Speech-to-text transcription

Video audio is transcribed to text and indexed so users can search what's actually *spoken* in a video, not just the caption. A separate `whisper-asr` container (`onerahmet/openai-whisper-asr-webservice`, faster-whisper engine, CPU) does the work; the app POSTs the raw video file and the service extracts the audio itself (it bundles ffmpeg), so the app needs no media tooling.

Transcription is **decoupled from ingest** so a slow transcription never blocks downloads and videos are searchable by description immediately. The `Video` row carries its own `TranscriptStatus` (Pending → Completed/Skipped/Failed, plus `NotRequested` for the back-catalog); `TranscriptionBackgroundService` is the worker, mirroring the search outbox worker — it claims `Pending`/`Failed` rows, transcribes them, and on success writes the transcript **and** a `SearchIndexOperation` row in one `SaveChanges`, so the existing outbox re-indexes the video with its transcript. Failures retry under the same capped exponential backoff. The whole feature is gated behind `SpeechToText:Enabled`.

**Confidence.** The client asks the engine for JSON output and derives a 0–1 confidence from the per-segment `avg_logprob` (duration-weighted, `exp()`-normalised). Transcripts below `SpeechToText:LowConfidenceThreshold` (default 0.5) — typically laughter, music, or background noise the model transcribes poorly — are logged as a warning and flagged in the UI (amber indicator on the card and in the Admin table, plus a dedicated **Low confidence** filter). Stored per-video in `TranscriptConfidence`.

**Deleting a transcript.** A bad transcript can be deleted from the transcript dialog (Videos page) or the Admin table: it clears the text/confidence, marks the video `Skipped` (so a backfill won't re-transcribe it), and re-indexes so it drops out of search.

**Managing transcription.** The Admin page has a **Transcription** tab showing status counts, a filterable/paged queue of videos (status, confidence, retry count, error), bulk **Retry failed** / **Transcribe back-catalog** actions, and per-row re-queue / delete. Each card on the Videos page also shows a transcription control: a 🎙️ button to queue, a clock while pending, a retry on failure, and a transcript icon once done (amber if low confidence; click to read or delete the transcript). All of it routes through `ITranscriptionService` (queue / delete / retry / backfill / counts), which is also exposed over the Admin API (`GET /api/admin/transcription/status`, `POST /api/admin/transcription/queue/{videoId}`, `.../{videoId}/delete`, `.../retry-failed`, `.../backfill`).

### Video ingestion pipeline

Submitting a URL enqueues a job in `VideoIngestQueue` and returns immediately; `VideoIngestBackgroundService` processes jobs off the UI thread: fetch metadata (`YtDlpService`, async process execution with timeouts — 2 min metadata, 15 min download) → download video → download thumbnail → persist video, creator, tags, and the search outbox row in a single atomic `SaveChanges`. A failure at any step leaves no partial database state, and downloaded files are cleaned up. The Videos page shows live job status; failed downloads stay visible with the yt-dlp error message until dismissed (or for 15 minutes).

Age-restricted/login-gated posts need TikTok cookies. The easiest path is the **Firefox companion extension** in [`firefox-extension/`](firefox-extension/README.md): one click reads your logged-in TikTok cookies and sends them to `POST /api/cookies`, which stores them in Netscape format at `YtDlp:CookiesFile` (default `/app/data/cookies.txt`) for yt-dlp to use. The extension also has an "Archive this video" button for the current tab. Alternatively, mount a manually exported `cookies.txt` and point `YtDlp:CookiesFile` at it. Note the default path lives inside the container, so synced cookies don't survive an image rebuild — re-syncing is one click.

Pending jobs are in-memory and do not survive a restart — acceptable for a self-hosted app where the submitter sees the failure and can resubmit.

The same pipeline backs the API: `POST /api/video?videoUrl=…` waits (up to 60 s) for the download and returns the real outcome — `200` archived, `422` failed with the reason, `202` still downloading — so simple callers like an iOS Shortcut get genuine success/failure feedback. Pass `wait=false` for fire-and-forget; either way the response includes a `jobId` that can be polled at `GET /api/video/ingest/{jobId}`.

### Configuration and operations

- Media paths are bound from the `MediaStorage` configuration section (`MediaStorageOptions`), defaulting to `/media/videos` and `/media/thumbnails`.
- `/health` reports MySQL and OpenSearch connectivity. In `docker-compose.yml`, the app waits for OpenSearch's healthcheck (`depends_on: condition: service_healthy`) before starting.
- EF Core migrations run automatically at startup.

## Running

```bash
docker compose up -d
```

Requires the `MYSQL_CONNECTION_STRING` environment variable (see `docker-compose.override.yml` for the development setup). The app listens on `${APP_PORT:-8080}`.

### Styling (Tailwind CSS)

The UI is styled with **Tailwind CSS v4** via the **standalone CLI** — no Node/npm required. An MSBuild target in `TikTokArchive.Web.csproj` downloads the platform-specific CLI binary to `TikTokArchive.Web/.tailwind/` on first build and compiles `Styles/app.css` → `wwwroot/app.css` (minified) before the static-asset pipeline runs. Both `.tailwind/` and the generated `wwwroot/app.css` are git-ignored; a normal `dotnet build` / `dotnet publish` (including the Docker build) regenerates the CSS.

For a fast edit loop during development, run the CLI in watch mode in a second terminal:

```bash
# from TikTokArchive.Web/
.tailwind/tailwindcss --input Styles/app.css --output wwwroot/app.css --watch
```

Shared UI primitives (`.btn`, `.card`, `.input`, `.chip`, `.badge`, `.spinner`, …) are defined once in `Styles/app.css`; reusable Razor components live in `Components/Components/` (`Icon`, `ToastHost`, `ThemeToggle`, `ImportErrorDialog`). Toast notifications are provided by `ToastService` (replacing the previous MudBlazor `ISnackbar`).

**Light/dark theme:** neutrals are driven by semantic CSS-variable tokens (`bg-page`, `bg-surface`, `text-ink`, `text-muted`, `border-line`, …) defined for `:root` and `.dark` in `Styles/app.css`. `ThemeToggle` flips the `dark` class on `<html>` and persists the choice to `localStorage`; an inline script in `App.razor` applies the saved (or OS-preferred) theme before first paint to avoid a flash.

---

## Roadmap / remaining improvements

1. **Speech-to-text search — enhancements.** Core transcription is implemented (see *Speech-to-text transcription* above): a self-hosted Whisper container transcribes audio, transcripts are stored in MySQL and indexed for full-text search. Remaining: store **segment timestamps** (currently a flat transcript), add **search highlighting**, and **deep-link** results to the spoken moment in the player.
2. **Tests.** There is no test project yet. The seams now exist (`IYtDlpService`, `TagParser`, `ISearchService`, the outbox worker) — start with unit tests for tag parsing and outbox retry behavior.
3. **DTOs / view models.** EF entities are rendered directly in Razor components and serialized in API responses; introduce DTOs to decouple the schema from the UI.
4. **`MediaFileValidationMiddleware` is dead code** — it is never registered in `Program.cs` and no static-file middleware serves `/media`. Wire it up or remove it.
5. **Consistent API error shape.** Controllers return raw exception messages; fine for a trusted LAN, but a consistent error contract would improve UI error handling.
