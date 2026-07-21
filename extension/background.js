// MonitorBot Harvester — background service worker
// Listens for bearer tokens from content script and harvests cookies on demand

const MONITORBOT_PORT = 52384;
const SUPPORTED_DOMAINS = ['target.com', 'walmart.com'];

let harvesting = false;
let capturedBearer = null;
let harvestCount = 0;
let cookieOrder = 'newest';
let dataExpiryMinutes = 3;
let harvestsPerLoad = 1;

// Restore persisted state on service worker startup
chrome.storage.local.get(['harvesting', 'cookieOrder', 'dataExpiryMinutes', 'harvestsPerLoad', 'capturedBearer', 'harvestCount'], (s) => {
  harvesting         = s.harvesting         ?? false;
  cookieOrder        = s.cookieOrder        ?? 'newest';
  dataExpiryMinutes  = s.dataExpiryMinutes  ?? 3;
  harvestsPerLoad    = s.harvestsPerLoad    ?? 1;
  capturedBearer     = s.capturedBearer     ?? null;
  harvestCount       = s.harvestCount       ?? 0;
  updateBadge();
});

// ?? Receive bearer token from content script ????????????????????????????????
chrome.runtime.onMessage.addListener((msg, sender) => {
  if (msg.type === 'BEARER_CAPTURED' && msg.token) {
    capturedBearer = msg.token;
    chrome.storage.local.set({ capturedBearer: msg.token });
    updateBadge();
  }
  if (msg.type === 'GET_STATE') {
    return true; // will respond async
  }
});

// ?? Tab update: auto-harvest when navigating to a supported site ????????????
chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
  if (!harvesting) return;
  if (changeInfo.status !== 'complete') return;
  if (!tab.url) return;

  const host = new URL(tab.url).hostname;
  if (!SUPPORTED_DOMAINS.some(d => host.includes(d))) return;

  // Wait longer on checkout pages so gsp.target.com has time to set target_access_token
  const isCheckout = tab.url.includes('/checkout');
  await sleep(isCheckout ? 5000 : 2000);
  await doHarvest(tab);
});

async function doHarvest(tab) {
  try {
    const host = new URL(tab.url).hostname;
    const domain = SUPPORTED_DOMAINS.find(d => host.includes(d));
    if (!domain) return;

    // For Target checkout pages, wait until target_access_token is present
    if (domain === 'target.com' && tab.url.includes('/checkout')) {
      let found = false;
      for (let i = 0; i < 10; i++) {
        const check = await chrome.cookies.getAll({ url: 'https://gsp.target.com', name: 'target_access_token' });
        if (check.length === 0) {
          // Also check www.target.com
          const check2 = await chrome.cookies.getAll({ url: 'https://www.target.com', name: 'target_access_token' });
          if (check2.length > 0) { found = true; break; }
        } else { found = true; break; }
        await sleep(1000);
      }
      if (!found) {
        console.warn('MonitorBot: target_access_token not found after 10s wait');
      }
    }

    // Collect cookies for all relevant subdomains
    const subdomains = [
      `https://${host}`,
      `https://www.${domain}`,
      `https://api.${domain}`,
      `https://carts.${domain}`,
      `https://gsp.${domain}`,
      `https://account.${domain}`
    ];

    const seen = new Map();
    for (const url of subdomains) {
      try {
        const cookies = await chrome.cookies.getAll({ url });
        for (const c of cookies) {
          const key = `${c.name}|${c.domain}`;
          if (!seen.has(key)) seen.set(key, c);
        }
      } catch (e) {}
    }

    if (seen.size === 0) return;

    // Sort: newest first = sort by expirationDate desc
    let cookieList = [...seen.values()];
    if (cookieOrder === 'newest') {
      cookieList.sort((a, b) => (b.expirationDate || 0) - (a.expirationDate || 0));
    }

    const cookieStr = cookieList.map(c => `${c.name}=${c.value}`).join('; ');

    // Read captured bearer
    const stored = await chrome.storage.local.get(['capturedBearer']);
    const bearer = stored.capturedBearer || capturedBearer || null;

    // Send to MonitorBot WPF app
    const payload = {
      site: domain,
      cookies: cookieStr,
      bearer: bearer,
      cookieCount: cookieList.length,
      timestamp: Date.now()
    };

    await sendToMonitorBot(payload);
    harvestCount++;
    chrome.storage.local.set({ harvestCount });
    updateBadge();
  } catch (e) {
    console.error('MonitorBot harvest error:', e);
  }
}

async function sendToMonitorBot(payload) {
  try {
    await fetch(`http://localhost:${MONITORBOT_PORT}/harvest`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
  } catch (e) {
    // MonitorBot may not be running — store locally for popup to display
    const stored = await chrome.storage.local.get(['pendingHarvests']) || [];
    const pending = stored.pendingHarvests || [];
    pending.unshift({ ...payload, ts: new Date().toISOString() });
    // Keep only last 10
    await chrome.storage.local.set({ pendingHarvests: pending.slice(0, 10) });
  }
}

function updateBadge() {
  chrome.action.setBadgeText({ text: harvesting ? (harvestCount > 0 ? String(harvestCount) : 'ON') : '' });
  chrome.action.setBadgeBackgroundColor({ color: harvesting ? '#22C55E' : '#6B7280' });
}

function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }

// ?? Popup message handlers ??????????????????????????????????????????????????
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.type === 'GET_STATE') {
    sendResponse({
      harvesting,
      harvestCount,
      cookieOrder,
      dataExpiryMinutes,
      harvestsPerLoad,
      capturedBearer: !!capturedBearer
    });
    return true;
  }
  if (msg.type === 'SET_HARVESTING') {
    harvesting = msg.value;
    if (!harvesting) harvestCount = 0;
    chrome.storage.local.set({ harvesting, harvestCount });
    updateBadge();
    sendResponse({ ok: true });
    return true;
  }
  if (msg.type === 'SET_COOKIE_ORDER') {
    cookieOrder = msg.value;
    chrome.storage.local.set({ cookieOrder });
    sendResponse({ ok: true });
    return true;
  }
  if (msg.type === 'SET_EXPIRY') {
    dataExpiryMinutes = msg.value;
    chrome.storage.local.set({ dataExpiryMinutes });
    sendResponse({ ok: true });
    return true;
  }
  if (msg.type === 'SET_HARVESTS_PER_LOAD') {
    harvestsPerLoad = msg.value;
    chrome.storage.local.set({ harvestsPerLoad });
    sendResponse({ ok: true });
    return true;
  }
  if (msg.type === 'CLEAR_DATA') {
    capturedBearer = null;
    harvestCount = 0;
    chrome.storage.local.clear();
    updateBadge();
    sendResponse({ ok: true });
    return true;
  }
  if (msg.type === 'HARVEST_NOW') {
    chrome.tabs.query({ active: true, currentWindow: true }, async (tabs) => {
      if (tabs[0]) await doHarvest(tabs[0]);
      sendResponse({ ok: true });
    });
    return true;
  }
});
