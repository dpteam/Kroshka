/* =============================================================================
   app.js — the chat state machine.

   Layout: one module, clearly separated into boot / socket / rendering / actions.
   Every server push is handled by a single dispatch function so there is exactly one
   place that knows what an event means.
   ============================================================================= */

const App = (() => {
  // ------------------------------------------------------------------- state

  const state = {
    boot: null,              // BootstrapDto
    me: null,                // User
    users: new Map(),        // id -> User
    conversations: new Map(),// id -> ConversationDto
    messages: new Map(),     // conversationId -> Message[] (ascending seq)
    hasMore: new Map(),      // conversationId -> bool
    activeId: null,
    online: new Set(),
    typing: new Map(),       // conversationId -> Map(userId -> timeoutId)
    replyTo: null,
    editing: null,
    pendingFiles: null,
    searchQuery: '',
    searchMode: false,
    socket: null,
    socketAttempts: 0,
    socketTimer: null,
    typingSent: 0,
    typingTimer: null,
    recorder: null,
    lastReadSent: new Map(),
    theme: 'light'
  };

  const MAX_MESSAGES_IN_MEMORY = 400;

  // -------------------------------------------------------------------- boot

  async function boot() {
    applyLocalTheme();

    let boot;
    try {
      boot = await Api.bootstrap();
    } catch (err) {
      showLoginFatal('Не удалось связаться с сервером. Проверьте, что он запущен.');
      return;
    }

    state.boot = boot;
    applyBrand(boot.brand);
    applyBranding(boot);

    if (boot.authMode === 'password') {
      $('#login-password-field').classList.remove('hidden');
    }
    if (boot.authMode === 'invite') {
      $('#login-invite-field').classList.remove('hidden');
    }

    $('#login-nick').maxLength = boot.maxNickLen || 24;

    // A stored token is enough to come back in; otherwise show the form.
    if (Api.getToken()) {
      try {
        const result = await Api.me();
        state.me = result.user || result.me;
        await enterApp();
        return;
      } catch {
        Api.setToken(null);
      }
    }

    showLogin();
  }

  function applyBranding(boot) {
    const name = boot.serverName || 'Kroshka';
    document.title = name;

    $('#login-name').textContent = name;
    $('#login-tagline').textContent = boot.tagline || '';
    $('#brand-name').textContent = name;

    const logo = $('#login-logo');
    if (boot.logoUrl) {
      logo.textContent = '';
      logo.style.backgroundImage = `url("${boot.logoUrl}")`;
    } else {
      logo.textContent = boot.serverShort || name.slice(0, 1).toUpperCase();
    }

    const foot = [];
    if (boot.userCount) foot.push(`${boot.userCount} участник${plural(boot.userCount, '', 'а', 'ов')}`);
    if (boot.motd) foot.push(boot.motd);
    $('#login-foot').textContent = foot.join(' · ');

    $('#login-submit').querySelector('span').textContent =
      boot.allowRegister ? 'Войти или создать' : 'Войти';
  }

  function plural(n, one, few, many) {
    const mod10 = n % 10;
    const mod100 = n % 100;
    if (mod10 === 1 && mod100 !== 11) return one;
    if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return few;
    return many;
  }

  function applyLocalTheme() {
    let saved = null;
    try { saved = localStorage.getItem('kroshka.theme'); } catch { /* ignore */ }
    state.theme = saved || 'light';
    setTheme(state.theme);
  }

  function showLogin() {
    document.body.classList.remove('booting');
    $('#login').classList.remove('hidden');
    $('#app').classList.add('hidden');
    setTimeout(() => $('#login-nick').focus(), 60);
  }

  function showLoginFatal(message) {
    document.body.classList.remove('booting');
    $('#login').classList.remove('hidden');
    const box = $('#login-error');
    box.textContent = message;
    box.classList.remove('hidden');
    $('#login-submit').disabled = true;
  }

  // ------------------------------------------------------------------- login

  async function doLogin(event) {
    event.preventDefault();

    const nick = $('#login-nick').value.trim();
    const password = $('#login-password').value;
    const invite = $('#login-invite').value.trim();
    const remember = $('#login-remember').checked;

    if (nick.length < 2) {
      loginError('Ник слишком короткий');
      return;
    }

    const button = $('#login-submit');
    button.disabled = true;

    try {
      const result = await Api.login(nick, password, invite, remember);
      state.me = result.user;
      Sound.unlock();
      await enterApp();
    } catch (err) {
      loginError(err.message || 'Не удалось войти');
      Sound.play('error');
    } finally {
      button.disabled = false;
    }
  }

  function loginError(message) {
    const box = $('#login-error');
    box.textContent = message;
    box.classList.remove('hidden');
    box.style.animation = 'none';
    void box.offsetWidth;
    box.style.animation = '';
  }

  async function enterApp() {
    document.body.classList.remove('booting');
    $('#login').classList.add('hidden');
    $('#app').classList.remove('hidden');

    try {
      const sync = await Api.sync();
      state.me = sync.me || state.me;
      state.users.clear();
      for (const [id, user] of Object.entries(sync.users || {})) state.users.set(id, user);
      if (state.me) state.users.set(state.me.id, state.me);

      state.conversations.clear();
      for (const conv of sync.conversations || []) state.conversations.set(conv.id, conv);

      state.online = new Set(sync.online || []);

      renderMe();
      renderConversations();
      connectSocket();

      const last = readLastConversation();
      const target = last && state.conversations.has(last) ? last : firstConversationId();
      if (target) openConversation(target);

      Sound.setEnabled(state.me?.sound !== false);
      $('#set-sound').checked = state.me?.sound !== false;
      $('#set-notifications').checked = state.me?.notifications !== false;
    } catch (err) {
      // A failure here means the app is half-built; surface it in the console too,
      // because a toast alone leaves nothing to debug later.
      console.error('enterApp failed', err);
      toast(err.message || 'Не удалось загрузить данные', 'error');
    }
  }

  function firstConversationId() {
    const list = sortedConversations();
    return list.length > 0 ? list[0].id : null;
  }

  // ------------------------------------------------------------------ socket

  function connectSocket() {
    if (state.socket && (state.socket.readyState === WebSocket.OPEN || state.socket.readyState === WebSocket.CONNECTING)) {
      return;
    }

    const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
    const token = Api.getToken();
    const query = token ? `?t=${encodeURIComponent(token)}` : '';

    // An explicit remote server overrides the page origin; otherwise stay relative
    // so the desktop WebView2 host and a LAN browser both work unchanged.
    const base = Api.getServer();
    const socketUrl = base
      ? base.replace(/^http/, 'ws') + `/ws${query}`
      : `${scheme}://${location.host}/ws${query}`;

    let socket;
    try {
      socket = new WebSocket(socketUrl);
    } catch {
      scheduleReconnect();
      return;
    }

    state.socket = socket;
    setConnection('connecting');

    socket.addEventListener('open', () => {
      state.socketAttempts = 0;
      setConnection('online');
      // Re-subscribe so a reconnect after a dropped Wi-Fi link resumes live updates.
      for (const id of state.conversations.keys()) {
        socket.send(JSON.stringify({ t: 'subscribe', conversationId: id }));
      }
      resyncAfterReconnect();
    });

    socket.addEventListener('message', (event) => {
      let frame;
      try { frame = JSON.parse(event.data); } catch { return; }
      handleEvent(frame);
    });

    socket.addEventListener('close', () => {
      setConnection('offline');
      scheduleReconnect();
    });

    socket.addEventListener('error', () => {
      setConnection('offline');
    });
  }

  function scheduleReconnect() {
    if (state.socketTimer) return;

    state.socketAttempts++;
    const delay = Math.min(1000 * Math.pow(1.7, state.socketAttempts), 15000);

    state.socketTimer = setTimeout(() => {
      state.socketTimer = null;
      connectSocket();
    }, delay);
  }

  function setConnection(status) {
    const node = $('#conn-state');
    if (!node) return;

    node.classList.remove('online', 'offline');
    if (status === 'online') node.classList.add('online');
    else if (status === 'offline') node.classList.add('offline');

    node.title = status === 'online' ? 'На связи'
               : status === 'offline' ? 'Нет связи, переподключение…'
               : 'Подключение…';
  }

  /**
   * After a reconnect we may have missed messages, so pull the tail of the active
   * conversation instead of trusting the in-memory list.
   */
  const resyncAfterReconnect = debounce(async () => {
    if (!state.activeId) return;

    try {
      const data = await Api.page(state.activeId, 0, state.boot?.pageSize || 50);
      const incoming = data.messages || [];
      if (incoming.length === 0) return;

      for (const [id, user] of Object.entries(data.users || {})) state.users.set(id, user);

      const known = state.messages.get(state.activeId) || [];
      const lastKnownSeq = known.length > 0 ? known[known.length - 1].seq : 0;

      const missing = incoming.filter((m) => m.seq > lastKnownSeq);
      if (missing.length === 0) return;

      const merged = mergeMessages(known, missing);
      state.messages.set(state.activeId, merged);
      renderMessages({ keepScroll: false });
      Sound.play('received');
    } catch { /* offline again, the next reconnect retries */ }
  }, 400);

  // -------------------------------------------------------------- event pump

  function handleEvent(frame) {
    switch (frame.t) {
      case 'ready':
        break;

      case 'message':
        onMessage(frame.data);
        break;

      case 'message.edited':
        onMessageEdited(frame.data);
        break;

      case 'message.deleted':
        onMessageDeleted(frame);
        break;

      case 'reaction':
        onReaction(frame);
        break;

      case 'typing':
        onTyping(frame);
        break;

      case 'presence':
        onPresence(frame);
        break;

      case 'read':
        onRead(frame);
        break;

      case 'pinned':
        onPinned(frame);
        break;

      case 'room.created':
      case 'room.updated':
        onRoomChanged(frame.data);
        break;

      case 'room.deleted':
        onRoomDeleted(frame.conversationId);
        break;

      case 'dm.created':
        onDmCreated(frame.data);
        break;

      case 'user.updated':
        onUserUpdated(frame.data);
        break;

      case 'call.signal':
        // Reserved for the WebRTC phase; kept so signalling has a home.
        break;

      case 'notice':
        toast(frame.text || '');
        break;

      case 'error':
        toast(frame.text || 'Ошибка', 'error');
        break;
    }
  }

  function onMessage(message) {
    if (!message) return;

    // A message may arrive from someone this client has never seen - a person who
    // joined after we synced, or an author we have no profile for. Fetch them once
    // so the bubble shows a name instead of "кто-то".
    if (message.authorId && !state.users.has(message.authorId)) {
      ensureUser(message.authorId);
    }

    const list = state.messages.get(message.conversationId);
    const isActive = message.conversationId === state.activeId;

    if (list) {
      if (!list.some((m) => m.id === message.id)) {
        state.messages.set(message.conversationId, mergeMessages(list, [message]));
      }
    }

    updateConversationFromMessage(message);
    renderConversations();

    if (isActive) {
      const stick = isNearBottom($('#messages'));
      renderMessages({ keepScroll: !stick });
      if (message.authorId !== state.me?.id) {
        Sound.play('received');
        markRead(message.conversationId, message.seq);
      } else {
        Sound.play('sent');
      }
    } else if (message.authorId !== state.me?.id) {
      Sound.play('received');
      notify(message);
    }
  }

  function onMessageEdited(message) {
    if (!message) return;
    replaceMessage(message);

    if (message.conversationId === state.activeId) renderMessages({ keepScroll: true });
    updateConversationFromMessage(message);
    renderConversations();
  }

  function onMessageDeleted(frame) {
    const list = state.messages.get(frame.conversationId);
    if (list) {
      const target = list.find((m) => m.id === frame.id);
      if (target) {
        target.deleted = true;
        target.text = '';
        target.attachment = null;
        target.reactions = {};
      }
    }

    if (frame.conversationId === state.activeId) renderMessages({ keepScroll: true });
  }

  function onReaction(frame) {
    const list = state.messages.get(frame.conversationId);
    if (!list) return;

    const target = list.find((m) => m.id === frame.id);
    if (!target) return;

    target.reactions = frame.data || {};

    if (frame.conversationId === state.activeId) renderMessages({ keepScroll: true });
  }

  function onTyping(frame) {
    if (!frame.conversationId || frame.userId === state.me?.id) return;
    if (frame.conversationId !== state.activeId) return;
    if (!state.users.has(frame.userId)) ensureUser(frame.userId);

    if (!state.typing.has(frame.conversationId)) state.typing.set(frame.conversationId, new Map());
    const map = state.typing.get(frame.conversationId);

    clearTimeout(map.get(frame.userId));
    if (!frame.on) {
      map.delete(frame.userId);
      renderTyping();
      return;
    }

    map.set(frame.userId, setTimeout(() => {
      map.delete(frame.userId);
      renderTyping();
    }, 6000));

    renderTyping();
  }

  function onPresence(frame) {
    if (!frame.userId) return;

    if (frame.on) state.online.add(frame.userId);
    else state.online.delete(frame.userId);

    renderConversations();
    updateChatSubtitle();
  }

  function onRead(frame) {
    // Only meaningful for group rooms: it shows how far others have read.
    if (frame.conversationId !== state.activeId) return;
  }

  function onPinned(frame) {
    const conv = state.conversations.get(frame.conversationId);
    if (!conv) return;

    conv.pinnedMessageId = frame.id || '';
    renderPinned();
  }

  function onRoomChanged(room) {
    if (!room) return;

    const existing = state.conversations.get(room.id);
    if (existing) {
      Object.assign(existing, {
        title: room.name,
        subtitle: room.topic,
        emoji: room.emoji,
        isPrivate: room.isPrivate,
        readOnly: room.readOnly,
        archived: room.archived,
        memberIds: room.members || existing.memberIds,
        members: (room.members || []).length || existing.members
      });
    } else {
      state.conversations.set(room.id, {
        id: room.id,
        kind: 0,
        title: room.name,
        subtitle: room.topic,
        emoji: room.emoji,
        isPrivate: room.isPrivate,
        readOnly: room.readOnly,
        archived: room.archived,
        lastSeq: room.lastSeq || 0,
        preview: '',
        lastUtc: null,
        unread: 0,
        lastReadSeq: 0,
        memberIds: room.members || [],
        members: (room.members || []).length
      });
    }

    renderConversations();
    if (state.activeId === room.id) {
      renderChatHead();
      renderPinned();
    }
  }

  function onRoomDeleted(conversationId) {
    if (!conversationId) return;

    state.conversations.delete(conversationId);
    state.messages.delete(conversationId);

    if (state.activeId === conversationId) {
      state.activeId = null;
      $('#chat-body').classList.add('hidden');
      $('#chat-empty').classList.remove('hidden');
    }

    renderConversations();
  }

  function onDmCreated(dm) {
    if (!dm) return;

    const peerId = dm.userA === state.me?.id ? dm.userB : dm.userA;
    if (!state.conversations.has(dm.id)) {
      const peer = state.users.get(peerId);
      state.conversations.set(dm.id, {
        id: dm.id,
        kind: 1,
        title: peer?.displayName || peer?.nick || 'Личка',
        subtitle: '',
        avatarBlob: peer?.avatarBlob || '',
        peerId,
        lastSeq: dm.lastSeq || 0,
        preview: dm.lastMessagePreview || '',
        lastUtc: dm.lastMessageUtc || null,
        unread: 0,
        lastReadSeq: 0,
        memberIds: [dm.userA, dm.userB],
        members: 2
      });
    }

    renderConversations();
    openConversation(dm.id);
  }

  function onUserUpdated(user) {
    if (!user) return;
    state.users.set(user.id, user);

    if (state.me?.id === user.id) {
      state.me = user;
      renderMe();
    }

    for (const conv of state.conversations.values()) {
      if (conv.kind === 1 && conv.peerId === user.id) {
        conv.title = user.displayName || user.nick;
        conv.avatarBlob = user.avatarBlob;
      }
    }

    renderConversations();
    if (state.activeId) renderMessages({ keepScroll: true });
  }

  /**
   * Fetches one profile on demand. Called when a message or a typing notice names
   * somebody we have no record of; the result is cached like any other user.
   */
  const pendingUsers = new Set();

  async function ensureUser(userId) {
    if (!userId || state.users.has(userId) || pendingUsers.has(userId)) return;

    pendingUsers.add(userId);
    try {
      const user = await Api.get('/api/users/' + encodeURIComponent(userId));
      if (user) {
        state.users.set(user.id, user);
        renderConversations();
        if (state.activeId) renderMessages({ keepScroll: true });
        renderTyping();
      }
    } catch {
      // A deleted or banned account has no profile; the bubble stays anonymous.
    } finally {
      pendingUsers.delete(userId);
    }
  }

  // ------------------------------------------------------------- conversations

  function sortedConversations() {
    return Array.from(state.conversations.values())
      .filter((conv) => !conv.archived)
      .sort((a, b) => {
        const at = a.lastUtc ? new Date(a.lastUtc).getTime() : 0;
        const bt = b.lastUtc ? new Date(b.lastUtc).getTime() : 0;
        if (bt !== at) return bt - at;
        return (a.title || '').localeCompare(b.title || '');
      });
  }

  function updateConversationFromMessage(message) {
    const conv = state.conversations.get(message.conversationId);
    if (!conv) return;

    conv.lastSeq = Math.max(conv.lastSeq || 0, message.seq || 0);
    conv.lastUtc = message.createdUtc;
    conv.preview = previewOf(message);

    if (message.conversationId !== state.activeId && message.authorId !== state.me?.id) {
      conv.unread = (conv.unread || 0) + 1;
    }
  }

  function previewOf(message) {
    if (!message) return '';
    if (message.deleted) return '';

    switch (message.type) {
      case 3: return '📷 Фото';
      case 2: return '🎤 Голосовое';
      case 1: return '📎 ' + (message.attachment?.name || 'Файл');
      case 4: return message.text || '';
      default: return (message.text || '').replace(/\s+/g, ' ').slice(0, 140);
    }
  }

  function renderConversations() {
    const host = $('#conv-list');
    if (!host) return;

    host.innerHTML = '';

    const list = sortedConversations();

    if (list.length === 0) {
      host.append(el('p', {
        class: 'conv-empty',
        style: 'padding:20px 12px;color:var(--text-faint);font-size:13px;text-align:center',
        text: 'Пока пусто. Создайте комнату.'
      }));
      updateBadge();
      return;
    }

    for (const conv of list) {
      host.append(conversationRow(conv));
    }

    updateBadge();
  }

  function conversationRow(conv) {
    const isActive = conv.id === state.activeId;
    const unread = conv.unread || 0;

    const row = el('button', {
      class: `conv${isActive ? ' active' : ''}${unread > 0 ? ' unread' : ''}`,
      type: 'button',
      onclick: () => openConversation(conv.id)
    });

    // avatar or emoji tile
    if (conv.kind === 1) {
      const peer = state.users.get(conv.peerId);
      const avatar = el('span', { class: 'avatar' });
      paintAvatar(avatar, peer, conv.title);
      row.append(avatar);
    } else if (conv.emoji) {
      row.append(el('span', { class: 'conv-emoji', text: conv.emoji }));
    } else {
      row.append(el('span', { class: 'conv-emoji' },
        el('i', { class: `fa-solid ${conv.isPrivate ? 'fa-lock' : 'fa-hashtag'}`, style: 'font-size:16px;color:var(--text-dim)' })
      ));
    }

    const body = el('div', { class: 'conv-body' },
      el('div', { class: 'conv-top' },
        el('span', { class: 'conv-name', text: conv.title || 'Без названия' }),
        el('span', { class: 'conv-time', text: formatListTime(conv.lastUtc) })
      ),
      el('div', { class: 'conv-bottom' },
        el('span', { class: 'conv-preview', text: conv.preview || (conv.subtitle || '') }),
        unread > 0 ? el('span', { class: 'badge', text: unread > 99 ? '99+' : String(unread) }) : null
      )
    );

    row.append(body);
    return row;
  }

  function renderMe() {
    if (!state.me) return;

    paintAvatar($('#me-avatar'), state.me, '?');
    $('#me-name').textContent = state.me.displayName || state.me.nick;
    $('#me-role').textContent = roleLabel(state.me.role);

    paintAvatar($('#profile-avatar'), state.me, '?');
    $('#profile-name').value = state.me.displayName || '';
    $('#profile-bio').value = state.me.bio || '';
  }

  // ---------------------------------------------------------------- open chat

  async function openConversation(conversationId) {
    if (!conversationId) return;

    if (state.activeId && state.activeId !== conversationId) {
      state.typing.delete(state.activeId);
    }

    state.activeId = conversationId;
    state.replyTo = null;
    state.editing = null;
    hideReplyBar();
    storeLastConversation(conversationId);

    const conv = state.conversations.get(conversationId);
    if (conv) conv.unread = 0;

    $('#chat-empty').classList.add('hidden');
    $('#chat-body').classList.remove('hidden');
    $('#app').classList.add('in-chat');

    renderConversations();
    renderChatHead();
    renderPinned();
    renderTyping();
    Sound.play('open');

    if (!state.messages.has(conversationId)) {
      $('#messages-inner').innerHTML = '';
      await loadPage(conversationId, 0);
    } else {
      renderMessages({ keepScroll: false });
    }

    markRead(conversationId, conv?.lastSeq || 0);

    if (window.innerWidth > 820) $('#composer-input').focus();
  }

  async function loadPage(conversationId, beforeSeq) {
    try {
      const data = await Api.page(conversationId, beforeSeq, state.boot?.pageSize || 50);

      for (const [id, user] of Object.entries(data.users || {})) state.users.set(id, user);

      const incoming = data.messages || [];
      const existing = state.messages.get(conversationId) || [];
      const merged = beforeSeq > 0 ? mergeMessages(incoming, existing) : mergeMessages(existing, incoming);

      state.messages.set(conversationId, trimMemory(merged));
      state.hasMore.set(conversationId, !!data.hasMore);

      if (conversationId === state.activeId) {
        renderMessages({ keepScroll: beforeSeq > 0 });
      }
    } catch (err) {
      toast(err.message || 'Не удалось загрузить историю', 'error');
    }
  }

  function trimMemory(list) {
    if (list.length <= MAX_MESSAGES_IN_MEMORY) return list;
    return list.slice(list.length - MAX_MESSAGES_IN_MEMORY);
  }

  function mergeMessages(a, b) {
    const map = new Map();
    for (const message of a) map.set(message.id, message);
    for (const message of b) map.set(message.id, message);

    return Array.from(map.values())
      .sort((x, y) => (x.seq || 0) - (y.seq || 0));
  }

  function replaceMessage(message) {
    const list = state.messages.get(message.conversationId);
    if (!list) return;

    const index = list.findIndex((m) => m.id === message.id);
    if (index >= 0) list[index] = { ...list[index], ...message };
  }

  function renderChatHead() {
    const conv = state.conversations.get(state.activeId);
    if (!conv) return;

    $('#chat-title').textContent = (conv.emoji ? conv.emoji + ' ' : '') + (conv.title || '');

    const actions = $('#btn-room-settings');
    const canManage = conv.kind === 0 && (isStaff(state.me?.role) || conv.isPrivate);
    actions.classList.toggle('hidden', !canManage);

    updateChatSubtitle();
  }

  function updateChatSubtitle() {
    const conv = state.conversations.get(state.activeId);
    if (!conv) return;

    let text = '';

    if (conv.kind === 1) {
      const peer = state.users.get(conv.peerId);
      if (state.online.has(conv.peerId)) text = 'в сети';
      else if (peer?.lastSeenUtc) text = 'был(а) ' + formatListTime(peer.lastSeenUtc);
      else text = 'личный диалог';
    } else {
      const parts = [];
      if (conv.subtitle) parts.push(conv.subtitle);
      if (conv.readOnly) parts.push('только чтение');
      if (conv.isPrivate) parts.push('приватная');
      const count = (conv.memberIds || []).length;
      if (count > 0) parts.push(`${count} участник${plural(count, '', 'а', 'ов')}`);
      text = parts.join(' · ');
    }

    $('#chat-subtitle').textContent = text;
  }

  // ---------------------------------------------------------------- messages

  function renderMessages({ keepScroll = false } = {}) {
    const host = $('#messages');
    const inner = $('#messages-inner');
    if (!host || !inner) return;

    const conversationId = state.activeId;
    const list = state.messages.get(conversationId) || [];

    const wasNearBottom = isNearBottom(host);
    const previousHeight = host.scrollHeight;
    const previousTop = host.scrollTop;

    inner.innerHTML = '';

    const loadMore = $('#btn-load-more');
    loadMore.classList.toggle('hidden', !state.hasMore.get(conversationId));

    let lastDay = null;
    let previous = null;

    for (const message of list) {
      const created = parseDate(message.createdUtc);
      if (!created) continue;

      if (!sameDay(created, lastDay)) {
        inner.append(el('div', { class: 'day-sep' }, el('span', { text: formatDayLabel(message.createdUtc) })));
        lastDay = created;
        previous = null;
      }

      const continuation = previous
        && previous.authorId === message.authorId
        && previous.type !== 4
        && message.type !== 4
        && (created - parseDate(previous.createdUtc)) < 5 * 60 * 1000;

      inner.append(renderMessage(message, continuation));
      previous = message;
    }

    renderTyping();

    if (keepScroll && !wasNearBottom) {
      host.scrollTop = previousTop + (host.scrollHeight - previousHeight);
    } else if (!keepScroll || wasNearBottom) {
      scrollToBottom(host);
    }

    updateChatSubtitle();
  }

  function renderMessage(message, continuation) {
    const mine = message.authorId === state.me?.id;
    const author = state.users.get(message.authorId);
    const authorName = author?.displayName || author?.nick || 'кто-то';

    if (message.type === 4) {
      return el('div', { class: 'msg system' },
        el('span', { class: 'sys', text: message.text || '' })
      );
    }

    const wrap = el('div', {
      class: `msg${mine ? ' out' : ''}${continuation ? ' continuation' : ''}`,
      dataset: { id: message.id }
    });

    // avatar (hidden visually on continuation rows)
    const avatarCell = el('div', { class: 'msg-avatar' });
    if (!continuation) {
      const avatar = el('span', { class: 'avatar' });
      paintAvatar(avatar, author, authorName);
      avatarCell.append(avatar);
    }
    wrap.append(avatarCell);

    const main = el('div', { class: 'msg-main' });

    if (!continuation && !mine) {
      main.append(el('div', { class: 'msg-author' },
        el('strong', { text: authorName }),
        el('time', { text: formatTime(message.createdUtc) })
      ));
    }

    const bubble = el('div', {
      class: `bubble${message.deleted ? ' deleted' : ''}${hasMedia(message) ? ' media' : ''}`
    });

    if (message.deleted) {
      bubble.append(el('span', { text: 'Сообщение удалено' }));
    } else {
      if (message.replyToId) {
        bubble.append(el('span', {
          class: 'quote',
          onclick: () => jumpToMessage(message.replyToId)
        },
          el('strong', { text: message.replyToAuthor || 'сообщение' }),
          el('span', { text: message.replyToPreview || '' })
        ));
      }

      if (message.attachment) {
        bubble.append(renderAttachment(message));
      }

      if (message.text) {
        const text = el('span', { class: 'msg-text' });
        text.innerHTML = renderText(message.text);
        bubble.append(text);
      }
    }

    main.append(bubble);

    // reactions
    const reactions = message.reactions || {};
    const hasReactions = Object.keys(reactions).length > 0;
    if (!message.deleted && (hasReactions || state.boot?.features?.reactions)) {
      main.append(renderReactions(message));
    }

    // hover meta: time, edit, delete, pin, reply
    const meta = el('div', { class: 'bubble-meta' });
    meta.append(el('span', { text: formatTime(message.createdUtc) }));

    if (message.editedUtc) meta.append(el('span', { class: 'edited', text: 'изм.' }));

    if (!message.deleted) {
      const features = state.boot?.features || {};

      if (features.replies) {
        meta.append(el('button', {
          type: 'button', title: 'Ответить',
          onclick: () => startReply(message)
        }, el('i', { class: 'fa-solid fa-reply' })));
      }

      if (features.pins) {
        meta.append(el('button', {
          type: 'button', title: 'Закрепить',
          onclick: () => togglePin(message)
        }, el('i', { class: 'fa-solid fa-thumbtack' })));
      }

      if (mine && features.edit && message.type === 0) {
        meta.append(el('button', {
          type: 'button', title: 'Изменить',
          onclick: () => startEdit(message)
        }, el('i', { class: 'fa-solid fa-pen' })));
      }

      if (mine && features.delete) {
        meta.append(el('button', {
          type: 'button', title: 'Удалить',
          onclick: () => deleteMessage(message)
        }, el('i', { class: 'fa-solid fa-trash' })));
      }
    }

    main.append(meta);
    wrap.append(main);

    return wrap;
  }

  function hasMedia(message) {
    if (!message.attachment) return false;
    return isImage(message.attachment.mime, message.attachment.name);
  }

  function renderAttachment(message) {
    const att = message.attachment;
    if (!att) return null;

    if (isImage(att.mime, att.name)) {
      return el('img', {
        class: 'att-image',
        src: Api.blobUrl(att.blobId),
        alt: att.name || 'изображение',
        loading: 'lazy',
        onclick: () => window.open(Api.blobUrl(att.blobId), '_blank')
      });
    }

    if (message.type === 2 || isAudio(att.mime, att.name)) {
      return voicePlayer(att);
    }

    return el('a', {
      class: 'att-file',
      href: Api.blobUrl(att.blobId),
      target: '_blank',
      rel: 'noopener',
      download: att.name || 'file'
    },
      el('i', { class: `fa-solid ${fileIcon(att.mime, att.name)}` }),
      el('span', { class: 'att-info' },
        el('span', { class: 'att-name', text: att.name || 'файл' }),
        el('span', { class: 'att-size', text: formatSize(att.size) })
      )
    );
  }

  function voicePlayer(att) {
    const audio = new Audio(Api.blobUrl(att.blobId));
    audio.preload = 'metadata';

    const bars = el('div', { class: 'voice-wave' });
    const heights = buildWaveform(att);

    for (const height of heights) {
      bars.append(el('span', { style: `height:${height}%` }));
    }

    const duration = el('span', { class: 'voice-dur', text: formatDuration(att.durationSec) });

    const button = el('button', {
      class: 'play',
      type: 'button',
      title: 'Слушать',
      onclick: () => {
        if (audio.paused) {
          audio.play().catch(() => toast('Не удалось воспроизвести', 'error'));
        } else {
          audio.pause();
        }
      }
    }, el('i', { class: 'fa-solid fa-play' }));

    audio.addEventListener('play', () => {
      button.innerHTML = '<i class="fa-solid fa-pause"></i>';
      bars.classList.add('playing');
    });

    audio.addEventListener('pause', () => {
      button.innerHTML = '<i class="fa-solid fa-play"></i>';
      bars.classList.remove('playing');
    });

    audio.addEventListener('ended', () => {
      button.innerHTML = '<i class="fa-solid fa-play"></i>';
      bars.classList.remove('playing');
      audio.currentTime = 0;
    });

    audio.addEventListener('loadedmetadata', () => {
      if (Number.isFinite(audio.duration) && audio.duration > 0) {
        duration.textContent = formatDuration(audio.duration);
      }
    });

    audio.addEventListener('timeupdate', () => {
      duration.textContent = formatDuration(audio.currentTime);
    });

    return el('div', { class: 'att-voice' }, button, bars, duration);
  }

  /** Deterministic pseudo-waveform so a voice note always looks the same. */
  function buildWaveform(att) {
    const seedText = (att.blobId || 'x') + (att.durationSec || 0);
    let seed = 0;
    for (let i = 0; i < seedText.length; i++) seed = (seed * 33 + seedText.charCodeAt(i)) | 0;

    const count = 34;
    const heights = [];
    let value = Math.abs(seed) % 97;

    for (let i = 0; i < count; i++) {
      value = (value * 1103515245 + 12345) & 0x7fffffff;
      const noise = (value % 100) / 100;
      const envelope = Math.sin((i / count) * Math.PI) * 0.6 + 0.4;
      heights.push(Math.max(14, Math.round(noise * envelope * 100)));
    }

    return heights;
  }

  function renderReactions(message) {
    const host = el('div', { class: 'reactions' });
    const reactions = message.reactions || {};
    const myId = state.me?.id;

    for (const [emoji, who] of Object.entries(reactions)) {
      if (!Array.isArray(who) || who.length === 0) continue;

      host.append(el('button', {
        class: `reaction${who.includes(myId) ? ' mine' : ''}`,
        type: 'button',
        title: who.map((id) => state.users.get(id)?.displayName || 'кто-то').join(', '),
        onclick: () => toggleReaction(message, emoji)
      },
        el('span', { text: emoji }),
        el('span', { class: 'count', text: String(who.length) })
      ));
    }

    if (state.boot?.features?.reactions) {
      host.append(el('button', {
        class: 'reaction-add',
        type: 'button',
        title: 'Реакция',
        onclick: (event) => openReactionPicker(event.currentTarget, message)
      }, el('i', { class: 'fa-regular fa-face-smile' })));
    }

    return host;
  }

  function openReactionPicker(anchor, message) {
    const pop = $('#emoji-pop');
    pop.innerHTML = '';
    pop.classList.remove('hidden');
    pop.style.gridTemplateColumns = 'repeat(6, 1fr)';

    for (const emoji of QUICK_REACTIONS) {
      pop.append(el('button', {
        type: 'button',
        text: emoji,
        onclick: () => {
          pop.classList.add('hidden');
          toggleReaction(message, emoji);
        }
      }));
    }

    const rect = anchor.getBoundingClientRect();
    const width = 220;
    pop.style.left = Math.max(8, Math.min(rect.left, window.innerWidth - width - 8)) + 'px';
    pop.style.top = Math.min(rect.bottom + 6, window.innerHeight - 120) + 'px';
  }

  function renderTyping() {
    const line = $('#typing-line');
    if (!line) return;

    const map = state.typing.get(state.activeId);
    if (!map || map.size === 0) {
      line.innerHTML = '';
      return;
    }

    const names = Array.from(map.keys())
      .map((id) => state.users.get(id)?.displayName || 'кто-то')
      .slice(0, 3);

    const text = names.length === 1
      ? `${names[0]} печатает`
      : `${names.join(', ')} печатают`;

    line.innerHTML = `${escapeHtml(text)}<span class="dots"><i></i><i></i><i></i></span>`;
  }

  function renderPinned() {
    const bar = $('#pinned-bar');
    const conv = state.conversations.get(state.activeId);

    if (!conv?.pinnedMessageId || !state.boot?.features?.pins) {
      bar.classList.add('hidden');
      return;
    }

    const list = state.messages.get(state.activeId) || [];
    const message = list.find((m) => m.id === conv.pinnedMessageId);

    if (!message) {
      bar.classList.add('hidden');
      return;
    }

    $('#pinned-text').textContent = previewOf(message);
    bar.classList.remove('hidden');
  }

  function jumpToMessage(messageId) {
    const node = $(`.msg[data-id="${CSS.escape(messageId)}"]`);
    if (!node) {
      toast('Сообщение не загружено');
      return;
    }

    node.scrollIntoView({ behavior: 'smooth', block: 'center' });
    node.style.transition = 'background 400ms';
    node.style.background = 'var(--active)';
    setTimeout(() => { node.style.background = ''; }, 900);
  }

  // ------------------------------------------------------------------ actions

  async function sendMessage() {
    const input = $('#composer-input');
    const text = input.value.trim();
    const conversationId = state.activeId;

    if (!conversationId) return;

    // editing an existing message takes the same path
    if (state.editing) {
      await submitEdit(text);
      return;
    }

    const attachment = state.pendingFiles;

    if (!text && !attachment) return;

    const clientId = crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random();

    input.value = '';
    autoGrow(input);
    state.pendingFiles = null;
    $('#btn-send').disabled = true;

    const replyTo = state.replyTo;
    state.replyTo = null;
    hideReplyBar();

    try {
      const message = await Api.send(conversationId, {
        text,
        clientId,
        replyToId: replyTo?.id || '',
        attachmentId: attachment?.id || '',
        type: attachment ? (attachment.mime?.startsWith('image/') ? 3
              : attachment.mime?.startsWith('audio/') ? 2 : 1) : 0
      });

      if (message) {
        const list = state.messages.get(conversationId) || [];
        state.messages.set(conversationId, mergeMessages(list, [message]));
        updateConversationFromMessage(message);
        renderConversations();

        if (conversationId === state.activeId) {
          renderMessages({ keepScroll: false });
        }
      }

      Sound.play('sent');
    } catch (err) {
      toast(err.message || 'Не удалось отправить', 'error');
      Sound.play('error');
      input.value = text;
      autoGrow(input);
    } finally {
      $('#btn-send').disabled = false;
      input.focus();
    }
  }

  function startReply(message) {
    state.replyTo = message;
    $('#reply-author').textContent = state.users.get(message.authorId)?.displayName || 'кто-то';
    $('#reply-preview').textContent = previewOf(message);
    $('#reply-bar').classList.remove('hidden');
    $('#composer-input').focus();
  }

  function hideReplyBar() {
    $('#reply-bar').classList.add('hidden');
  }

  function startEdit(message) {
    state.editing = message;
    const input = $('#composer-input');
    input.value = message.text || '';
    autoGrow(input);
    input.focus();
    $('#btn-send').title = 'Сохранить';
  }

  async function submitEdit(text) {
    const message = state.editing;
    state.editing = null;
    $('#btn-send').title = 'Отправить (Enter)';

    const input = $('#composer-input');
    input.value = '';
    autoGrow(input);

    if (!message) return;

    if (!text) {
      toast('Пустое сообщение', 'error');
      return;
    }

    try {
      const updated = await Api.edit(message.id, text);
      if (updated) replaceMessage(updated);
      renderMessages({ keepScroll: true });
      toast('Изменено', 'success', 1600);
    } catch (err) {
      toast(err.message || 'Не удалось изменить', 'error');
    }
  }

  async function deleteMessage(message) {
    if (!confirm('Удалить сообщение?')) return;

    try {
      await Api.remove(message.id);
      message.deleted = true;
      message.text = '';
      message.attachment = null;
      message.reactions = {};
      renderMessages({ keepScroll: true });
    } catch (err) {
      toast(err.message || 'Не удалось удалить', 'error');
    }
  }

  async function toggleReaction(message, emoji) {
    try {
      const reactions = await Api.react(message.id, emoji);
      message.reactions = reactions || {};
      renderMessages({ keepScroll: true });
      Sound.play('pop');
    } catch (err) {
      toast(err.message || 'Не удалось поставить реакцию', 'error');
    }
  }

  async function togglePin(message) {
    try {
      const result = await Api.pin(message.id);
      const conv = state.conversations.get(message.conversationId);
      if (conv) conv.pinnedMessageId = result?.pinnedMessageId || '';
      renderPinned();
      toast(conv?.pinnedMessageId ? 'Закреплено' : 'Откреплено', 'success', 1600);
    } catch (err) {
      toast(err.message || 'Не удалось закрепить', 'error');
    }
  }

  function markRead(conversationId, seq) {
    if (!conversationId || !seq) return;
    if ((state.lastReadSent.get(conversationId) || 0) >= seq) return;

    state.lastReadSent.set(conversationId, seq);

    const conv = state.conversations.get(conversationId);
    if (conv) {
      conv.unread = 0;
      conv.lastReadSeq = seq;
    }

    Api.read(conversationId, seq).catch(() => { /* best effort */ });
  }

  // ------------------------------------------------------------------ typing

  function sendTyping() {
    if (!state.boot?.features?.typing || !state.activeId) return;
    if (!state.socket || state.socket.readyState !== WebSocket.OPEN) return;

    const now = Date.now();
    if (now - state.typingSent < 2500) return;

    state.typingSent = now;
    state.socket.send(JSON.stringify({ t: 'typing', conversationId: state.activeId, on: true }));

    clearTimeout(state.typingTimer);
    state.typingTimer = setTimeout(stopTyping, 4000);
  }

  function stopTyping() {
    if (!state.activeId || !state.socket || state.socket.readyState !== WebSocket.OPEN) return;
    state.socket.send(JSON.stringify({ t: 'typing', conversationId: state.activeId, on: false }));
    state.typingSent = 0;
  }

  // ------------------------------------------------------------------ upload

  async function handleFile(file) {
    if (!file) return;
    if (!state.activeId) return;

    const maxMb = state.boot?.maxUploadMb || 64;
    if (file.size > maxMb * 1024 * 1024) {
      toast(`Файл больше ${maxMb} МБ`, 'error');
      return;
    }

    const progress = $('#upload-progress');
    const bar = $('#upload-bar');
    const label = $('#upload-label');

    progress.classList.remove('hidden');
    bar.style.width = '0%';
    label.textContent = `Загрузка ${file.name}`;

    try {
      const result = await Api.upload(file, (ratio) => {
        bar.style.width = Math.round(ratio * 100) + '%';
      });

      bar.style.width = '100%';
      label.textContent = 'Готово';
      state.pendingFiles = result;

      // images and voice notes send immediately: an extra click adds nothing
      await sendMessage();
    } catch (err) {
      toast(err.message || 'Не удалось загрузить', 'error');
    } finally {
      setTimeout(() => progress.classList.add('hidden'), 600);
    }
  }

  // -------------------------------------------------------------- recording

  async function startRecording() {
    if (state.recorder) return;
    if (!state.boot?.features?.voice) {
      toast('Голосовые отключены на сервере');
      return;
    }

    if (!navigator.mediaDevices?.getUserMedia) {
      toast('Микрофон недоступен в этом окне', 'error');
      return;
    }

    let stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch {
      toast('Нет доступа к микрофону', 'error');
      return;
    }

    const mime = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg']
      .find((type) => window.MediaRecorder?.isTypeSupported?.(type)) || '';

    let recorder;
    try {
      recorder = new MediaRecorder(stream, mime ? { mimeType: mime } : undefined);
    } catch {
      stream.getTracks().forEach((t) => t.stop());
      toast('Запись не поддерживается', 'error');
      return;
    }

    const chunks = [];
    const started = Date.now();
    const waveHost = $('#rec-wave');
    waveHost.innerHTML = '';

    // A live level meter drawn from an analyser keeps the recording honest.
    const audioCtx = new (window.AudioContext || window.webkitAudioContext)();
    const source = audioCtx.createMediaStreamSource(stream);
    const analyser = audioCtx.createAnalyser();
    analyser.fftSize = 256;
    source.connect(analyser);

    const buffer = new Uint8Array(analyser.frequencyBinCount);
    const bars = [];
    for (let i = 0; i < 28; i++) {
      const span = el('span', { style: 'height:6px' });
      waveHost.append(span);
      bars.push(span);
    }

    const meter = setInterval(() => {
      analyser.getByteFrequencyData(buffer);
      const level = Math.max(...buffer.slice(0, 24)) / 255;
      const height = Math.max(6, Math.round(level * 26));
      const span = bars.shift();
      span.style.height = height + 'px';
      waveHost.append(span);
      bars.push(span);
    }, 90);

    const clock = setInterval(() => {
      $('#rec-time').textContent = formatDuration((Date.now() - started) / 1000);
    }, 200);

    recorder.addEventListener('dataavailable', (event) => {
      if (event.data.size > 0) chunks.push(event.data);
    });

    recorder.addEventListener('stop', async () => {
      clearInterval(meter);
      clearInterval(clock);
      stream.getTracks().forEach((track) => track.stop());
      audioCtx.close().catch(() => {});

      const seconds = (Date.now() - started) / 1000;
      const type = recorder.mimeType || 'audio/webm';
      const blob = new Blob(chunks, { type });

      cleanupRecorder();

      if (seconds < 0.6 || blob.size < 512) {
        toast('Слишком короткая запись');
        return;
      }

      const extension = type.includes('ogg') ? 'ogg' : 'webm';
      const file = new File([blob], `voice-${Date.now()}.${extension}`, { type });

      await uploadVoice(file, seconds);
    });

    state.recorder = { recorder, started };
    $('#recorder').classList.remove('hidden');
    $('#composer-input').disabled = true;
    Sound.recordStart();

    recorder.start(250);

    // Hard stop at the server's limit so a stuck finger cannot record forever.
    const maxSec = state.boot?.maxVoiceSec || 300;
    setTimeout(() => {
      if (state.recorder) stopRecording();
    }, maxSec * 1000);
  }

  function cleanupRecorder() {
    state.recorder = null;
    $('#recorder').classList.add('hidden');
    $('#composer-input').disabled = false;
    $('#rec-time').textContent = '0:00';
    $('#rec-wave').innerHTML = '';
  }

  function stopRecording(send = true) {
    if (!state.recorder) return;

    if (!send) {
      state.recorder.recorder.addEventListener('stop', () => cleanupRecorder(), { once: true });
    }

    try {
      state.recorder.recorder.stop();
    } catch {
      cleanupRecorder();
    }

    Sound.recordStop();
  }

  async function uploadVoice(file, duration) {
    const progress = $('#upload-progress');
    const bar = $('#upload-bar');
    const label = $('#upload-label');

    progress.classList.remove('hidden');
    bar.style.width = '0%';
    label.textContent = 'Отправка голосового';

    try {
      const result = await Api.upload(file, (ratio) => {
        bar.style.width = Math.round(ratio * 100) + '%';
      }, duration);

      const conversationId = state.activeId;
      const message = await Api.send(conversationId, {
        text: '',
        clientId: crypto.randomUUID ? crypto.randomUUID() : String(Date.now()),
        attachmentId: result.id,
        type: 2
      });

      if (message) {
        const list = state.messages.get(conversationId) || [];
        state.messages.set(conversationId, mergeMessages(list, [message]));
        updateConversationFromMessage(message);
        renderConversations();
        renderMessages({ keepScroll: false });
      }

      Sound.play('sent');
    } catch (err) {
      toast(err.message || 'Не удалось отправить голосовое', 'error');
    } finally {
      setTimeout(() => progress.classList.add('hidden'), 600);
    }
  }

  // ------------------------------------------------------------------ search

  const runSearch = debounce(async (query) => {
    const host = $('#conv-list');

    if (!query || query.length < 2) {
      state.searchMode = false;
      renderConversations();
      return;
    }

    try {
      const result = await Api.search(query);
      state.searchMode = true;
      renderSearchResults(result);
    } catch (err) {
      toast(err.message || 'Поиск не удался', 'error');
    }
  }, 320);

  function renderSearchResults(result) {
    const host = $('#conv-list');
    host.innerHTML = '';

    const messages = result.messages || [];
    const titles = result.titles || {};
    const users = result.users || {};

    for (const [id, user] of Object.entries(users)) state.users.set(id, user);

    if (messages.length === 0) {
      host.append(el('p', {
        style: 'padding:20px 12px;color:var(--text-faint);font-size:13px;text-align:center',
        text: 'Ничего не найдено'
      }));
      return;
    }

    for (const message of messages) {
      const author = state.users.get(message.authorId);
      const row = el('button', {
        class: 'conv',
        type: 'button',
        onclick: async () => {
          $('#search-input').value = '';
          $('#search-clear').classList.add('hidden');
          state.searchMode = false;
          await openConversation(message.conversationId);
          setTimeout(() => jumpToMessage(message.id), 500);
        }
      },
        el('span', { class: 'conv-emoji' }, el('i', { class: 'fa-solid fa-magnifying-glass', style: 'font-size:14px' })),
        el('div', { class: 'conv-body' },
          el('div', { class: 'conv-top' },
            el('span', { class: 'conv-name', text: titles[message.conversationId] || 'Диалог' }),
            el('span', { class: 'conv-time', text: formatListTime(message.createdUtc) })
          ),
          el('div', { class: 'conv-bottom' },
            el('span', { class: 'conv-preview', text: `${author?.displayName || 'кто-то'}: ${message.text || ''}` })
          )
        )
      );

      host.append(row);
    }
  }

  // ----------------------------------------------------------- notifications

  function notify(message) {
    if (state.me?.notifications === false) return;
    if (document.hasFocus() && state.activeId === message.conversationId) return;
    if (!('Notification' in window)) return;
    if (Notification.permission !== 'granted') return;

    const conv = state.conversations.get(message.conversationId);
    const author = state.users.get(message.authorId);

    try {
      const notification = new Notification(conv?.title || 'Kroshka', {
        body: `${author?.displayName || 'кто-то'}: ${previewOf(message)}`,
        icon: '/assets/img/logo-192.png',
        tag: message.conversationId,
        silent: true
      });

      notification.onclick = () => {
        window.focus();
        openConversation(message.conversationId);
        notification.close();
      };
    } catch { /* notification blocked */ }
  }

  function updateBadge() {
    const total = Array.from(state.conversations.values())
      .reduce((sum, conv) => sum + (conv.unread || 0), 0);

    if (window.desktopBridge?.setBadge) {
      window.desktopBridge.setBadge(total);
    }

    document.title = total > 0
      ? `(${total}) ${state.boot?.serverName || 'Kroshka'}`
      : (state.boot?.serverName || 'Kroshka');
  }

  // ------------------------------------------------------------------ storage

  function storeLastConversation(id) {
    try { localStorage.setItem('kroshka.last', id); } catch { /* ignore */ }
  }

  function readLastConversation() {
    try { return localStorage.getItem('kroshka.last'); } catch { return null; }
  }

  // ------------------------------------------------------------------- drawer

  async function openDrawer() {
    $('#drawer').classList.remove('hidden');
    $('#drawer-scrim').classList.remove('hidden');
    renderSessions();
    renderServerInfo();
    renderThemePicker();
  }

  function closeDrawer() {
    $('#drawer').classList.add('hidden');
    $('#drawer-scrim').classList.add('hidden');
  }

  function renderThemePicker() {
    $$('#theme-picker .theme-swatch').forEach((button) => {
      button.classList.toggle('active', button.dataset.theme === state.theme);
    });
  }

  async function renderSessions() {
    const host = $('#sessions-list');
    host.innerHTML = '<p style="font-size:12.5px;color:var(--text-faint)">Загрузка…</p>';

    try {
      const result = await Api.sessions();
      host.innerHTML = '';

      for (const session of result.sessions || []) {
        const isCurrent = session.device === Api.deviceName() && session.remember;

        host.append(el('div', { class: 'session' },
          el('i', { class: `fa-solid ${/Android|iOS/.test(session.device) ? 'fa-mobile-screen' : 'fa-desktop'}` }),
          el('div', { class: 'session-body' },
            el('strong', { text: session.device || 'Устройство' }),
            el('small', { text: `${session.ip || ''} · ${formatListTime(session.lastUsedUtc)}` })
          ),
          isCurrent
            ? el('span', { style: 'font-size:11px;color:var(--accent)', text: 'это я' })
            : el('button', {
                class: 'icon-btn tiny',
                type: 'button',
                title: 'Завершить',
                onclick: async () => {
                  try {
                    await Api.revokeSession(session.id);
                    renderSessions();
                    toast('Сессия завершена', 'success');
                  } catch (err) {
                    toast(err.message || 'Не удалось', 'error');
                  }
                }
              }, el('i', { class: 'fa-solid fa-xmark' }))
        ));
      }

      if ((result.sessions || []).length === 0) {
        host.append(el('p', { style: 'font-size:12.5px;color:var(--text-faint)', text: 'Нет активных сессий' }));
      }
    } catch {
      host.innerHTML = '<p style="font-size:12.5px;color:var(--text-faint)">Не удалось получить список</p>';
    }
  }

  function renderServerInfo() {
    const host = $('#server-info');
    host.innerHTML = '';

    const boot = state.boot || {};
    const rows = [
      ['Сервер', boot.serverName || '—'],
      ['Версия', boot.version || '—'],
      ['Адрес', Api.getServer() || location.origin],
      ['Участников', String(boot.userCount || 0)],
      ['Режим входа', boot.authMode === 'password' ? 'пароль семьи'
                    : boot.authMode === 'invite' ? 'по приглашению'
                    : 'открытый'],
      ['Лимит файла', `${boot.maxUploadMb || 64} МБ`]
    ];

    if (boot.footer) rows.push(['', boot.footer]);

    for (const [key, value] of rows) {
      host.append(el('dt', { text: key }), el('dd', { text: value }));
    }
  }

  async function saveProfile() {
    const displayName = $('#profile-name').value.trim();
    const bio = $('#profile-bio').value.trim();

    try {
      const updated = await Api.updateProfile({ displayName, bio });
      if (updated) {
        state.me = updated;
        state.users.set(updated.id, updated);
        renderMe();
        renderConversations();
        renderMessages({ keepScroll: true });
      }
      toast('Сохранено', 'success', 1600);
    } catch (err) {
      toast(err.message || 'Не удалось сохранить', 'error');
    }
  }

  // -------------------------------------------------------------------- rooms

  function openRoomModal() {
    $('#room-name').value = '';
    $('#room-topic').value = '';
    $('#room-private').checked = false;

    const host = $('#room-members');
    host.innerHTML = '';

    for (const user of state.users.values()) {
      if (user.id === state.me?.id) continue;

      const row = el('button', {
        class: 'member-row',
        type: 'button',
        dataset: { id: user.id },
        onclick: (event) => event.currentTarget.classList.toggle('picked')
      });

      const avatar = el('span', { class: 'avatar sm' });
      paintAvatar(avatar, user, user.nick);

      row.append(avatar,
        el('span', { class: 'name', text: user.displayName || user.nick }),
        el('i', { class: 'fa-solid fa-check check' }));

      host.append(row);
    }

    showModal('#modal-room');
    setTimeout(() => $('#room-name').focus(), 80);
  }

  async function createRoom() {
    const name = $('#room-name').value.trim();
    if (!name) {
      toast('Введите название', 'error');
      return;
    }

    const members = $$('#room-members .member-row.picked').map((row) => row.dataset.id);

    try {
      const room = await Api.createRoom({
        name,
        topic: $('#room-topic').value.trim(),
        isPrivate: $('#room-private').checked,
        members
      });

      hideModal('#modal-room');
      onRoomChanged(room);
      await openConversation(room.id);
      toast('Комната создана', 'success');
    } catch (err) {
      toast(err.message || 'Не удалось создать', 'error');
    }
  }

  function openUsersModal() {
    const host = $('#user-list');
    host.innerHTML = '';

    const list = Array.from(state.users.values())
      .filter((user) => user.id !== state.me?.id)
      .sort((a, b) => (a.displayName || '').localeCompare(b.displayName || ''));

    for (const user of list) {
      const row = el('button', {
        class: 'user-row',
        type: 'button',
        onclick: async () => {
          hideModal('#modal-users');
          try {
            const dm = await Api.openDm(user.id);
            onDmCreated(dm);
          } catch (err) {
            toast(err.message || 'Не удалось открыть личку', 'error');
          }
        }
      });

      const avatar = el('span', { class: 'avatar sm' });
      paintAvatar(avatar, user, user.nick);

      row.append(avatar,
        el('span', { class: 'name', text: user.displayName || user.nick }),
        el('span', { class: `online-dot${state.online.has(user.id) ? '' : ' off'}` }));

      host.append(row);
    }

    if (list.length === 0) {
      host.append(el('p', { style: 'font-size:13px;color:var(--text-faint)', text: 'Пока никого нет' }));
    }

    showModal('#modal-users');
  }

  // ------------------------------------------------------------------- modals

  function showModal(selector) {
    $('#modal-scrim').classList.remove('hidden');
    $(selector).classList.remove('hidden');
  }

  function hideModal(selector) {
    $(selector).classList.add('hidden');
    if ($$('.modal:not(.hidden)').length === 0) {
      $('#modal-scrim').classList.add('hidden');
    }
  }

  function hideAllModals() {
    $$('.modal').forEach((modal) => modal.classList.add('hidden'));
    $('#modal-scrim').classList.add('hidden');
    $('#emoji-pop').classList.add('hidden');
  }

  // -------------------------------------------------------------------- emoji

  function toggleEmojiPicker() {
    const pop = $('#emoji-pop');
    if (!pop.classList.contains('hidden')) {
      pop.classList.add('hidden');
      return;
    }

    pop.style.gridTemplateColumns = 'repeat(8, 1fr)';
    buildEmojiPicker(pop, (emoji) => {
      const input = $('#composer-input');
      const start = input.selectionStart ?? input.value.length;
      const end = input.selectionEnd ?? input.value.length;
      input.value = input.value.slice(0, start) + emoji + input.value.slice(end);
      input.selectionStart = input.selectionEnd = start + emoji.length;
      input.focus();
      autoGrow(input);
    });

    const button = $('#btn-emoji');
    const rect = button.getBoundingClientRect();
    const width = 300;
    pop.style.left = Math.max(8, Math.min(rect.left - width + rect.width, window.innerWidth - width - 8)) + 'px';
    pop.style.top = Math.max(8, rect.top - 268) + 'px';
    pop.classList.remove('hidden');
  }

  // ---------------------------------------------------------------- listeners

  function bind() {
    $('#login-form').addEventListener('submit', doLogin);

    $('#search-input').addEventListener('input', (event) => {
      const query = event.target.value.trim();
      $('#search-clear').classList.toggle('hidden', query.length === 0);
      runSearch(query);
    });

    $('#search-clear').addEventListener('click', () => {
      $('#search-input').value = '';
      $('#search-clear').classList.add('hidden');
      state.searchMode = false;
      renderConversations();
    });

    $('#btn-new-room').addEventListener('click', openRoomModal);
    $('#btn-create-room').addEventListener('click', createRoom);

    $('#btn-menu').addEventListener('click', () => {
      openUsersModal();
    });

    $('#btn-profile').addEventListener('click', openDrawer);
    $('#btn-drawer-close').addEventListener('click', closeDrawer);
    $('#drawer-scrim').addEventListener('click', closeDrawer);
    $('#modal-scrim').addEventListener('click', hideAllModals);
    $$('[data-close-modal]').forEach((button) => {
      button.addEventListener('click', (event) => {
        const modal = event.currentTarget.closest('.modal');
        if (modal) hideModal('#' + modal.id);
      });
    });

    $('#btn-save-profile').addEventListener('click', saveProfile);

    $('#profile-avatar').addEventListener('click', () => $('#avatar-input').click());
    $('#avatar-input').addEventListener('change', async (event) => {
      const file = event.target.files?.[0];
      if (!file) return;

      try {
        const result = await Api.uploadAvatar(file);
        if (result?.avatarBlob) {
          state.me.avatarBlob = result.avatarBlob;
          renderMe();
          renderConversations();
          toast('Аватар обновлён', 'success');
        }
      } catch (err) {
        toast(err.message || 'Не удалось загрузить аватар', 'error');
      } finally {
        event.target.value = '';
      }
    });

    $$('#theme-picker .theme-swatch').forEach((button) => {
      button.addEventListener('click', () => {
        state.theme = button.dataset.theme;
        setTheme(state.theme);
        renderThemePicker();
        Api.updateProfile({ theme: state.theme }).catch(() => {});
        Sound.play('pop');
      });
    });

    $('#set-sound').addEventListener('change', (event) => {
      Sound.setEnabled(event.target.checked);
      Api.updateProfile({ sound: event.target.checked }).catch(() => {});
      if (event.target.checked) Sound.play('sent');
    });

    $('#set-notifications').addEventListener('change', async (event) => {
      const enabled = event.target.checked;

      if (enabled && 'Notification' in window && Notification.permission === 'default') {
        try { await Notification.requestPermission(); } catch { /* ignored */ }
      }

      Api.updateProfile({ notifications: enabled }).catch(() => {});
    });

    $('#btn-logout').addEventListener('click', async () => {
      if (!confirm('Выйти на этом устройстве?')) return;
      await Api.logout();
      location.reload();
    });

    // composer
    const input = $('#composer-input');

    input.addEventListener('input', () => {
      autoGrow(input);
      sendTyping();
      $('#btn-send').disabled = input.value.trim().length === 0 && !state.pendingFiles;
    });

    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter' && !event.shiftKey) {
        event.preventDefault();
        sendMessage();
      }
      if (event.key === 'Escape') {
        if (state.editing) {
          state.editing = null;
          input.value = '';
          autoGrow(input);
          $('#btn-send').title = 'Отправить (Enter)';
        }
        if (state.replyTo) {
          state.replyTo = null;
          hideReplyBar();
        }
      }
    });

    $('#btn-send').addEventListener('click', sendMessage);

    $('#btn-cancel-reply').addEventListener('click', () => {
      state.replyTo = null;
      hideReplyBar();
    });

    $('#btn-emoji').addEventListener('click', (event) => {
      event.stopPropagation();
      toggleEmojiPicker();
    });

    document.addEventListener('click', (event) => {
      const pop = $('#emoji-pop');
      if (!pop.classList.contains('hidden') &&
          !pop.contains(event.target) &&
          !event.target.closest('#btn-emoji')) {
        pop.classList.add('hidden');
      }
    });

    $('#btn-attach').addEventListener('click', () => {
      const picker = document.createElement('input');
      picker.type = 'file';
      picker.addEventListener('change', () => handleFile(picker.files?.[0]));
      picker.click();
    });

    $('#btn-voice').addEventListener('click', startRecording);
    $('#btn-rec-cancel').addEventListener('click', () => stopRecording(false));
    $('#btn-rec-send').addEventListener('click', () => stopRecording(true));

    $('#btn-load-more').addEventListener('click', () => {
      if (!state.activeId) return;
      const list = state.messages.get(state.activeId) || [];
      const oldest = list.length > 0 ? list[0].seq : 0;
      loadPage(state.activeId, oldest);
    });

    $('#btn-back').addEventListener('click', () => {
      $('#app').classList.remove('in-chat');
    });

    $('#btn-unpin').addEventListener('click', () => {
      const conv = state.conversations.get(state.activeId);
      if (!conv?.pinnedMessageId) return;

      const list = state.messages.get(state.activeId) || [];
      const message = list.find((m) => m.id === conv.pinnedMessageId);
      if (message) togglePin(message);
    });

    $('#btn-search-in').addEventListener('click', () => {
      $('#search-input').focus();
    });

    $('#btn-room-settings').addEventListener('click', () => {
      const conv = state.conversations.get(state.activeId);
      if (!conv) return;

      if (conv.kind === 1) return;

      const name = prompt('Название комнаты', conv.title || '');
      if (name === null) return;

      Api.updateRoom(conv.id, { name: name.trim() }).then((room) => {
        onRoomChanged(room);
        toast('Сохранено', 'success', 1600);
      }).catch((err) => toast(err.message || 'Не удалось', 'error'));
    });

    // drag and drop onto the message area
    const dropZone = $('#chat');

    ['dragover', 'dragenter'].forEach((name) => {
      dropZone.addEventListener(name, (event) => {
        if (!state.activeId) return;
        event.preventDefault();
        dropZone.style.background = 'var(--active)';
      });
    });

    ['dragleave', 'drop'].forEach((name) => {
      dropZone.addEventListener(name, (event) => {
        event.preventDefault();
        dropZone.style.background = '';
      });
    });

    dropZone.addEventListener('drop', (event) => {
      const file = event.dataTransfer?.files?.[0];
      if (file) handleFile(file);
    });

    // paste an image straight into the composer
    input.addEventListener('paste', (event) => {
      const items = event.clipboardData?.items;
      if (!items) return;

      for (const item of items) {
        if (item.kind !== 'file') continue;
        const file = item.getAsFile();
        if (file) {
          event.preventDefault();
          handleFile(file);
          return;
        }
      }
    });

    // keep read markers honest while scrolling
    $('#messages').addEventListener('scroll', throttle(() => {
      if (!state.activeId) return;
      const conv = state.conversations.get(state.activeId);
      if (conv && isNearBottom($('#messages'))) markRead(state.activeId, conv.lastSeq);
    }, 500));

    window.addEventListener('focus', () => {
      if (state.activeId) {
        const conv = state.conversations.get(state.activeId);
        if (conv) markRead(state.activeId, conv.lastSeq);
      }
      updateBadge();
    });

    window.addEventListener('beforeunload', () => {
      if (state.socket) state.socket.close();
    });
  }

  // --------------------------------------------------------------------- init

  function start() {
    bind();
    boot().catch((err) => {
      console.error(err);
      showLoginFatal('Ошибка запуска: ' + (err?.message || err));
    });
  }

  return { start, state, openConversation, sortedConversations, renderConversations, conversationRow };
})();

document.addEventListener('DOMContentLoaded', () => App.start());
