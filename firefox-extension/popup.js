"use strict";

const appUrlInput = document.getElementById("appUrl");
const saveBtn = document.getElementById("saveBtn");
const archiveBtn = document.getElementById("archiveBtn");
const syncBtn = document.getElementById("syncBtn");
const statusEl = document.getElementById("status");
const cookieInfoEl = document.getElementById("cookieInfo");

function setStatus(text, kind) {
  statusEl.textContent = text;
  statusEl.className = kind || "";
}

function normalizeAppUrl(raw) {
  let url = raw.trim().replace(/\/+$/, "");
  if (url && !/^https?:\/\//i.test(url)) {
    url = "http://" + url;
  }
  new URL(url); // throws on garbage
  return url;
}

// Match patterns can't carry a port, so request the whole host; Firefox then
// allows fetches to any port on it.
function originPattern(appUrl) {
  const u = new URL(appUrl);
  return `${u.protocol}//${u.hostname}/*`;
}

async function getAppUrl() {
  const { appUrl } = await browser.storage.local.get({ appUrl: "" });
  return appUrl;
}

async function saveSettings() {
  let appUrl;
  try {
    appUrl = normalizeAppUrl(appUrlInput.value);
  } catch {
    setStatus("That doesn't look like a valid URL", "error");
    return;
  }

  const granted = await browser.permissions.request({ origins: [originPattern(appUrl)] });
  if (!granted) {
    setStatus("Permission declined — the extension can't reach the app without it", "error");
    return;
  }

  await browser.storage.local.set({ appUrl });
  setStatus("Saved", "ok");
  refreshCookieInfo(appUrl);
}

async function syncCookies() {
  const appUrl = await getAppUrl();
  if (!appUrl) {
    setStatus("Set the archive URL first", "error");
    return;
  }

  syncBtn.disabled = true;
  setStatus("Syncing cookies…");
  try {
    const tiktokCookies = await browser.cookies.getAll({ domain: "tiktok.com" });
    const instagramCookies = await browser.cookies.getAll({ domain: "instagram.com" });
    const cookies = [...tiktokCookies, ...instagramCookies];
    if (cookies.length === 0) {
      setStatus("No TikTok or Instagram cookies found — log in first", "error");
      return;
    }

    const payload = cookies.map((c) => ({
      name: c.name,
      value: c.value,
      domain: c.domain,
      path: c.path,
      secure: c.secure,
      hostOnly: c.hostOnly,
      expirationDate: c.expirationDate ?? null,
    }));

    const resp = await fetch(`${appUrl}/api/cookies`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
    if (!resp.ok) {
      throw new Error(`${resp.status} ${await resp.text()}`);
    }
    const data = await resp.json();
    // Instagram needs a logged-in session (sessionid) for yt-dlp to download anything;
    // anonymous cookies like csrftoken/mid sync fine but don't authenticate. Surface
    // that here, since a bare total made "synced but not logged in" look like success.
    const igLoggedIn = instagramCookies.some((c) => c.name === "sessionid");
    const summary =
      `Synced ${data.count} cookies (${tiktokCookies.length} TikTok, ${instagramCookies.length} Instagram)`;
    if (instagramCookies.length > 0 && !igLoggedIn) {
      setStatus(`${summary} — no Instagram login session; log in at instagram.com and re-sync`, "error");
    } else {
      setStatus(summary, "ok");
    }
    refreshCookieInfo(appUrl);
  } catch (err) {
    setStatus(`Cookie sync failed: ${err.message}`, "error");
  } finally {
    syncBtn.disabled = false;
  }
}

async function archiveCurrentVideo() {
  const appUrl = await getAppUrl();
  if (!appUrl) {
    setStatus("Set the archive URL first", "error");
    return;
  }

  const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
  archiveBtn.disabled = true;
  setStatus("Queueing…");
  try {
    const resp = await fetch(
      `${appUrl}/api/video?videoUrl=${encodeURIComponent(tab.url)}&wait=false`,
      { method: "POST" }
    );
    if (!resp.ok) {
      throw new Error(`${resp.status} ${await resp.text()}`);
    }
    setStatus("Queued — check the archive for progress", "ok");
  } catch (err) {
    setStatus(`Failed to queue: ${err.message}`, "error");
  } finally {
    archiveBtn.disabled = false;
  }
}

async function refreshCookieInfo(appUrl) {
  cookieInfoEl.textContent = "";
  if (!appUrl) return;
  try {
    const resp = await fetch(`${appUrl}/api/cookies/status`);
    if (!resp.ok) return;
    const data = await resp.json();
    cookieInfoEl.textContent = data.exists
      ? `Cookies on server, last synced ${new Date(data.lastModified).toLocaleString()}`
      : "No cookies on server yet";
  } catch {
    cookieInfoEl.textContent = "Archive unreachable";
  }
}

async function init() {
  const appUrl = await getAppUrl();
  appUrlInput.value = appUrl;

  // The archive button only makes sense on a video page. TikTok enables on any page
  // (as before); Instagram only on post-shaped paths so a profile or the home feed —
  // which yt-dlp can't download as a single video — doesn't get queued. The server
  // still validates by host only.
  try {
    const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
    const url = new URL(tab.url);
    const hostname = url.hostname;
    const onTikTok = hostname === "tiktok.com" || hostname.endsWith(".tiktok.com");
    const onInstagram =
      (hostname === "instagram.com" || hostname.endsWith(".instagram.com")) &&
      /^\/(reel|reels|p)\//.test(url.pathname);
    archiveBtn.disabled = !(onTikTok || onInstagram);
    if (archiveBtn.disabled) {
      archiveBtn.title = "Open a TikTok or Instagram video to archive it";
    }
  } catch {
    archiveBtn.disabled = true;
  }

  refreshCookieInfo(appUrl);
}

saveBtn.addEventListener("click", saveSettings);
syncBtn.addEventListener("click", syncCookies);
archiveBtn.addEventListener("click", archiveCurrentVideo);
init();
