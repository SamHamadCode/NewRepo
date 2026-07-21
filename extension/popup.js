let state = {};

async function loadState() {
  state = await chrome.runtime.sendMessage({ type: 'GET_STATE' });
  render();
}

function render() {
  // Status dot + text
  const dot = document.getElementById('statusDot');
  const txt = document.getElementById('statusText');
  dot.className = 'dot ' + (state.harvesting ? 'dot-green' : 'dot-gray');
  txt.textContent = state.harvesting ? 'Harvesting Active' : 'Harvesting Off';
  txt.style.color = state.harvesting ? '#22c55e' : '#9ca3af';

  document.getElementById('harvestCount').textContent = state.harvestCount || 0;

  const bearerEl = document.getElementById('bearerStatus');
  bearerEl.textContent = state.capturedBearer ? '? Captured' : '—';
  bearerEl.style.color = state.capturedBearer ? '#22c55e' : '#6b7280';

  document.getElementById('harvestsPerLoad').value = state.harvestsPerLoad || 1;
  document.getElementById('dataExpiry').value = state.dataExpiryMinutes || 3;

  const btnOrder = document.getElementById('btnCookieOrder');
  btnOrder.textContent = state.cookieOrder === 'newest'
    ? 'Current: Use Newest First'
    : 'Current: Use Oldest First';
  btnOrder.className = 'btn btn-blue';

  const btnHarvest = document.getElementById('btnHarvesting');
  btnHarvest.textContent = state.harvesting ? 'Stop Harvesting' : 'Start Harvesting';
  btnHarvest.className = 'btn ' + (state.harvesting ? 'btn-gray' : 'btn-green');
}

document.getElementById('btnClear').addEventListener('click', async () => {
  await chrome.runtime.sendMessage({ type: 'CLEAR_DATA' });
  await loadState();
});

document.getElementById('btnCookieOrder').addEventListener('click', async () => {
  const newOrder = state.cookieOrder === 'newest' ? 'oldest' : 'newest';
  await chrome.runtime.sendMessage({ type: 'SET_COOKIE_ORDER', value: newOrder });
  await loadState();
});

document.getElementById('btnHarvesting').addEventListener('click', async () => {
  await chrome.runtime.sendMessage({ type: 'SET_HARVESTING', value: !state.harvesting });
  await loadState();
});

document.getElementById('btnHarvestNow').addEventListener('click', async () => {
  await chrome.runtime.sendMessage({ type: 'HARVEST_NOW' });
  setTimeout(loadState, 500);
});

document.getElementById('harvestsPerLoad').addEventListener('change', async (e) => {
  await chrome.runtime.sendMessage({ type: 'SET_HARVESTS_PER_LOAD', value: parseInt(e.target.value) });
});

document.getElementById('dataExpiry').addEventListener('change', async (e) => {
  await chrome.runtime.sendMessage({ type: 'SET_EXPIRY', value: parseInt(e.target.value) });
});

loadState();
