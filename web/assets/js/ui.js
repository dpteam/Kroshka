/* =============================================================================
   ui.js — small, dependency-free helpers: DOM shorthand, escaping, dates, avatars,
   link detection, toasts and the emoji picker. Kept apart from app.js so the state
   machine there stays readable.
   ============================================================================= */

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);

  for (const [key, value] of Object.entries(attrs)) {
    if (value === undefined || value === null || value === false) continue;

    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key === 'html') node.innerHTML = value;
    else if (key.startsWith('on') && typeof value === 'function') {
      node.addEventListener(key.slice(2).toLowerCase(), value);
    } else if (key === 'dataset') {
      Object.assign(node.dataset, value);
    } else if (value === true) {
      node.setAttribute(key, '');
    } else {
      node.setAttribute(key, value);
    }
  }

  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }

  return node;
}

function escapeHtml(text) {
  return String(text ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

// ------------------------------------------------------------------- dates

const MONTHS = ['января', 'февраля', 'марта', 'апреля', 'мая', 'июня',
                'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря'];
const WEEKDAYS = ['воскресенье', 'понедельник', 'вторник', 'среда', 'четверг', 'пятница', 'суббота'];

function parseDate(value) {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

function startOfDay(date) {
  const copy = new Date(date);
  copy.setHours(0, 0, 0, 0);
  return copy;
}

function sameDay(a, b) {
  return a && b && startOfDay(a).getTime() === startOfDay(b).getTime();
}

function formatTime(value) {
  const date = parseDate(value);
  if (!date) return '';
  return date.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}

/** Compact stamp for the conversation list: 14:32 / вчера / 12 ноя. */
function formatListTime(value) {
  const date = parseDate(value);
  if (!date) return '';

  const now = new Date();
  if (sameDay(date, now)) return formatTime(value);

  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  if (sameDay(date, yesterday)) return 'вчера';

  if (date.getFullYear() === now.getFullYear()) {
    return date.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' }).replace('.', '');
  }

  return date.toLocaleDateString('ru-RU', { day: 'numeric', month: 'numeric', year: '2-digit' });
}

/** Day separator label: сегодня / вчера / пятница, 14 ноября. */
function formatDayLabel(value) {
  const date = parseDate(value);
  if (!date) return '';

  const now = new Date();
  if (sameDay(date, now)) return 'сегодня';

  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  if (sameDay(date, yesterday)) return 'вчера';

  const weekAgo = new Date(now);
  weekAgo.setDate(now.getDate() - 6);
  if (date > weekAgo) return WEEKDAYS[date.getDay()];

  const label = `${date.getDate()} ${MONTHS[date.getMonth()]}`;
  return date.getFullYear() === now.getFullYear()
    ? label
    : `${label} ${date.getFullYear()}`;
}

function formatDuration(seconds) {
  const total = Math.max(0, Math.round(Number(seconds) || 0));
  const mins = Math.floor(total / 60);
  const secs = total % 60;
  return `${mins}:${String(secs).padStart(2, '0')}`;
}

function formatSize(bytes) {
  const value = Number(bytes) || 0;
  if (value < 1024) return value + ' Б';
  if (value < 1024 * 1024) return (value / 1024).toFixed(0) + ' КБ';
  if (value < 1024 * 1024 * 1024) return (value / 1024 / 1024).toFixed(1) + ' МБ';
  return (value / 1024 / 1024 / 1024).toFixed(2) + ' ГБ';
}

// -------------------------------------------------------------------- roles

/**
 * UserRole travels as a number (0 member, 1 admin, 2 owner) because the same
 * serializer carries ConversationKind and MessageKind, where the client compares
 * numbers. Normalising here keeps every comparison in the UI readable and stops
 * `role !== 'member'` from being true for members.
 */
const ROLE_NAMES = ['member', 'admin', 'owner'];

function roleName(role) {
  if (typeof role === 'number') return ROLE_NAMES[role] || 'member';
  if (typeof role === 'string' && ROLE_NAMES.includes(role)) return role;
  return 'member';
}

function isStaff(role) {
  const name = roleName(role);
  return name === 'admin' || name === 'owner';
}

function roleLabel(role) {
  return { owner: 'владелец', admin: 'администратор', member: 'участник' }[roleName(role)];
}

// ------------------------------------------------------------------ avatars

const AVATAR_COLORS = [
  '#0088FF', '#7C5CFF', '#E5484D', '#F76808', '#12A594',
  '#D6409F', '#3E63DD', '#30A46C', '#AB4ABA', '#0D9488'
];

function colorFor(seed) {
  const text = String(seed || '?');
  let hash = 0;
  for (let i = 0; i < text.length; i++) {
    hash = (hash * 31 + text.charCodeAt(i)) | 0;
  }
  return AVATAR_COLORS[Math.abs(hash) % AVATAR_COLORS.length];
}

function initials(name) {
  const text = String(name || '?').trim();
  if (!text) return '?';
  const parts = text.split(/\s+/).filter(Boolean);
  if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
  return text.slice(0, 2).toUpperCase();
}

/**
 * Renders an avatar into an existing element. Falls back to initials on a tinted
 * circle, which is what most family chats will actually show.
 *
 * If the node contains a [data-avatar-text] child (an editable avatar with an icon
 * overlay), only that child is painted so the overlay survives re-renders.
 */
function paintAvatar(node, user, nameFallback) {
  if (!node) return;

  const name = user?.displayName || user?.nick || nameFallback || '?';
  const blob = user?.avatarBlob;

  const label = node.querySelector('[data-avatar-text]');
  const text = blob ? '' : initials(name);

  if (label) label.textContent = text;
  else node.textContent = text;

  node.style.backgroundImage = blob ? `url("${Api.blobUrl(blob)}")` : '';
  node.style.backgroundColor = blob ? 'transparent' : (user?.accent || colorFor(user?.id || name));
  node.style.color = '#fff';
  node.title = name;
}

// -------------------------------------------------------------- text helpers

const URL_PATTERN = /\b(https?:\/\/[^\s<>"']+)/gi;

/**
 * Turns plain text into safe HTML: escapes first, then re-links URLs and turns
 * @nick into a mention span. No markdown renderer, so nothing can inject markup.
 */
function renderText(text) {
  const safe = escapeHtml(text ?? '');

  const linked = safe.replace(URL_PATTERN, (match) => {
    const trimmed = match.replace(/[.,;:!?)]+$/, '');
    const tail = match.slice(trimmed.length);
    return `<a href="${trimmed}" target="_blank" rel="noopener noreferrer">${trimmed}</a>${tail}`;
  });

  return linked.replace(/(^|\s)@([\w.-]{2,32})/g, '$1<span class="mention">@$2</span>');
}

function isImage(mime, name) {
  if (mime && mime.startsWith('image/')) return true;
  return /\.(png|jpe?g|gif|webp|bmp|svg)$/i.test(name || '');
}

function isAudio(mime, name) {
  if (mime && mime.startsWith('audio/')) return true;
  return /\.(mp3|ogg|oga|wav|m4a|webm|opus)$/i.test(name || '');
}

function fileIcon(mime, name) {
  const value = (mime || '').toLowerCase();
  if (value.startsWith('image/')) return 'fa-file-image';
  if (value.startsWith('video/')) return 'fa-file-video';
  if (value.startsWith('audio/')) return 'fa-file-audio';
  if (value.includes('pdf')) return 'fa-file-pdf';
  if (value.includes('zip') || value.includes('rar') || value.includes('7z')) return 'fa-file-zipper';
  if (value.includes('word') || /\.docx?$/i.test(name || '')) return 'fa-file-word';
  if (value.includes('sheet') || value.includes('excel') || /\.xlsx?$/i.test(name || '')) return 'fa-file-excel';
  if (value.includes('text') || /\.(txt|md|log)$/i.test(name || '')) return 'fa-file-lines';
  if (/\.(cs|js|ts|py|json|xml|html|css|sh|ps1)$/i.test(name || '')) return 'fa-file-code';
  return 'fa-file';
}

// -------------------------------------------------------------------- toasts

function toast(message, kind = 'info', timeout = 3200) {
  const host = $('#toasts');
  if (!host) return;

  const icon = kind === 'error' ? 'fa-circle-exclamation'
             : kind === 'success' ? 'fa-circle-check'
             : 'fa-circle-info';

  const node = el('div', { class: `toast ${kind}` },
    el('i', { class: `fa-solid ${icon}` }),
    el('span', { text: message })
  );

  host.append(node);

  const remove = () => {
    node.classList.add('out');
    node.addEventListener('animationend', () => node.remove(), { once: true });
    setTimeout(() => node.remove(), 400);
  };

  node.addEventListener('click', remove);
  setTimeout(remove, timeout);
}

// --------------------------------------------------------------------- emoji

const EMOJI = [
  '😀', '😄', '😁', '😊', '🙂', '😉', '😍', '🥰',
  '😘', '😜', '🤪', '😎', '🤔', '🤨', '😐', '😴',
  '😢', '😭', '😤', '😠', '🤬', '🤯', '😱', '🥺',
  '👍', '👎', '👌', '✌️', '🤝', '👏', '🙌', '🙏',
  '❤️', '🧡', '💛', '💚', '💙', '💜', '🖤', '🤍',
  '🔥', '✨', '🎉', '🎂', '☕', '🍕', '🍺', '🥳',
  '🐱', '🐶', '🌻', '🌈', '⭐', '💡', '✅', '❌'
];

const QUICK_REACTIONS = ['👍', '❤️', '😂', '🔥', '😮', '😢'];

function buildEmojiPicker(host, onPick) {
  host.innerHTML = '';
  for (const emoji of EMOJI) {
    host.append(el('button', {
      type: 'button',
      text: emoji,
      title: emoji,
      onclick: () => onPick(emoji)
    }));
  }
}

// ------------------------------------------------------------------- misc

function debounce(fn, wait) {
  let timer = null;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), wait);
  };
}

function throttle(fn, wait) {
  let last = 0;
  let timer = null;
  return (...args) => {
    const now = Date.now();
    const remaining = wait - (now - last);
    if (remaining <= 0) {
      last = now;
      fn(...args);
    } else if (!timer) {
      timer = setTimeout(() => {
        last = Date.now();
        timer = null;
        fn(...args);
      }, remaining);
    }
  };
}

function autoGrow(textarea, maxHeight = 180) {
  textarea.style.height = 'auto';
  textarea.style.height = Math.min(textarea.scrollHeight, maxHeight) + 'px';
}

function scrollToBottom(host, smooth = false) {
  if (!host) return;
  host.scrollTo({ top: host.scrollHeight, behavior: smooth ? 'smooth' : 'auto' });
}

function isNearBottom(host, threshold = 120) {
  if (!host) return true;
  return host.scrollHeight - host.scrollTop - host.clientHeight < threshold;
}

/** Applies the server's brand palette as CSS custom properties. */
function applyBrand(brand) {
  if (!brand) return;

  const root = document.documentElement;

  // Only theme-independent values may live in inline style: an inline declaration
  // beats every stylesheet rule, so putting --bg here would freeze the theme switch.
  if (brand.accent) root.style.setProperty('--accent', brand.accent);
  if (brand.accentHi) root.style.setProperty('--accent-hi', brand.accentHi);
  if (brand.accentLo) root.style.setProperty('--accent-lo', brand.accentLo);

  // The accent also tints a few derived surfaces that live in stylesheet rules.
  const inject = (selector, values) => {
    const body = Object.entries(values)
      .filter(([, value]) => !!value)
      .map(([key, value]) => `${key}: ${value};`)
      .join(' ');
    if (!body) return;

    const style = document.createElement('style');
    style.textContent = `${selector} { ${body} }`;
    document.head.append(style);
  };

  // Each theme is targeted by attribute only. Including `:root` here would match the
  // html element in every theme and, having equal specificity, beat the dark block.
  inject('[data-theme="light"]', {
    '--bg': brand.lightBg,
    '--panel': brand.lightPanel,
    '--panel-2': brand.lightPanel2,
    '--text': brand.lightText
  });

  inject('[data-theme="dark"]', {
    '--bg': brand.darkBg,
    '--panel': brand.darkPanel,
    '--panel-2': brand.darkPanel2,
    '--text': brand.darkText
  });

  inject('[data-theme="oled"]', {
    '--bg': brand.oledBg,
    '--panel': brand.oledPanel,
    '--panel-2': brand.oledPanel2,
    '--text': brand.oledText
  });
}

function setTheme(name) {
  document.documentElement.setAttribute('data-theme', name);
  try { localStorage.setItem('kroshka.theme', name); } catch { /* ignore */ }
}
