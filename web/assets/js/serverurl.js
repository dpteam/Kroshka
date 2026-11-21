/* =============================================================================
   serverurl.js — one answer to "what did the user actually mean by that address".

   People type addresses in every shape imaginable:

       192.168.1.250
       192.168.1.250:6767
       http://192.168.1.250
       http://192.168.1.250:6767
       http://192.168.1.250:6767/
       192.168.1.250 6767
       localhost:6767
       my-server.local

   All of those are the same server. This module normalises them to one canonical
   base URL so the app never stores two spellings of the same place, and so a
   typo produces a clear message instead of a silent connection failure.
   ============================================================================= */

const ServerUrl = (() => {
  const DEFAULT_PORT = 6767;

  /**
   * @param {string} input  whatever was typed or pasted
   * @param {number} [defaultPort] port to assume when none was given
   * @returns {{ok: boolean, url: string, error: string}}
   */
  function normalize(input, defaultPort = DEFAULT_PORT) {
    let value = String(input ?? '').trim();

    if (!value) {
      return { ok: false, url: '', error: 'Введите адрес сервера' };
    }

    // "192.168.1.250 6767" is a common slip; a space where a colon belongs.
    value = value.replace(/\s+(\d{2,5})\s*$/, ':$1');
    // Kill any internal whitespace left over.
    value = value.replace(/\s+/g, '');

    // A bare host needs a scheme, otherwise URL() reads "192.168.1.250" as a
    // path and every later check is nonsense.
    if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(value)) {
      value = 'http://' + value;
    }

    // Only http(s) make sense for this app; anything else is a mistake or a
    // paste of a link from somewhere else.
    if (!/^https?:\/\//i.test(value)) {
      return { ok: false, url: '', error: 'Поддерживается только http:// или https://' };
    }

    let parsed;
    try {
      parsed = new URL(value);
    } catch {
      // URL() also throws on a port outside 1..65535, which deserves its own
      // message rather than a generic "not an address".
      if (/:\d{5,}$/.test(value)) {
        return { ok: false, url: '', error: 'Порт должен быть от 1 до 65535' };
      }
      return { ok: false, url: '', error: 'Не похоже на адрес сервера' };
    }

    if (!parsed.hostname) {
      return { ok: false, url: '', error: 'В адресе нет имени хоста' };
    }

    // A host is letters, digits, dots and hyphens (or a bracketed IPv6 literal).
    // Anything else - spaces, exclamation marks, slashes - means the input was
    // not an address at all, and URL() happily percent-encodes it into nonsense.
    const host = parsed.hostname;
    const looksLikeHost = /^[a-z0-9._-]+$/i.test(host) || /^\[[0-9a-f:.]+\]$/i.test(host);

    if (!looksLikeHost) {
      return { ok: false, url: '', error: 'Неверное имя хоста' };
    }

    // The server always serves from its root, so a pasted "/chat" is dropped
    // rather than silently producing 404s.
    const port = parsed.port || String(defaultPort);
    const numericPort = Number(port);

    if (!Number.isInteger(numericPort) || numericPort < 1 || numericPort > 65535) {
      return { ok: false, url: '', error: 'Порт должен быть от 1 до 65535' };
    }

    return {
      ok: true,
      url: `${parsed.protocol}//${parsed.hostname}:${numericPort}`,
      error: ''
    };
  }

  /** Human-friendly form for display: drops the default scheme and port. */
  function prettify(url) {
    if (!url) return '';
    try {
      const parsed = new URL(url);
      const host = parsed.hostname;
      const port = parsed.port;
      return port && port !== String(DEFAULT_PORT) ? `${host}:${port}` : host;
    } catch {
      return url;
    }
  }

  /** The port a canonical URL carries, or the default. */
  function portOf(url) {
    try {
      const parsed = new URL(url);
      return parsed.port ? Number(parsed.port) : DEFAULT_PORT;
    } catch {
      return DEFAULT_PORT;
    }
  }

  return { normalize, prettify, portOf, DEFAULT_PORT };
})();
