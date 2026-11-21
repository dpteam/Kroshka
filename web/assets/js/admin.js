/* =============================================================================
   admin.js — the board's only script. Small on purpose: it reads state, renders
   tables, and calls maintenance endpoints. All rules live on the server.
   ============================================================================= */

(() => {
  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

  const state = {
    data: null,
    tab: 'overview',
    editUserId: null,
    configValues: {},
    timer: null
  };

  // ------------------------------------------------------------------ helpers

  function el(tag, attrs = {}, ...children) {
    const node = document.createElement(tag);

    for (const [key, value] of Object.entries(attrs)) {
      if (value === undefined || value === null || value === false) continue;
      if (key === 'class') node.className = value;
      else if (key === 'text') node.textContent = value;
      else if (key === 'html') node.innerHTML = value;
      else if (key.startsWith('on') && typeof value === 'function') {
        node.addEventListener(key.slice(2).toLowerCase(), value);
      } else if (value === true) node.setAttribute(key, '');
      else node.setAttribute(key, value);
    }

    for (const child of children.flat()) {
      if (child === null || child === undefined || child === false) continue;
      node.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }

    return node;
  }

  async function api(method, path, body) {
    const options = { method, credentials: 'same-origin', headers: {} };

    if (body !== undefined) {
      options.headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify(body);
    }

    const response = await fetch(path, options);
    const data = response.status === 204 ? null : await response.json().catch(() => null);

    if (!response.ok) {
      const error = new Error(data?.error || ('HTTP ' + response.status));
      error.status = response.status;
      throw error;
    }

    return data;
  }

  function toast(message, kind = 'info') {
    const host = $('#toasts');
    const icon = kind === 'error' ? 'fa-circle-exclamation'
               : kind === 'success' ? 'fa-circle-check'
               : 'fa-circle-info';

    const node = el('div', { class: `toast ${kind}` },
      el('i', { class: `fa-solid ${icon}` }),
      el('span', { text: message })
    );

    host.append(node);

    setTimeout(() => {
      node.classList.add('out');
      setTimeout(() => node.remove(), 400);
    }, 3200);
  }

  function fmtDate(value) {
    if (!value) return '—';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '—';
    return date.toLocaleString('ru-RU', {
      day: '2-digit', month: '2-digit', year: '2-digit',
      hour: '2-digit', minute: '2-digit'
    });
  }

  function fmtUptime(seconds) {
    const total = Math.max(0, Math.floor(seconds));
    const days = Math.floor(total / 86400);
    const hours = Math.floor((total % 86400) / 3600);
    const mins = Math.floor((total % 3600) / 60);

    if (days > 0) return `${days}д ${hours}ч`;
    if (hours > 0) return `${hours}ч ${mins}м`;
    return `${mins}м`;
  }

  // -------------------------------------------------------------------- auth

  async function login(event) {
    event.preventDefault();

    const password = $('#admin-password').value;
    const button = $('#admin-submit');
    const error = $('#admin-error');

    button.disabled = true;
    error.classList.add('hidden');

    try {
      await api('POST', '/api/admin/login', { password });
      await enter();
    } catch (err) {
      error.textContent = err.message || 'Неверный пароль';
      error.classList.remove('hidden');
    } finally {
      button.disabled = false;
    }
  }

  async function enter() {
    $('#admin-login').classList.add('hidden');
    $('#admin-app').classList.remove('hidden');
    await refresh();

    clearInterval(state.timer);
    state.timer = setInterval(() => {
      if (state.tab === 'overview') refresh(true);
    }, 15000);
  }

  function showLogin() {
    clearInterval(state.timer);
    $('#admin-app').classList.add('hidden');
    $('#admin-login').classList.remove('hidden');
  }

  // ------------------------------------------------------------------ state

  async function refresh(silent = false) {
    try {
      state.data = await api('GET', '/api/admin/state');

      $('#admin-title').textContent = state.data.server.name || 'Kroshka';

      renderOverview();

      if (state.tab === 'users') renderUsers();
      if (state.tab === 'rooms') renderRooms();
    } catch (err) {
      if (err.status === 401) {
        showLogin();
        return;
      }
      if (!silent) toast(err.message || 'Не удалось обновить', 'error');
    }
  }

  // --------------------------------------------------------------- overview

  function renderOverview() {
    const data = state.data;
    if (!data) return;

    const stats = [
      ['Участники', data.stats.users, true],
      ['Онлайн', data.stats.online, true],
      ['Комнаты', data.stats.rooms, false],
      ['Личные', data.stats.dms, false],
      ['Сообщений', data.stats.messages, false],
      ['Сессии', data.stats.sessions, false],
      ['Файлы', data.stats.blobFiles, false],
      ['Вложения', data.stats.blobMb + ' МБ', false],
      ['Данные', data.stats.dataMb + ' МБ', false],
      ['Логи', data.stats.logMb + ' МБ', false]
    ];

    const grid = $('#stat-grid');
    grid.innerHTML = '';

    for (const [label, value, accent] of stats) {
      grid.append(el('div', { class: `stat${accent ? ' accent' : ''}` },
        el('div', { class: 'stat-value', text: String(value) }),
        el('div', { class: 'stat-label', text: label })
      ));
    }

    const facts = $('#server-facts');
    facts.innerHTML = '';

    const rows = [
      ['Версия', data.server.version],
      ['Порт', data.server.port],
      ['Админ-порт', data.server.adminPort],
      ['Вход', data.server.authMode],
      ['Работает', fmtUptime(data.server.uptimeSec)],
      ['Запущен', fmtDate(data.server.startedUtc)],
      ['Данные', data.server.dataDir],
      ['Логи', data.server.logDir],
      ['Файл лога', data.server.logFile]
    ];

    for (const [key, value] of rows) {
      facts.append(el('dt', { text: key }), el('dd', { text: String(value ?? '—') }));
    }

    const warnings = data.warnings || [];
    $('#warn-card').classList.toggle('hidden', warnings.length === 0);

    const list = $('#warn-list');
    list.innerHTML = '';
    for (const warning of warnings) list.append(el('li', { text: warning }));
  }

  // ------------------------------------------------------------------ users

  function renderUsers() {
    const body = $('#users-table tbody');
    body.innerHTML = '';

    const users = state.data?.users || [];

    for (const user of users) {
      const roleChip = el('span', {
        class: `chip ${user.role === 'owner' ? 'owner' : user.role === 'admin' ? 'admin' : ''}`,
        text: user.role === 'owner' ? 'владелец' : user.role === 'admin' ? 'админ' : 'участник'
      });

      const status = user.banned
        ? el('span', { class: 'chip banned', text: 'заблокирован' })
        : user.online
          ? el('span', { class: 'chip online', text: 'в сети' })
          : el('span', { class: 'muted', text: 'офлайн' });

      body.append(el('tr', {},
        el('td', {}, el('strong', { text: user.nick })),
        el('td', { class: 'muted', text: user.displayName || '' }),
        el('td', {}, roleChip),
        el('td', {}, status),
        el('td', { class: 'muted', text: fmtDate(user.createdUtc) }),
        el('td', {},
          el('div', { class: 'actions' },
            el('button', {
              class: 'icon-btn tiny', type: 'button', title: 'Изменить',
              onclick: () => openEditUser(user)
            }, el('i', { class: 'fa-solid fa-pen' })),
            el('button', {
              class: 'icon-btn tiny', type: 'button', title: 'Удалить',
              onclick: () => deleteUser(user)
            }, el('i', { class: 'fa-solid fa-trash' }))
          )
        )
      ));
    }

    if (users.length === 0) {
      body.append(el('tr', {}, el('td', { colspan: '6', class: 'muted', text: 'Пока никого нет' })));
    }
  }

  function openEditUser(user) {
    state.editUserId = user.id;

    $('#edit-user-title').textContent = user.nick;
    $('#edit-user-name').value = user.displayName || '';
    $('#edit-user-role').value = user.role;
    $('#edit-user-banned').checked = !!user.banned;

    $('#modal-scrim').classList.remove('hidden');
    $('#modal-edit-user').classList.remove('hidden');
  }

  async function saveUser() {
    if (!state.editUserId) return;

    try {
      await api('PATCH', '/api/admin/users/' + encodeURIComponent(state.editUserId), {
        displayName: $('#edit-user-name').value.trim(),
        role: $('#edit-user-role').value,
        banned: $('#edit-user-banned').checked
      });

      closeModal();
      await refresh();
      toast('Сохранено', 'success');
    } catch (err) {
      toast(err.message || 'Не удалось сохранить', 'error');
    }
  }

  async function deleteUser(user) {
    if (!confirm(`Удалить ${user.nick}? Все его сессии и личные данные будут стёрты.`)) return;

    try {
      await api('DELETE', '/api/admin/users/' + encodeURIComponent(user.id));
      await refresh();
      toast('Пользователь удалён', 'success');
    } catch (err) {
      toast(err.message || 'Не удалось удалить', 'error');
    }
  }

  async function addUser() {
    const nick = prompt('Ник нового участника');
    if (!nick) return;

    try {
      await api('POST', '/api/admin/users', { nick: nick.trim() });
      await refresh();
      toast('Участник создан', 'success');
    } catch (err) {
      toast(err.message || 'Не удалось создать', 'error');
    }
  }

  // ------------------------------------------------------------------ rooms

  function renderRooms() {
    const body = $('#rooms-table tbody');
    body.innerHTML = '';

    const rooms = state.data?.rooms || [];

    for (const room of rooms) {
      const type = room.private
        ? el('span', { class: 'chip private', text: 'приватная' })
        : el('span', { class: 'chip', text: 'открытая' });

      body.append(el('tr', {},
        el('td', {}, el('strong', { text: room.name })),
        el('td', {}, type),
        el('td', { class: 'num', text: String(room.messages) }),
        el('td', { class: 'muted', text: fmtDate(room.lastUtc) }),
        el('td', {},
          el('div', { class: 'actions' },
            el('button', {
              class: 'icon-btn tiny', type: 'button', title: 'Удалить',
              onclick: () => deleteRoom(room)
            }, el('i', { class: 'fa-solid fa-trash' }))
          )
        )
      ));
    }

    if (rooms.length === 0) {
      body.append(el('tr', {}, el('td', { colspan: '5', class: 'muted', text: 'Комнат нет' })));
    }
  }

  async function deleteRoom(room) {
    if (!confirm(`Удалить комнату «${room.name}» вместе с историей?`)) return;

    try {
      await api('DELETE', '/api/admin/rooms/' + encodeURIComponent(room.id));
      await refresh();
      toast('Комната удалена', 'success');
    } catch (err) {
      toast(err.message || 'Не удалось удалить', 'error');
    }
  }

  async function addRoom() {
    const name = prompt('Название комнаты');
    if (!name) return;

    try {
      await api('POST', '/api/admin/rooms', { name: name.trim() });
      await refresh();
      toast('Комната создана', 'success');
    } catch (err) {
      toast(err.message || 'Не удалось создать', 'error');
    }
  }

  // ----------------------------------------------------------------- config

  const LABELS = {
    'site.name': 'Название',
    'site.tagline': 'Подпись',
    'site.motd': 'Сообщение дня',
    'site.footer': 'Подвал',
    'site.theme': 'Тема по умолчанию',
    'site.defaultSound': 'Звуки по умолчанию',
    'auth.mode': 'Режим входа',
    'auth.allowRegister': 'Разрешить регистрацию',
    'auth.password': 'Пароль семьи',
    'auth.inviteCode': 'Код приглашения',
    'auth.allowNickChange': 'Смена ника',
    'branding.accent': 'Акцент',
    'branding.accentHi': 'Акцент (hover)',
    'branding.accentLo': 'Акцент (тёмный)',
    'branding.darkBg': 'Тёмная: фон',
    'branding.darkPanel': 'Тёмная: панель',
    'branding.oledBg': 'OLED: фон',
    'features.reactions': 'Реакции',
    'features.replies': 'Ответы',
    'features.pins': 'Закрепление',
    'features.voice': 'Голосовые',
    'features.files': 'Файлы',
    'features.search': 'Поиск',
    'features.typing': 'Индикатор набора',
    'features.presence': 'Присутствие',
    'features.edit': 'Редактирование',
    'features.delete': 'Удаление',
    'features.rooms': 'Комнаты',
    'features.dm': 'Личные сообщения',
    'limits.maxMessageLen': 'Лимит сообщения',
    'limits.maxUploadMb': 'Лимит файла, МБ',
    'storage.pruneMonths': 'Хранить, мес.',
    'storage.keepBackups': 'Копий хранить',
    'server.discoverable': 'Видимость в локальной сети'
  };

  const GROUPS = {
    'Сайт': ['site.name', 'site.tagline', 'site.motd', 'site.footer', 'site.theme', 'site.defaultSound'],
    'Вход': ['auth.mode', 'auth.allowRegister', 'auth.password', 'auth.inviteCode', 'auth.allowNickChange'],
    'Цвета': ['branding.accent', 'branding.accentHi', 'branding.accentLo', 'branding.darkBg', 'branding.darkPanel', 'branding.oledBg'],
    'Функции': ['features.reactions', 'features.replies', 'features.pins', 'features.voice',
                'features.files', 'features.search', 'features.typing', 'features.presence',
                'features.edit', 'features.delete', 'features.rooms', 'features.dm'],
    'Лимиты': ['limits.maxMessageLen', 'limits.maxUploadMb'],
    'Хранение': ['storage.pruneMonths', 'storage.keepBackups'],
    'Сеть': ['server.discoverable']
  };

  async function loadConfig() {
    try {
      const result = await api('GET', '/api/admin/config');
      state.configValues = result.values || {};
      renderConfig();
    } catch (err) {
      toast(err.message || 'Не удалось загрузить настройки', 'error');
    }
  }

  function renderConfig() {
    const host = $('#config-form');
    host.innerHTML = '';

    for (const [groupName, keys] of Object.entries(GROUPS)) {
      const group = el('div', { class: 'config-group' },
        el('h3', { text: groupName })
      );

      for (const key of keys) {
        const value = state.configValues[key] ?? '';
        group.append(configRow(key, value));
      }

      host.append(group);
    }
  }

  function configRow(key, value) {
    const label = el('label', { text: LABELS[key] || key, title: key });
    let control;

    const isColor = key.includes('accent') || key.includes('Bg') || key.includes('Panel');
    const isBool = value === 'true' || value === 'false';

    if (isColor) {
      const color = el('input', { type: 'color', value: /^#[0-9a-f]{6}$/i.test(value) ? value : '#0088FF' });
      const text = el('input', { type: 'text', class: 'input', value, dataset: { key } });

      color.addEventListener('input', () => { text.value = color.value.toUpperCase(); });
      text.addEventListener('input', () => {
        if (/^#[0-9a-f]{6}$/i.test(text.value)) color.value = text.value;
      });

      control = el('div', { class: 'color-input' }, color, text);
      control.dataset.key = key;
      control.dataset.role = 'color';
    } else if (isBool) {
      control = el('label', { class: 'switch' },
        el('input', { type: 'checkbox', checked: value === 'true', dataset: { key } }),
        el('span', { text: value === 'true' ? 'включено' : 'выключено' })
      );
    } else if (key === 'auth.mode') {
      const select = el('select', { class: 'plain-select', dataset: { key } },
        el('option', { value: 'open', text: 'открытый (только ник)' }),
        el('option', { value: 'password', text: 'пароль семьи' }),
        el('option', { value: 'invite', text: 'по приглашению' })
      );
      select.value = value || 'open';
      control = select;
    } else if (key === 'site.theme') {
      const select = el('select', { class: 'plain-select', dataset: { key } },
        el('option', { value: 'light', text: 'светлая' }),
        el('option', { value: 'dark', text: 'тёмная' }),
        el('option', { value: 'oled', text: 'OLED' })
      );
      select.value = value || 'light';
      control = select;
    } else {
      control = el('input', {
        type: key.includes('password') ? 'password' : 'text',
        class: 'input',
        value,
        dataset: { key }
      });
    }

    return el('div', { class: 'config-row' }, label, control);
  }

  async function saveConfig() {
    const values = {};

    for (const node of $$('#config-form [data-key]')) {
      const key = node.dataset.key;

      if (node.dataset.role === 'color') {
        const text = node.querySelector('input[type="text"]');
        values[key] = text.value.trim();
        continue;
      }

      if (node.type === 'checkbox') {
        values[key] = node.checked ? 'true' : 'false';
        continue;
      }

      values[key] = node.value;
    }

    try {
      const result = await api('POST', '/api/admin/config', values);
      toast(result?.restartRequired
        ? 'Сохранено. Часть настроек применится после перезапуска.'
        : 'Сохранено', 'success', 4200);
    } catch (err) {
      toast(err.message || 'Не удалось сохранить', 'error');
    }
  }

  // ------------------------------------------------------------ maintenance

  async function doBackup() {
    try {
      const result = await api('POST', '/api/admin/maintenance/backup', {});
      toast('Копия создана: ' + result.name, 'success');
      loadBackups();
      await refresh();
    } catch (err) {
      toast(err.message || 'Не удалось', 'error');
    }
  }

  async function doCompact() {
    if (!confirm('Сжать журналы сообщений?')) return;

    try {
      const result = await api('POST', '/api/admin/maintenance/compact', {});
      toast(`Готово. Отброшено строк: ${result.droppedLines}`, 'success');
      await refresh();
    } catch (err) {
      toast(err.message || 'Не удалось', 'error');
    }
  }

  async function doPrune() {
    const months = Number($('#prune-months').value) || 0;
    if (months <= 0) {
      toast('Укажите срок в месяцах', 'error');
      return;
    }

    if (!confirm(`Удалить все сообщения старше ${months} мес.? Это необратимо.`)) return;

    try {
      const result = await api('POST', '/api/admin/maintenance/prune', { months });
      toast(`Удалено сообщений: ${result.removed}`, 'success');
      await refresh();
    } catch (err) {
      toast(err.message || 'Не удалось', 'error');
    }
  }

  async function doSweep() {
    if (!confirm('Удалить вложения без ссылок?')) return;

    try {
      const result = await api('POST', '/api/admin/maintenance/sweep', {});
      toast(`Удалено файлов: ${result.removed}, освобождено ${result.freedMb} МБ`, 'success');
      await refresh();
    } catch (err) {
      toast(err.message || 'Не удалось', 'error');
    }
  }

  async function loadBackups() {
    const host = $('#backup-list');
    if (!host) return;

    try {
      const result = await api('GET', '/api/admin/backups');
      host.innerHTML = '';

      for (const backup of result.backups || []) {
        host.append(el('div', {},
          el('span', { text: backup.name }),
          el('span', { text: backup.sizeMb + ' МБ' })
        ));
      }

      if ((result.backups || []).length === 0) {
        host.append(el('div', {}, el('span', { text: 'Копий пока нет' })));
      }
    } catch { /* the list is decorative */ }
  }

  // ------------------------------------------------------------------- logs

  async function loadLogs() {
    const lines = $('#log-lines').value;

    try {
      const result = await api('GET', `/api/admin/logs?lines=${lines}`);

      $('#log-file').textContent = result.file ? 'Файл: ' + result.file : '';

      const view = $('#log-view');
      view.innerHTML = '';

      for (const entry of result.entries || []) {
        const level = String(entry.level || 'info');
        const letter = level[0].toUpperCase();
        view.append(el('div', {
          class: `lv-${letter.toLowerCase()}`,
          text: `${entry.time} [${letter}] ${entry.category} ${entry.message}`
        }));
      }

      if ((result.entries || []).length === 0) {
        view.append(el('div', { text: 'Пусто' }));
      }
    } catch (err) {
      toast(err.message || 'Не удалось загрузить логи', 'error');
    }
  }

  // ------------------------------------------------------------------- tabs

  function switchTab(tab) {
    state.tab = tab;

    $$('#admin-tabs .tab').forEach((button) => {
      button.classList.toggle('active', button.dataset.tab === tab);
    });

    $$('.panel').forEach((panel) => {
      panel.classList.toggle('hidden', panel.dataset.panel !== tab);
    });

    if (tab === 'config') loadConfig();
    if (tab === 'logs') loadLogs();
    if (tab === 'maintenance') loadBackups();
    if (tab === 'users') renderUsers();
    if (tab === 'rooms') renderRooms();
  }

  function closeModal() {
    $('#modal-edit-user').classList.add('hidden');
    $('#modal-scrim').classList.add('hidden');
    state.editUserId = null;
  }

  // ------------------------------------------------------------------- boot

  function bind() {
    $('#admin-login-form').addEventListener('submit', login);

    $$('#admin-tabs .tab').forEach((button) => {
      button.addEventListener('click', () => switchTab(button.dataset.tab));
    });

    $('#btn-refresh').addEventListener('click', () => {
      refresh();
      if (state.tab === 'logs') loadLogs();
      if (state.tab === 'maintenance') loadBackups();
      toast('Обновлено', 'success', 1400);
    });

    $('#btn-admin-logout').addEventListener('click', async () => {
      try { await api('POST', '/api/admin/logout', {}); } catch { /* ignore */ }
      showLogin();
    });

    $('#btn-add-user').addEventListener('click', addUser);
    $('#btn-add-room').addEventListener('click', addRoom);
    $('#btn-save-config').addEventListener('click', saveConfig);
    $('#btn-save-user').addEventListener('click', saveUser);

    $('#btn-backup').addEventListener('click', doBackup);
    $('#btn-compact').addEventListener('click', doCompact);
    $('#btn-prune').addEventListener('click', doPrune);
    $('#btn-sweep').addEventListener('click', doSweep);
    $('#btn-logs-refresh').addEventListener('click', loadLogs);

    $$('[data-close]').forEach((button) => button.addEventListener('click', closeModal));
    $('#modal-scrim').addEventListener('click', closeModal);

    document.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') closeModal();
    });
  }

  async function start() {
    bind();

    // Already signed in? /api/admin/state answers 200 and we skip the form.
    try {
      const health = await api('GET', '/api/admin/health');

      if (health && health.passwordConfigured === false) {
        showLogin();
        const error = $('#admin-error');
        error.textContent = 'Пароль админки не задан. Укажите admin.password в config.ini и перезапустите сервер.';
        error.classList.remove('hidden');
        $('#admin-submit').disabled = true;
        return;
      }

      await api('GET', '/api/admin/state');
      await enter();
    } catch (err) {
      showLogin();
      if (err.status !== 401) {
        const error = $('#admin-error');
        error.textContent = err.message || 'Сервер недоступен';
        error.classList.remove('hidden');
      } else {
        $('#admin-password').focus();
      }
    }
  }

  document.addEventListener('DOMContentLoaded', start);
})();
