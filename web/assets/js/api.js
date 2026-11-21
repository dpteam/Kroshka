/* =============================================================================
   api.js — the only place that talks to the server.

   Cookie auth is used for the same-origin app; the bearer token is kept in memory
   and mirrored to localStorage so a reload does not need a fresh login on a LAN
   where cookies may be stripped by a proxy.
   ============================================================================= */

const Api = (() => {
  const TOKEN_KEY = 'kroshka.token';
  const BASE_KEY = 'kroshka.server';

  let token = null;
  let base = '';

  function load() {
    try {
      token = localStorage.getItem(TOKEN_KEY) || null;
      base = localStorage.getItem(BASE_KEY) || '';
    } catch {
      token = null;
      base = '';
    }
  }

  function setToken(value) {
    token = value || null;
    try {
      if (token) localStorage.setItem(TOKEN_KEY, token);
      else localStorage.removeItem(TOKEN_KEY);
    } catch { /* private mode: keep it in memory only */ }
  }

  function getToken() {
    return token;
  }

  function setServer(value) {
    base = (value || '').replace(/\/+$/, '');
    try {
      if (base) localStorage.setItem(BASE_KEY, base);
      else localStorage.removeItem(BASE_KEY);
    } catch { /* ignore */ }
  }

  function getServer() {
    return base;
  }

  function url(path) {
    return base + path;
  }

  class ApiError extends Error {
    constructor(status, code, message) {
      super(message || 'Ошибка сети');
      this.status = status;
      this.code = code;
    }
  }

  async function request(method, path, body, options = {}) {
    const headers = {};
    if (token) headers.Authorization = 'Bearer ' + token;

    let payload;
    if (body instanceof FormData) {
      payload = body;
    } else if (body !== undefined && body !== null) {
      headers['Content-Type'] = 'application/json';
      payload = JSON.stringify(body);
    }

    let response;
    try {
      response = await fetch(url(path), {
        method,
        headers,
        body: payload,
        credentials: 'same-origin',
        signal: options.signal
      });
    } catch (err) {
      if (err.name === 'AbortError') throw err;
      throw new ApiError(0, 'network', 'Сервер недоступен');
    }

    if (response.status === 204) return null;

    const type = response.headers.get('content-type') || '';
    const isJson = type.includes('application/json');
    const data = isJson ? await response.json().catch(() => null) : null;

    if (!response.ok) {
      throw new ApiError(response.status, data?.code || 'http', data?.error || ('HTTP ' + response.status));
    }

    return data;
  }

  const get = (path, options) => request('GET', path, null, options);
  const post = (path, body, options) => request('POST', path, body ?? {}, options);
  const patch = (path, body) => request('PATCH', path, body ?? {});
  const del = (path) => request('DELETE', path);

  // ---------------------------------------------------------------- endpoints

  const bootstrap = () => get('/api/bootstrap');

  const login = async (nick, password, inviteCode, remember) => {
    const result = await post('/api/login', {
      nick,
      password,
      inviteCode,
      device: deviceName(),
      remember
    });
    if (result?.token) setToken(result.token);
    return result;
  };

  const logout = async () => {
    try { await post('/api/logout'); } catch { /* already gone */ }
    setToken(null);
  };

  const me = () => get('/api/me');
  const sync = () => get('/api/sync');
  const users = () => get('/api/users');

  const page = (conversationId, beforeSeq = 0, limit = 50) =>
    get(`/api/messages?conversationId=${encodeURIComponent(conversationId)}&beforeSeq=${beforeSeq}&limit=${limit}`);

  const send = (conversationId, body) => post(`/api/messages/${encodeURIComponent(conversationId)}`, body);
  const edit = (id, text) => patch(`/api/messages/${encodeURIComponent(id)}`, { text });
  const remove = (id) => del(`/api/messages/${encodeURIComponent(id)}`);
  const react = (id, emoji) => post(`/api/messages/${encodeURIComponent(id)}/reactions`, { emoji });
  const pin = (id) => post(`/api/messages/${encodeURIComponent(id)}/pin`);

  const read = (conversationId, seq) => post('/api/read', { conversationId, seq });

  const createRoom = (body) => post('/api/rooms', body);
  const updateRoom = (id, body) => patch(`/api/rooms/${encodeURIComponent(id)}`, body);
  const deleteRoom = (id) => del(`/api/rooms/${encodeURIComponent(id)}`);
  const openDm = (userId) => post(`/api/dm/${encodeURIComponent(userId)}`);

  const search = (q, conversationId) =>
    get(`/api/search?q=${encodeURIComponent(q)}${conversationId ? '&conversationId=' + encodeURIComponent(conversationId) : ''}`);

  const updateProfile = (body) => patch('/api/me', body);

  const uploadAvatar = (file) => {
    const form = new FormData();
    form.append('file', file);
    return request('POST', '/api/me/avatar', form);
  };

  const sessions = () => get('/api/me/sessions');
  const revokeSession = (id) => del(`/api/me/sessions/${encodeURIComponent(id)}`);

  /**
   * Uploads with progress. fetch() cannot report upload progress, so this uses
   * XHR for the one request that can be large.
   */
  function upload(file, onProgress, duration) {
    return new Promise((resolve, reject) => {
      const form = new FormData();
      form.append('file', file);
      if (duration) form.append('duration', String(duration));

      const xhr = new XMLHttpRequest();
      xhr.open('POST', url('/api/upload'));
      if (token) xhr.setRequestHeader('Authorization', 'Bearer ' + token);

      xhr.upload.onprogress = (event) => {
        if (event.lengthComputable && onProgress) {
          onProgress(event.loaded / event.total);
        }
      };

      xhr.onload = () => {
        let data = null;
        try { data = JSON.parse(xhr.responseText); } catch { /* not json */ }

        if (xhr.status >= 200 && xhr.status < 300 && data) resolve(data);
        else reject(new ApiError(xhr.status, data?.code || 'http', data?.error || 'Не удалось загрузить'));
      };

      xhr.onerror = () => reject(new ApiError(0, 'network', 'Сервер недоступен'));
      xhr.onabort = () => reject(new ApiError(0, 'abort', 'Отменено'));

      xhr.send(form);
    });
  }

  function blobUrl(id) {
    // The token travels in the query string because <img>/<audio> cannot send headers.
    const suffix = token ? '?t=' + encodeURIComponent(token) : '';
    return url('/api/blob/' + encodeURIComponent(id)) + suffix;
  }

  function deviceName() {
    const ua = navigator.userAgent;
    if (/Windows/.test(ua)) return 'Windows';
    if (/Android/.test(ua)) return 'Android';
    if (/iPhone|iPad/.test(ua)) return 'iOS';
    if (/Mac OS/.test(ua)) return 'macOS';
    if (/Linux/.test(ua)) return 'Linux';
    return 'Browser';
  }

  load();

  return {
    ApiError,
    get, post, patch, del,
    bootstrap, login, logout, me, sync, users,
    page, send, edit, remove, react, pin, read,
    createRoom, updateRoom, deleteRoom, openDm,
    search, updateProfile, uploadAvatar, sessions, revokeSession,
    upload, blobUrl,
    getToken, setToken, getServer, setServer, url, deviceName
  };
})();
