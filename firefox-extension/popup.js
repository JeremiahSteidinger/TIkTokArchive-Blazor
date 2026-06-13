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
    const cookies = await browser.cookies.getAll({ domain: "tiktok.com" });
    if (cookies.length === 0) {
      setStatus("No TikTok cookies found — log in at tiktok.com first", "error");
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
    setStatus(`Synced ${data.count} cookies`, "ok");
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

  // The archive button only makes sense on a TikTok video page.
  try {
    const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
    const hostname = new URL(tab.url).hostname;
    const onTikTok = hostname === "tiktok.com" || hostname.endsWith(".tiktok.com");
    archiveBtn.disabled = !onTikTok;
    if (!onTikTok) {
      archiveBtn.title = "Open a TikTok video to archive it";
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
