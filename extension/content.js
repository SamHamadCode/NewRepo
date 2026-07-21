// Intercept fetch and XHR to capture Authorization Bearer tokens before the page makes them
(function () {
  if (window.__monitorBotInjected) return;
  window.__monitorBotInjected = true;

  // Patch fetch
  const _fetch = window.fetch;
  window.fetch = function (input, init) {
    try {
      const headers = init && init.headers;
      if (headers) {
        let auth = null;
        if (typeof headers.get === 'function') {
          auth = headers.get('authorization') || headers.get('Authorization');
        } else if (headers['Authorization']) {
          auth = headers['Authorization'];
        } else if (headers['authorization']) {
          auth = headers['authorization'];
        }
        if (auth && auth.startsWith('Bearer ')) {
          const token = auth.substring(7);
          window.__capturedBearer = token;
          chrome.runtime.sendMessage({ type: 'BEARER_CAPTURED', token, url: typeof input === 'string' ? input : input.url });
        }
      }
    } catch (e) {}
    return _fetch.apply(this, arguments);
  };

  // Patch XHR
  const _setHeader = XMLHttpRequest.prototype.setRequestHeader;
  XMLHttpRequest.prototype.setRequestHeader = function (name, value) {
    try {
      if (name && name.toLowerCase() === 'authorization' && value && value.startsWith('Bearer ')) {
        const token = value.substring(7);
        window.__capturedBearer = token;
        chrome.runtime.sendMessage({ type: 'BEARER_CAPTURED', token, url: this._url || '' });
      }
    } catch (e) {}
    return _setHeader.apply(this, arguments);
  };

  const _open = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (method, url) {
    this._url = url;
    return _open.apply(this, arguments);
  };
})();
