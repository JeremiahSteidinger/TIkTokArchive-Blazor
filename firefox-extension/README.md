# TikTok Archive Companion (Firefox)

A small Firefox extension for your self-hosted TikTok Archive, in the spirit of the TubeArchivist companion:

- **Sync TikTok + Instagram cookies** — reads your logged-in TikTok and Instagram session cookies from Firefox and sends them to the archive (`POST /api/cookies`). The app stores them as a Netscape `cookies.txt` and passes them to yt-dlp, which makes age-restricted / "log in for access" posts downloadable. Instagram requires cookies for most downloads, including metadata.
- **Archive this video** — queues the TikTok video or Instagram Reel in the current tab for download (`POST /api/video`).

## Install

1. Open `about:debugging#/runtime/this-firefox` in Firefox.
2. Click **Load Temporary Add-on…** and select `manifest.json` from this folder.

Temporary add-ons are removed when Firefox restarts. For a permanent install, zip this folder's contents and either submit it to addons.mozilla.org as an unlisted add-on for signing, or use Firefox Developer Edition/ESR with `xpinstall.signatures.required` set to `false` in `about:config`.

## Usage

1. Click the extension icon and enter your archive's URL (e.g. `http://192.168.1.10:8080`), then **Save**. Firefox will ask to grant the extension access to that host — accept, it's how the popup is allowed to call your API.
2. Log in to [tiktok.com](https://www.tiktok.com) and/or [instagram.com](https://www.instagram.com) in Firefox if you aren't already.
3. Click **Sync TikTok + Instagram cookies**. The popup shows when cookies were last synced. Syncing keeps the other platform's cookies on the server, so you can sync each from different sessions.
4. On any TikTok video page, or an Instagram Reel/post page (`/reel/`, `/reels/`, `/p/`), click **Archive this video** to queue it.

Cookies expire periodically — if gated videos start failing again with a "log in for access" error, just re-sync.

## Permissions

- `cookies` + `*://*.tiktok.com/*` + `*://*.instagram.com/*` — read TikTok and Instagram cookies (only those; the app additionally filters to those domains server-side).
- `activeTab` — read the current tab's URL for the archive button.
- `storage` — remember your archive URL.
- Optional host permission for your archive's address — requested when you save the URL, so the popup can call the API.
