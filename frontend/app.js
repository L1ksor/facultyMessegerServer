(() => {
  "use strict";

  const STORAGE_KEY = "fm_debug_state_v2";

  const state = {
    apiBase: "http://localhost:5132",
    sessions: [],
    activeEmail: null,
    chats: [],
    activeChatId: null,
    activeChatDetails: null,
    messages: [],
    groupsCache: [],
    directoryCache: [],
    hub: null,
    pendingFile: null,
    mediaRecorder: null,
    audioChunks: [],
    recordStartAt: 0,
    logCount: 0,
    blobUrls: new Map()
  };

  const $ = (id) => document.getElementById(id);

  function esc(val) {
    return String(val ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function activeSession() {
    return state.sessions.find((s) => s.email === state.activeEmail) || null;
  }

  function currentUser() {
    return activeSession()?.user || null;
  }

  function hasPerm(permName) {
    return Boolean(state.activeChatDetails?.myPermissions?.includes(permName));
  }

  function saveState() {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({
      apiBase: state.apiBase,
      sessions: state.sessions,
      activeEmail: state.activeEmail
    }));
  }

  function loadState() {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return;
      const parsed = JSON.parse(raw);
      if (parsed.apiBase) state.apiBase = parsed.apiBase;
      if (Array.isArray(parsed.sessions)) state.sessions = parsed.sessions;
      if (parsed.activeEmail) state.activeEmail = parsed.activeEmail;
    } catch { /* ignore */ }
  }

  function toast(msg, kind = "ok") {
    const el = document.createElement("div");
    el.className = `toast ${kind}`;
    el.textContent = msg;
    $("toastContainer").appendChild(el);
    setTimeout(() => el.remove(), 4500);
  }

  function addLog(text, kind = "http") {
    state.logCount++;
    $("logCounter").textContent = String(state.logCount);
    const list = $("logList");
    const entry = document.createElement("div");
    entry.className = `log-entry ${kind}`;
    const time = new Date().toLocaleTimeString("ru-RU");
    entry.textContent = `[${time}] ${text}`;
    list.prepend(entry);
    if (list.children.length > 150) list.lastChild.remove();
  }

  async function api(path, options = {}) {
    const url = `${state.apiBase.replace(/\/+$/, "")}${path}`;
    const headers = new Headers(options.headers || {});
    const sess = activeSession();
    if (sess?.accessToken && !headers.has("Authorization")) {
      headers.set("Authorization", `Bearer ${sess.accessToken}`);
    }
    if (options.body && !(options.body instanceof FormData) && !headers.has("Content-Type")) {
      headers.set("Content-Type", "application/json");
    }

    const method = options.method || "GET";
    const resp = await fetch(url, { ...options, headers });
    const text = await resp.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }

    addLog(`${method} ${path} -> ${resp.status}`, resp.ok ? "http" : "err");

    if (!resp.ok) {
      const errMsg = (data && (data.detail || data.title)) || `HTTP ${resp.status}`;
      throw new Error(errMsg);
    }
    return data;
  }

  function formatSize(bytes) {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
  }

  function formatTime(iso) {
    return new Date(iso).toLocaleString("ru-RU", {
      day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit"
    });
  }

  // ---------- Sessions & SignalR ----------
  function upsertSession(authResp) {
    const item = {
      email: authResp.user.email,
      accessToken: authResp.accessToken,
      mustChangePassword: authResp.mustChangePassword,
      user: authResp.user
    };
    const idx = state.sessions.findIndex((s) => s.email === item.email);
    if (idx >= 0) state.sessions[idx] = item;
    else state.sessions.push(item);
    state.activeEmail = item.email;
    saveState();
  }

  async function switchSession(email) {
    state.activeEmail = email;
    state.activeChatId = null;
    state.activeChatDetails = null;
    state.messages = [];
    saveState();
    await onSessionChanged();
  }

  function removeSession(email) {
    state.sessions = state.sessions.filter((s) => s.email !== email);
    if (state.activeEmail === email) {
      state.activeEmail = state.sessions[0]?.email || null;
      state.activeChatId = null;
      state.activeChatDetails = null;
      state.messages = [];
    }
    saveState();
    onSessionChanged();
  }

  function renderTopBar() {
    $("apiBaseInput").value = state.apiBase;
    const container = $("sessionSwitcher");
    container.innerHTML = "";

    for (const s of state.sessions) {
      const chip = document.createElement("div");
      chip.className = `session-chip ${s.email === state.activeEmail ? "active" : ""}`;
      const label = document.createElement("span");
      label.textContent = `${s.user.fullName.split(" ")[0]} (${s.user.role})`;
      label.title = s.email;
      label.onclick = () => switchSession(s.email);

      const close = document.createElement("span");
      close.className = "chip-close";
      close.textContent = "×";
      close.onclick = (e) => { e.stopPropagation(); removeSession(s.email); };

      chip.appendChild(label);
      chip.appendChild(close);
      container.appendChild(chip);
    }

    const sess = activeSession();
    if (sess) {
      const grp = sess.user.academicGroupName ? ` · гр. ${sess.user.academicGroupName}` : "";
      $("currentUserBadge").textContent = `${sess.user.fullName} [${sess.user.role}${grp}]`;
      $("mustChangeBanner").classList.toggle("hidden", !sess.mustChangePassword);
    } else {
      $("currentUserBadge").textContent = "Не авторизован";
      $("mustChangeBanner").classList.add("hidden");
    }
  }

  async function stopSignalR() {
    if (state.hub) {
      try { await state.hub.stop(); } catch { /* ignore */ }
      state.hub = null;
    }
    $("signalrStatus").textContent = "SignalR: отключён";
    $("signalrStatus").className = "status-pill status-off";
  }

  async function startSignalR() {
    await stopSignalR();
    const sess = activeSession();
    if (!sess || sess.mustChangePassword || typeof signalR === "undefined") return;

    const hubUrl = `${state.apiBase.replace(/\/+$/, "")}/hubs/chat`;
    const conn = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, { accessTokenFactory: () => activeSession()?.accessToken || "" })
      .withAutomaticReconnect()
      .build();

    conn.on("ReceiveMessage", (msg) => {
      addLog(`WS ReceiveMessage chat=${msg.chatId} sender=${msg.senderName}`, "ws");
      if (msg.chatId === state.activeChatId) {
        if (!state.messages.some((m) => m.id === msg.id)) {
          state.messages.push(msg);
          renderMessages();
        }
      } else if (!state.chats.some((c) => c.id === msg.chatId)) {
        loadChats();
      }
    });

    conn.on("MessagePinned", (ev) => {
      addLog(`WS MessagePinned msg=${ev.messageId} pinned=${ev.isPinned}`, "ws");
      const m = state.messages.find((x) => x.id === ev.messageId);
      if (m) { m.isPinned = ev.isPinned; renderMessages(); }
    });

    conn.on("MessageDeleted", (ev) => {
      addLog(`WS MessageDeleted msg=${ev.messageId}`, "ws");
      state.messages = state.messages.filter((x) => x.id !== ev.messageId);
      renderMessages();
    });

    conn.on("MessageAcknowledged", (ev) => {
      addLog(`WS MessageAcknowledged msg=${ev.messageId} user=${ev.userFullName}`, "ws");
      const m = state.messages.find((x) => x.id === ev.messageId);
      if (m) {
        m.acknowledgementsCount = ev.totalAcknowledgedCount;
        if (ev.userId === currentUser()?.id) m.isAcknowledgedByMe = true;
        renderMessages();
      }
    });

    conn.on("PollUpdated", (poll) => {
      addLog(`WS PollUpdated poll=${poll.id} totalVotes=${poll.totalVotes}`, "ws");
      const m = state.messages.find((x) => x.poll && x.poll.id === poll.id);
      if (m) { m.poll = poll; renderMessages(); }
    });

    conn.on("ChatUpdated", (ev) => {
      addLog(`WS ChatUpdated chat=${ev.chatId}`, "ws");
      loadChats();
      if (ev.chatId === state.activeChatId) openChat(ev.chatId);
    });

    conn.on("ChatRolesUpdated", (ev) => {
      addLog(`WS ChatRolesUpdated chat=${ev.chatId}`, "ws");
      if (ev.chatId === state.activeChatId) openChat(ev.chatId);
    });

    conn.on("ChatGroupsBound", (ev) => {
      addLog(`WS ChatGroupsBound chat=${ev.chatId}`, "ws");
      loadChats();
      if (ev.chatId === state.activeChatId) openChat(ev.chatId);
    });

    conn.on("NewMessageNotification", (ev) => {
      addLog(`WS NewMessageNotification [${ev.chatTitle}] ${ev.senderName}: ${ev.previewText}`, "ws");
      toast(`🔔 [${ev.chatTitle}] ${ev.senderName}: ${ev.previewText}`, "ok");
    });

    conn.onreconnected(() => {
      $("signalrStatus").textContent = "SignalR: подключён";
      $("signalrStatus").className = "status-pill status-on";
    });
    conn.onclose(() => {
      $("signalrStatus").textContent = "SignalR: отключён";
      $("signalrStatus").className = "status-pill status-off";
    });

    try {
      await conn.start();
      state.hub = conn;
      $("signalrStatus").textContent = "SignalR: подключён";
      $("signalrStatus").className = "status-pill status-on";
      addLog("WS Connected to /hubs/chat", "ws");
    } catch (err) {
      addLog(`WS Connect error: ${err.message}`, "err");
    }
  }

  async function onSessionChanged() {
    renderTopBar();
    renderProfile();

    const sess = activeSession();
    const user = sess?.user;
    $("openSubjectModalBtn").classList.toggle("hidden", !(user && (user.role === "Employee" || user.role === "Admin")));

    if (!sess || sess.mustChangePassword) {
      await stopSignalR();
      state.chats = [];
      renderChatList();
      renderActiveChat();
      return;
    }

    try {
      const freshUser = await api("/api/auth/me");
      sess.user = freshUser;
      saveState();
      renderTopBar();
      renderProfile();
    } catch { /* ignore */ }

    await startSignalR();
    await Promise.all([
      loadChats(),
      loadNotificationPreferences(),
      loadDirectoryGroups(),
      loadDirectoryUsers()
    ]);
    if (user?.role === "Admin") {
      await Promise.all([loadAdminGroups(), loadAdminUsers()]);
    }
  }

  // ---------- Auth ----------
  async function loginWithCredentials(email, password) {
    const resp = await api("/api/auth/login", {
      method: "POST",
      body: JSON.stringify({ email, password })
    });
    upsertSession(resp);
    closeModal("loginModal");
    toast(`Вход выполнен: ${resp.user.fullName} (${resp.user.role})`);
    await onSessionChanged();
  }

  async function submitEiosLogin() {
    const useDemo = $("eiosDemoChk").checked;
    const payload = {
      username: $("eiosUsername").value.trim(),
      password: $("eiosPassword").value,
      groupName: $("eiosGroupName").value.trim() || null,
      fullName: useDemo ? ($("eiosFullName").value.trim() || null) : null,
      useDemoFallback: useDemo
    };
    const resp = await api("/api/auth/eios-login", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    upsertSession(resp);
    closeModal("loginModal");
    toast(`Вход через ЭИОС: ${resp.user.fullName} (${resp.user.role})`);
    await onSessionChanged();
  }

  async function changeInitialPassword() {
    const currentPassword = $("mcpCurrentPass").value;
    const newPassword = $("mcpNewPass").value;
    const resp = await api("/api/auth/change-initial-password", {
      method: "POST",
      body: JSON.stringify({ currentPassword, newPassword })
    });
    upsertSession(resp);
    $("mcpCurrentPass").value = "";
    $("mcpNewPass").value = "";
    toast("Пароль изменён, получен новый токен.");
    await onSessionChanged();
  }

  // ---------- Chats & Messages ----------
  async function loadChats() {
    if (!activeSession() || activeSession().mustChangePassword) return;
    const inc = $("includeArchivedChk").checked;
    state.chats = await api(`/api/chats?includeArchived=${inc}`);
    renderChatList();
  }

  function renderChatList() {
    const list = $("chatList");
    list.innerHTML = "";
    if (!state.chats.length) {
      list.innerHTML = `<div class="muted">Чатов пока нет</div>`;
      return;
    }
    for (const c of state.chats) {
      const div = document.createElement("div");
      div.className = `chat-item ${c.id === state.activeChatId ? "active" : ""}`;
      const groupsStr = c.boundGroups?.map((g) => g.name).join(", ");
      const flags = [
        c.settings && !c.settings.allowMembersSendMessages ? "Канал" : "",
        c.isArchived ? "Архив" : ""
      ].filter(Boolean).join(" · ");
      const muteIcon = c.notificationsEnabled === false ? "🔕 " : "";

      div.innerHTML = `
        <div class="chat-item-title">
          <span>${muteIcon}${esc(c.title)}</span>
          <span class="badge">${esc(c.type)}</span>
        </div>
        <div class="chat-item-sub">${esc(groupsStr || "Личный / беседа")}${flags ? " · " + esc(flags) : ""}</div>
      `;
      div.onclick = () => openChat(c.id);
      list.appendChild(div);
    }
  }

  async function openChat(chatId) {
    state.activeChatId = chatId;
    renderChatList();
    const [details, msgs] = await Promise.all([
      api(`/api/chats/${chatId}`),
      api(`/api/chats/${chatId}/messages?page=1&pageSize=100`)
    ]);
    state.activeChatDetails = details;
    state.messages = msgs.slice().reverse();
    if (state.hub?.state === "Connected") {
      state.hub.invoke("JoinChat", chatId).catch(() => {});
    }
    renderActiveChat();
    if (!$("filesDrawer").classList.contains("hidden")) {
      await loadFiles();
    }
  }

  function renderActiveChat() {
    const chat = state.activeChatDetails;
    if (!chat) {
      $("chatEmptyState").classList.remove("hidden");
      $("chatActiveView").classList.add("hidden");
      return;
    }
    $("chatEmptyState").classList.add("hidden");
    $("chatActiveView").classList.remove("hidden");

    $("activeChatTitle").textContent = chat.title;
    $("activeChatTypeBadge").textContent = chat.type;
    $("activeChatChannelBadge").classList.toggle("hidden", chat.settings?.allowMembersSendMessages !== false);
    $("activeChatArchivedBadge").classList.toggle("hidden", !chat.isArchived);

    const groupsText = chat.boundGroups?.length
      ? "Группы: " + chat.boundGroups.map((g) => g.name).join(", ")
      : (chat.participants?.length ? `Участники (${chat.participants.length}): ${chat.participants.slice(0, 6).map((p) => p.fullName).join(", ")}` : "");
    $("activeChatBoundGroups").textContent = groupsText;

    const user = currentUser();
    const isEmployeeOrAdmin = user?.role === "Employee" || user?.role === "Admin";

    $("bindGroupsBtn").classList.toggle("hidden", !(isEmployeeOrAdmin && chat.isManager && chat.type !== "Direct"));
    $("openChatSettingsBtn").classList.toggle("hidden", !(hasPerm("CanManageChatSettings") && chat.type !== "Direct"));
    $("openChatRolesBtn").classList.toggle("hidden", !(hasPerm("CanManageRoles") && chat.type !== "Direct"));
    $("toggleArchiveBtn").classList.toggle("hidden", !(isEmployeeOrAdmin && chat.isManager));
    $("toggleArchiveBtn").textContent = chat.isArchived ? "Разархивировать" : "В архив";
    $("toggleChatNotificationsBtn").textContent = chat.notificationsEnabled ? "🔔 Уведомления: Вкл" : "🔕 Уведомления: Выкл";
    $("reqAckLabel").classList.toggle("hidden", !hasPerm("CanSendAcknowledgements"));
    $("openPollModalBtn").classList.toggle("hidden", !hasPerm("CanCreatePolls"));

    renderMessages();
  }

  function renderBadgesHtml(badges) {
    return (badges || []).map((b) =>
      `<span class="role-badge" style="background:${esc(b.badgeColor || '#2563eb')}">${esc(b.name)}</span>`
    ).join(" ");
  }

  function renderMessages() {
    const list = $("messagesList");
    list.innerHTML = "";
    const me = currentUser();
    const canPin = hasPerm("CanPinMessages");
    const canDeleteAny = hasPerm("CanDeleteMessages");
    const canSeeAck = hasPerm("CanSendAcknowledgements") || me?.role === "Employee" || me?.role === "Admin";

    for (const m of state.messages) {
      const card = document.createElement("div");
      const isMine = m.senderId === me?.id;
      card.className = `msg-card ${isMine ? "mine" : ""} ${m.isPinned ? "pinned" : ""}`;

      const badges = [
        `<span class="badge">${esc(m.senderRole)}</span>`,
        renderBadgesHtml(m.senderBadges),
        m.isPinned ? `<span class="badge badge-warn">📌 Закреплено</span>` : "",
        m.requiresAcknowledgement ? `<span class="badge badge-warn">Ознакомлены: ${m.acknowledgementsCount}</span>` : ""
      ].filter(Boolean).join(" ");

      card.innerHTML = `
        <div class="msg-header">
          <div><span class="msg-sender">${esc(m.senderName)}</span> · <span class="muted">${formatTime(m.createdAt)}</span></div>
          <div class="msg-badges">${badges}</div>
        </div>
        ${m.text ? `<div class="msg-text">${esc(m.text)}</div>` : ""}
      `;

      for (const att of m.attachments || []) {
        const box = document.createElement("div");
        box.className = "attachment-box";
        const dur = att.durationSeconds != null ? ` · ${att.durationSeconds} сек.` : "";
        box.innerHTML = `<div>📎 <strong>${esc(att.originalFileName)}</strong> <span class="muted">(${formatSize(att.fileSize)}${dur})</span></div>`;

        const btns = document.createElement("div");
        btns.style.display = "flex";
        btns.style.gap = "6px";
        btns.style.alignItems = "center";

        if (m.messageType === "Voice") {
          const playBtn = document.createElement("button");
          playBtn.className = "btn btn-sm btn-secondary";
          playBtn.textContent = "▶ Прослушать";
          playBtn.onclick = () => loadAudioIntoBox(att, box, playBtn);
          btns.appendChild(playBtn);
        }

        const dlBtn = document.createElement("button");
        dlBtn.className = "btn btn-sm btn-primary";
        dlBtn.textContent = "⬇ Скачать";
        dlBtn.onclick = () => downloadAttachment(att);
        btns.appendChild(dlBtn);
        box.appendChild(btns);
        card.appendChild(box);
      }

      if (m.poll) {
        card.appendChild(renderPollBox(m.poll, isMine || state.activeChatDetails?.isManager));
      }

      const actions = document.createElement("div");
      actions.className = "msg-actions";

      if (m.requiresAcknowledgement) {
        const ackBtn = document.createElement("button");
        ackBtn.className = `btn btn-sm ${m.isAcknowledgedByMe ? "btn-secondary" : "btn-primary"}`;
        ackBtn.textContent = m.isAcknowledgedByMe ? "✓ Вы ознакомлены" : "Ознакомлен";
        ackBtn.disabled = m.isAcknowledgedByMe;
        ackBtn.onclick = () => acknowledgeMessage(m.id);
        actions.appendChild(ackBtn);

        if (canSeeAck || isMine) {
          const statusBtn = document.createElement("button");
          statusBtn.className = "btn btn-sm btn-secondary";
          statusBtn.textContent = "📊 Кто прочитал";
          statusBtn.onclick = () => showAckStatus(m.id);
          actions.appendChild(statusBtn);
        }
      }

      if (canPin) {
        const pinBtn = document.createElement("button");
        pinBtn.className = "btn btn-sm btn-secondary";
        pinBtn.textContent = m.isPinned ? "Открепить" : "📌 Закрепить";
        pinBtn.onclick = () => togglePin(m.id, !m.isPinned);
        actions.appendChild(pinBtn);
      }

      if ((isMine || canDeleteAny) && !state.activeChatDetails?.isArchived) {
        const delBtn = document.createElement("button");
        delBtn.className = "btn btn-sm btn-danger";
        delBtn.textContent = "🗑 Удалить";
        delBtn.onclick = () => deleteMessage(m.id);
        actions.appendChild(delBtn);
      }

      if (actions.children.length) card.appendChild(actions);
      list.appendChild(card);
    }

    list.scrollTop = list.scrollHeight;
  }

  function renderPollBox(poll, canClose) {
    const box = document.createElement("div");
    box.className = "poll-box";
    const meta = [
      poll.isClosed ? "Закрыт" : "Открыт",
      poll.allowMultipleChoice ? "Множественный выбор" : "Один выбор",
      `Всего голосов: ${poll.totalVotes}`
    ].join(" · ");
    box.innerHTML = `<div class="muted">${esc(meta)}</div>`;

    for (const opt of poll.options) {
      const pct = poll.totalVotes > 0 ? Math.round((opt.votesCount / poll.totalVotes) * 100) : 0;
      const limitStr = opt.maxVotes != null ? ` / ${opt.maxVotes} мест` : "";
      const votersNames = opt.voters?.map((v) => v.fullName.split(" ")[0]).join(", ");

      const optEl = document.createElement("div");
      optEl.className = `poll-option ${opt.isVotedByMe ? "voted" : ""} ${opt.isFull ? "full" : ""}`;
      optEl.innerHTML = `
        <div class="poll-option-top">
          <span>${opt.isVotedByMe ? "☑ " : ""}${esc(opt.optionText)}</span>
          <span>${opt.votesCount}${esc(limitStr)} ${opt.isFull ? "(Заполнено)" : ""}</span>
        </div>
        <div class="poll-bar"><div style="width:${pct}%"></div></div>
        ${votersNames ? `<div class="muted" style="margin:3px 0 0">Записались: ${esc(votersNames)}</div>` : ""}
      `;

      optEl.onclick = async () => {
        try {
          if (opt.isVotedByMe) {
            await api(`/api/polls/${opt.id}/vote`, { method: "DELETE" });
            toast("Голос отменён");
          } else {
            await api(`/api/polls/${opt.id}/vote`, { method: "POST" });
            toast("Голос учтён");
          }
          if (state.activeChatId) await openChat(state.activeChatId);
        } catch (err) { toast(err.message, "err"); }
      };
      box.appendChild(optEl);
    }

    if (canClose) {
      const closeBtn = document.createElement("button");
      closeBtn.className = "btn btn-sm btn-secondary";
      closeBtn.style.alignSelf = "flex-start";
      closeBtn.textContent = poll.isClosed ? "Открыть опрос" : "Закрыть опрос";
      closeBtn.onclick = async () => {
        await api(`/api/polls/${poll.id}/close`, {
          method: "PATCH",
          body: JSON.stringify({ isClosed: !poll.isClosed })
        });
        await openChat(state.activeChatId);
      };
      box.appendChild(closeBtn);
    }

    return box;
  }

  async function fetchAttachmentBlobUrl(att) {
    if (state.blobUrls.has(att.id)) return state.blobUrls.get(att.id);
    const url = `${state.apiBase.replace(/\/+$/, "")}${att.downloadUrl}`;
    const resp = await fetch(url, {
      headers: { Authorization: `Bearer ${activeSession()?.accessToken || ""}` }
    });
    if (!resp.ok) throw new Error(`Ошибка загрузки файла (${resp.status})`);
    const blob = await resp.blob();
    const objUrl = URL.createObjectURL(blob);
    state.blobUrls.set(att.id, objUrl);
    return objUrl;
  }

  async function downloadAttachment(att) {
    try {
      const objUrl = await fetchAttachmentBlobUrl(att);
      const a = document.createElement("a");
      a.href = objUrl;
      a.download = att.originalFileName;
      a.click();
    } catch (err) { toast(err.message, "err"); }
  }

  async function loadAudioIntoBox(att, box, btn) {
    try {
      const objUrl = await fetchAttachmentBlobUrl(att);
      const audio = document.createElement("audio");
      audio.controls = true;
      audio.src = objUrl;
      btn.replaceWith(audio);
      audio.play().catch(() => {});
    } catch (err) { toast(err.message, "err"); }
  }

  async function sendMessage() {
    if (!state.activeChatId) return;
    const text = $("messageTextInput").value.trim();
    const requiresAcknowledgement = $("reqAckChk").checked;

    try {
      if (state.pendingFile) {
        const fd = new FormData();
        fd.append("ChatId", state.activeChatId);
        fd.append("File", state.pendingFile.file);
        if (text) fd.append("Text", text);
        fd.append("IsVoice", String(state.pendingFile.isVoice));
        if (state.pendingFile.durationSeconds != null) {
          fd.append("DurationSeconds", String(state.pendingFile.durationSeconds));
        }
        fd.append("RequiresAcknowledgement", String(requiresAcknowledgement));

        const res = await api("/api/messages/upload", { method: "POST", body: fd });
        clearPendingFile();
        $("messageTextInput").value = "";
        $("reqAckChk").checked = false;
        if (!state.messages.some((m) => m.id === res.message.id)) {
          state.messages.push(res.message);
          renderMessages();
        }
        return;
      }

      if (!text) return;

      const viaSignalR = $("sendViaSignalRChk").checked && state.hub?.state === "Connected";
      let res;
      if (viaSignalR) {
        res = await state.hub.invoke("SendMessage", {
          chatId: state.activeChatId,
          text,
          requiresAcknowledgement
        });
        addLog("WS invoke SendMessage -> ok", "ws");
      } else {
        res = await api("/api/messages", {
          method: "POST",
          body: JSON.stringify({ chatId: state.activeChatId, text, requiresAcknowledgement })
        });
      }

      $("messageTextInput").value = "";
      $("reqAckChk").checked = false;
      if (res?.message && !state.messages.some((m) => m.id === res.message.id)) {
        state.messages.push(res.message);
        renderMessages();
      }
    } catch (err) { toast(err.message, "err"); }
  }

  function clearPendingFile() {
    state.pendingFile = null;
    $("uploadFileInput").value = "";
    $("pendingFileLabel").classList.add("hidden");
    $("pendingFileLabel").textContent = "";
  }

  async function togglePin(messageId, isPinned) {
    try {
      await api(`/api/messages/${messageId}/pin`, {
        method: "PATCH",
        body: JSON.stringify({ isPinned })
      });
      await openChat(state.activeChatId);
    } catch (err) { toast(err.message, "err"); }
  }

  async function deleteMessage(messageId) {
    try {
      await api(`/api/messages/${messageId}`, { method: "DELETE" });
      state.messages = state.messages.filter((m) => m.id !== messageId);
      renderMessages();
      toast("Сообщение удалено");
    } catch (err) { toast(err.message, "err"); }
  }

  async function acknowledgeMessage(messageId) {
    try {
      await api(`/api/messages/${messageId}/ack`, { method: "POST" });
      toast("Прочтение подтверждено");
      await openChat(state.activeChatId);
    } catch (err) { toast(err.message, "err"); }
  }

  async function showAckStatus(messageId) {
    try {
      const data = await api(`/api/messages/${messageId}/ack-status`);
      const ackRows = data.acknowledgedUsers.map((u) =>
        `<li><strong>${esc(u.fullName)}</strong> (${esc(u.groupName || "-")}) — ${formatTime(u.confirmedAt)}</li>`
      ).join("") || "<li class='muted'>Пока никто</li>";

      const pendRows = data.pendingUsers.map((u) =>
        `<li>${esc(u.fullName)} (${esc(u.groupName || "-")})</li>`
      ).join("") || "<li class='muted'>Все ознакомлены</li>";

      $("ackStatusBody").innerHTML = `
        <div><h4>✅ Ознакомились (${data.acknowledgedCount})</h4><ul>${ackRows}</ul></div>
        <div><h4>⏳ Не прочитали (${data.pendingCount})</h4><ul>${pendRows}</ul></div>
      `;
      openModal("ackStatusModal");
    } catch (err) { toast(err.message, "err"); }
  }

  async function loadFiles() {
    if (!state.activeChatId) return;
    const qs = new URLSearchParams();
    const search = $("filesSearchInput").value.trim();
    const type = $("filesTypeSelect").value;
    if (search) qs.set("search", search);
    if (type) qs.set("type", type);

    const items = await api(`/api/chats/${state.activeChatId}/files?${qs}`);
    const list = $("filesList");
    list.innerHTML = "";
    if (!items.length) {
      list.innerHTML = `<div class="muted">Файлов не найдено</div>`;
      return;
    }
    for (const item of items) {
      const div = document.createElement("div");
      div.className = "material-item";
      div.innerHTML = `
        <div><strong>${esc(item.originalFileName)}</strong> <span class="badge">${esc(item.messageType)}</span></div>
        <div class="muted">${esc(item.senderName)} · ${formatSize(item.fileSize)} · ${formatTime(item.createdAt)}</div>
      `;
      const dlBtn = document.createElement("button");
      dlBtn.className = "btn btn-sm btn-secondary";
      dlBtn.style.marginTop = "5px";
      dlBtn.textContent = "⬇ Скачать";
      dlBtn.onclick = () => downloadAttachment({ id: item.attachmentId, downloadUrl: item.downloadUrl, originalFileName: item.originalFileName });
      div.appendChild(dlBtn);
      list.appendChild(div);
    }
  }

  // ---------- Chat Roles Constructor ----------
  function renderChatRolesModal() {
    const chat = state.activeChatDetails;
    if (!chat) return;
    const list = $("chatRolesList");
    list.innerHTML = "";

    if (!chat.roles?.length) {
      list.innerHTML = `<div class="muted">Кастомных ролей в этом чате ещё нет. Создайте первую справа.</div>`;
      return;
    }

    for (const role of chat.roles) {
      const card = document.createElement("div");
      card.className = "role-card";
      const permsText = role.permissionNames.join(", ") || "Только бейдж";
      const assigned = chat.participants.filter((p) => role.memberUserIds.includes(p.userId));
      const available = chat.participants.filter((p) => !role.memberUserIds.includes(p.userId));

      card.innerHTML = `
        <div class="section-header">
          <div>
            <span class="role-badge" style="background:${esc(role.badgeColor)}">${esc(role.name)}</span>
            ${role.isTemplate ? '<span class="badge">шаблон</span>' : ""}
          </div>
          <button class="btn btn-sm btn-danger" data-del-role="${role.id}">Удалить</button>
        </div>
        <div class="muted" style="margin:0">Права: ${esc(permsText)}</div>
      `;

      const membersRow = document.createElement("div");
      membersRow.style.display = "flex";
      membersRow.style.flexWrap = "wrap";
      membersRow.style.gap = "5px";
      for (const m of assigned) {
        const pill = document.createElement("button");
        pill.className = "btn btn-sm btn-secondary";
        pill.textContent = `${m.fullName.split(" ")[0]} ×`;
        pill.title = "Нажмите, чтобы снять роль";
        pill.onclick = async () => {
          await api(`/api/chats/${chat.id}/roles/${role.id}/members/${m.userId}`, { method: "DELETE" });
          await openChat(chat.id);
          renderChatRolesModal();
        };
        membersRow.appendChild(pill);
      }
      card.appendChild(membersRow);

      if (available.length) {
        const assignRow = document.createElement("div");
        assignRow.className = "inline-form";
        assignRow.style.margin = "0";
        const sel = document.createElement("select");
        sel.innerHTML = available.map((u) => `<option value="${u.userId}">${esc(u.fullName)} (${esc(u.role)})</option>`).join("");
        const addBtn = document.createElement("button");
        addBtn.className = "btn btn-sm btn-primary";
        addBtn.textContent = "Выдать";
        addBtn.onclick = async () => {
          await api(`/api/chats/${chat.id}/roles/${role.id}/members/${sel.value}`, { method: "POST" });
          await openChat(chat.id);
          renderChatRolesModal();
        };
        assignRow.appendChild(sel);
        assignRow.appendChild(addBtn);
        card.appendChild(assignRow);
      }

      card.querySelector("[data-del-role]").onclick = async () => {
        await api(`/api/chats/${chat.id}/roles/${role.id}`, { method: "DELETE" });
        await openChat(chat.id);
        renderChatRolesModal();
      };

      list.appendChild(card);
    }
  }

  // ---------- Voice recording ----------
  async function toggleVoiceRecord() {
    if (state.mediaRecorder && state.mediaRecorder.state === "recording") {
      state.mediaRecorder.stop();
      $("recordVoiceBtn").textContent = "🎤 Голос";
      $("recordVoiceBtn").classList.remove("btn-danger");
      return;
    }

    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      state.audioChunks = [];
      state.recordStartAt = Date.now();
      const mimeType = MediaRecorder.isTypeSupported("audio/webm;codecs=opus") ? "audio/webm;codecs=opus" : "audio/webm";
      const rec = new MediaRecorder(stream, { mimeType });
      rec.ondataavailable = (e) => { if (e.data.size > 0) state.audioChunks.push(e.data); };
      rec.onstop = () => {
        stream.getTracks().forEach((t) => t.stop());
        const duration = Math.max(1, Math.round((Date.now() - state.recordStartAt) / 1000));
        const blob = new Blob(state.audioChunks, { type: "audio/webm" });
        const file = new File([blob], `voice_${Date.now()}.webm`, { type: "audio/webm" });
        state.pendingFile = { file, isVoice: true, durationSeconds: duration };
        $("pendingFileLabel").textContent = `🎤 Голосовое (${duration} сек.) ×`;
        $("pendingFileLabel").classList.remove("hidden");
      };
      rec.start();
      state.mediaRecorder = rec;
      $("recordVoiceBtn").textContent = "⏹ Стоп";
      $("recordVoiceBtn").classList.add("btn-danger");
    } catch (err) { toast("Микрофон недоступен: " + err.message, "err"); }
  }

  // ---------- Profile, Notifications & Directory ----------
  function renderProfile() {
    const u = currentUser();
    if (!u) {
      $("profileDetails").innerHTML = "Войдите в систему, чтобы увидеть профиль.";
      return;
    }
    $("profileDetails").innerHTML = `
      <div class="kv-key">ID</div><div><code>${esc(u.id)}</code></div>
      <div class="kv-key">ФИО</div><div>${esc(u.fullName)}</div>
      <div class="kv-key">Email</div><div>${esc(u.email)}</div>
      <div class="kv-key">Глобальная роль</div><div><span class="badge badge-info">${esc(u.role)}</span></div>
      <div class="kv-key">Группа</div><div>${esc(u.academicGroupName || "—")}</div>
      <div class="kv-key">ЭИОС</div><div>${u.isEiosVerified ? "Подтверждён" : "Нет"}</div>
      <div class="kv-key">Уведомления по умолчанию</div><div>${u.defaultNotificationsEnabled ? "Включены (включая новые чаты)" : "Выключены (только выбранные чаты)"}</div>
    `;
  }

  async function loadNotificationPreferences() {
    const wrap = $("chatNotificationsTableWrap");
    if (!activeSession() || activeSession().mustChangePassword) { wrap.innerHTML = ""; return; }
    try {
      const prefs = await api("/api/users/me/notifications");
      $("defaultNotifyChk").checked = Boolean(prefs.defaultNotificationsEnabled);
      if (activeSession().user) {
        activeSession().user.defaultNotificationsEnabled = prefs.defaultNotificationsEnabled;
        saveState();
        renderProfile();
      }

      if (!prefs.chats.length) {
        wrap.innerHTML = `<div class="muted">Доступных чатов пока нет.</div>`;
        return;
      }

      let html = `<table><thead><tr><th>Чат</th><th>Тип</th><th>Статус</th><th>Исключение</th></tr></thead><tbody>`;
      for (const c of prefs.chats) {
        const val = c.overrideValue === null ? "default" : String(c.overrideValue);
        html += `<tr>
          <td><strong>${esc(c.chatTitle)}</strong></td>
          <td><span class="badge">${esc(c.chatType)}</span></td>
          <td>${c.effectiveNotificationsEnabled ? "🔔 Вкл" : "🔕 Выкл"}</td>
          <td>
            <select data-chat-notify="${c.chatId}">
              <option value="default" ${val === "default" ? "selected" : ""}>По умолчанию</option>
              <option value="true" ${val === "true" ? "selected" : ""}>🔔 Всегда получать</option>
              <option value="false" ${val === "false" ? "selected" : ""}>🔕 Не получать</option>
            </select>
          </td>
        </tr>`;
      }
      html += `</tbody></table>`;
      wrap.innerHTML = html;

      wrap.querySelectorAll("[data-chat-notify]").forEach((sel) => {
        sel.onchange = async () => {
          const raw = sel.value;
          const notificationsEnabled = raw === "default" ? null : raw === "true";
          await api(`/api/chats/${sel.dataset.chatNotify}/notifications`, {
            method: "PUT",
            body: JSON.stringify({ notificationsEnabled })
          });
          toast("Настройка уведомлений обновлена");
          await Promise.all([loadNotificationPreferences(), loadChats()]);
          if (state.activeChatId === sel.dataset.chatNotify) await openChat(state.activeChatId);
        };
      });
    } catch (err) { wrap.innerHTML = `<div class="muted">${esc(err.message)}</div>`; }
  }

  async function loadDirectoryUsers() {
    if (!activeSession() || activeSession().mustChangePassword) return;
    try { state.directoryCache = await api("/api/users/directory?limit=200"); } catch { /* ignore */ }
  }

  async function startDirectWithUser(targetUserId) {
    try {
      const chat = await api("/api/chats/direct", {
        method: "POST",
        body: JSON.stringify({ targetUserId })
      });
      document.querySelector('[data-tab="messenger"]').click();
      await loadChats();
      await openChat(chat.id);
    } catch (err) { toast(err.message, "err"); }
  }

  async function loadDirectoryGroups() {
    const wrap = $("directoryGroupsTableWrap");
    if (!activeSession() || activeSession().mustChangePassword) { wrap.innerHTML = ""; return; }
    try {
      const groups = await api("/api/groups");
      state.groupsCache = groups;
      if (!groups.length) { wrap.innerHTML = `<div class="muted">Групп пока нет.</div>`; return; }

      const meId = currentUser()?.id;
      let html = `<table><thead><tr><th>Группа</th><th>Курс / Факультет</th><th>Староста</th><th>Куратор</th><th>Студ.</th></tr></thead><tbody>`;
      for (const g of groups) {
        const leaderCell = g.leader
          ? `${esc(g.leader.fullName)} ${g.leader.id !== meId ? `<button class="btn btn-sm btn-secondary" data-dm-user="${g.leader.id}">💬 ЛС</button>` : ""}`
          : `<span class="muted">Не назначен</span>`;
        const curatorCell = g.curator
          ? `${esc(g.curator.fullName)} ${g.curator.id !== meId ? `<button class="btn btn-sm btn-secondary" data-dm-user="${g.curator.id}">💬 ЛС</button>` : ""}`
          : `<span class="muted">Нет</span>`;
        html += `<tr>
          <td><strong>${esc(g.name)}</strong></td>
          <td>${g.course} курс · ${esc(g.faculty)}</td>
          <td>${leaderCell}</td>
          <td>${curatorCell}</td>
          <td>${g.studentsCount}</td>
        </tr>`;
      }
      html += `</tbody></table>`;
      wrap.innerHTML = html;

      wrap.querySelectorAll("[data-dm-user]").forEach((btn) => {
        btn.onclick = () => startDirectWithUser(btn.dataset.dmUser);
      });
    } catch (err) { wrap.innerHTML = `<div class="muted">${esc(err.message)}</div>`; }
  }

  // ---------- Admin Panel ----------
  async function loadAdminGroups() {
    const wrap = $("groupsTableWrap");
    try {
      const [groups, allUsers] = await Promise.all([
        api("/api/groups"),
        api("/api/admin/users?pageSize=200")
      ]);
      state.groupsCache = groups;
      const employees = allUsers.filter((u) => u.role === "Employee");

      let html = `<table><thead><tr><th>Группа</th><th>Курс</th><th>Староста (Student группы)</th><th>Куратор (Employee)</th><th></th></tr></thead><tbody>`;
      for (const g of groups) {
        const groupStudents = allUsers.filter((u) => u.role === "Student" && u.academicGroupId === g.id);
        const leaderOpts = `<option value="">— не назначен —</option>` + groupStudents.map((s) =>
          `<option value="${s.id}" ${g.leader?.id === s.id ? "selected" : ""}>${esc(s.fullName)}</option>`
        ).join("");
        const curatorOpts = `<option value="">— без куратора —</option>` + employees.map((e) =>
          `<option value="${e.id}" ${g.curator?.id === e.id ? "selected" : ""}>${esc(e.fullName)}</option>`
        ).join("");

        html += `<tr>
          <td><strong>${esc(g.name)}</strong><div class="muted" style="margin:0">${esc(g.faculty)}</div></td>
          <td>${g.course}</td>
          <td><select data-assign-leader="${g.id}">${leaderOpts}</select></td>
          <td><select data-assign-curator="${g.id}">${curatorOpts}</select></td>
          <td><button class="btn btn-sm btn-danger" data-del-group="${g.id}">Удалить</button></td>
        </tr>`;
      }
      html += `</tbody></table>`;
      wrap.innerHTML = html;

      wrap.querySelectorAll("[data-assign-leader]").forEach((sel) => {
        sel.onchange = async () => {
          if (!sel.value) return;
          try {
            await api(`/api/groups/${sel.dataset.assignLeader}/leader`, {
              method: "PUT",
              body: JSON.stringify({ studentUserId: sel.value })
            });
            toast("Староста группы назначен");
            await Promise.all([loadAdminGroups(), loadDirectoryGroups()]);
          } catch (e) { toast(e.message, "err"); }
        };
      });

      wrap.querySelectorAll("[data-assign-curator]").forEach((sel) => {
        sel.onchange = async () => {
          try {
            await api(`/api/groups/${sel.dataset.assignCurator}/curator`, {
              method: "PUT",
              body: JSON.stringify({ curatorUserId: sel.value || null })
            });
            toast("Куратор группы обновлён");
            await Promise.all([loadAdminGroups(), loadDirectoryGroups()]);
          } catch (e) { toast(e.message, "err"); }
        };
      });

      wrap.querySelectorAll("[data-del-group]").forEach((btn) => {
        btn.onclick = async () => {
          try {
            await api(`/api/groups/${btn.dataset.delGroup}`, { method: "DELETE" });
            toast("Группа удалена");
            await Promise.all([loadAdminGroups(), loadDirectoryGroups(), loadChats()]);
          } catch (e) { toast(e.message, "err"); }
        };
      });
    } catch (err) { wrap.innerHTML = `<div class="muted">${esc(err.message)}</div>`; }
  }

  async function loadAdminUsers() {
    try {
      const qs = new URLSearchParams({ pageSize: "200" });
      const search = $("filterUsersSearch").value.trim();
      const role = $("filterUsersRole").value;
      if (search) qs.set("search", search);
      if (role) qs.set("role", role);

      const users = await api(`/api/admin/users?${qs}`);
      const roles = ["Student", "Employee", "Admin"];
      let html = `<table><thead><tr><th>ФИО</th><th>Email</th><th>Группа</th><th>Глобальная роль</th></tr></thead><tbody>`;
      for (const u of users) {
        const opts = roles.map((r) => `<option value="${r}" ${r === u.role ? "selected" : ""}>${r}</option>`).join("");
        html += `<tr>
          <td>${esc(u.fullName)}</td>
          <td>${esc(u.email)}</td>
          <td>${esc(u.academicGroupName || "—")}</td>
          <td><select data-role-user="${u.id}">${opts}</select></td>
        </tr>`;
      }
      html += `</tbody></table>`;
      $("usersTableWrap").innerHTML = html;

      $("usersTableWrap").querySelectorAll("[data-role-user]").forEach((sel) => {
        sel.onchange = async () => {
          try {
            await api(`/api/admin/users/${sel.dataset.roleUser}/role`, {
              method: "PUT",
              body: JSON.stringify({ role: sel.value })
            });
            toast("Глобальная роль обновлена");
            await loadAdminGroups();
          } catch (e) { toast(e.message, "err"); }
        };
      });
    } catch (err) { $("usersTableWrap").innerHTML = `<div class="muted">${esc(err.message)}</div>`; }
  }

  async function runCsvImport(file) {
    const fd = new FormData();
    fd.append("File", file);
    fd.append("DefaultFaculty", $("csvDefaultFaculty").value.trim() || "ФИТ");
    fd.append("SendEmails", String($("csvSendEmailsChk").checked));

    const res = await api("/api/admin/import-users", { method: "POST", body: fd });
    const box = $("importResultBox");
    box.classList.remove("hidden");

    let html = `<div><strong>Создано:</strong> ${res.createdCount} из ${res.totalRows} (групп: ${res.groupsCreatedCount}, пропущено: ${res.skippedCount})</div>`;
    if (res.errors?.length) {
      html += `<div class="muted" style="color:#dc2626">${res.errors.map(esc).join("<br>")}</div>`;
    }
    if (res.createdUsers?.length) {
      html += `<table><thead><tr><th>ФИО</th><th>Email</th><th>Роль</th><th>Пароль</th><th></th></tr></thead><tbody>`;
      for (const u of res.createdUsers) {
        html += `<tr>
          <td>${esc(u.fullName)}</td>
          <td>${esc(u.email)}</td>
          <td>${esc(u.role)}</td>
          <td><code>${esc(u.temporaryPassword)}</code></td>
          <td><button class="btn btn-sm btn-secondary" data-quick-login="${esc(u.email)}" data-quick-pass="${esc(u.temporaryPassword)}">Войти</button></td>
        </tr>`;
      }
      html += `</tbody></table>`;
    }
    box.innerHTML = html;
    box.querySelectorAll("[data-quick-login]").forEach((btn) => {
      btn.onclick = () => {
        $("mcpCurrentPass").value = btn.dataset.quickPass;
        loginWithCredentials(btn.dataset.quickLogin, btn.dataset.quickPass);
      };
    });

    await Promise.all([loadAdminGroups(), loadAdminUsers(), loadDirectoryGroups(), loadDirectoryUsers(), loadChats()]);
  }

  // ---------- Modals ----------
  function openModal(id) { $(id).classList.remove("hidden"); }
  function closeModal(id) { $(id).classList.add("hidden"); }

  function initPollOptionRows() {
    const cont = $("pollOptionsContainer");
    cont.innerHTML = "";
    addPollOptionRow("10:00 (слот 1)", "1");
    addPollOptionRow("11:00 (слот 2)", "5");
  }

  function addPollOptionRow(text = "", maxVotes = "") {
    const row = document.createElement("div");
    row.className = "row-2";
    row.innerHTML = `
      <input type="text" class="poll-opt-text" placeholder="Вариант ответа" value="${esc(text)}" />
      <input type="number" class="poll-opt-max" min="1" placeholder="Лимит мест (необяз.)" value="${esc(maxVotes)}" />
    `;
    $("pollOptionsContainer").appendChild(row);
  }

  // ---------- Event Wiring ----------
  function wireEvents() {
    document.querySelectorAll(".nav-tab").forEach((tab) => {
      tab.onclick = () => {
        document.querySelectorAll(".nav-tab").forEach((t) => t.classList.remove("active"));
        document.querySelectorAll(".tab-panel").forEach((p) => p.classList.remove("active"));
        tab.classList.add("active");
        $(`tab-${tab.dataset.tab}`).classList.add("active");
      };
    });

    document.querySelectorAll("[data-close-modal]").forEach((btn) => {
      btn.onclick = () => closeModal(btn.dataset.closeModal);
    });

    document.querySelectorAll("[data-auth-mode]").forEach((btn) => {
      btn.onclick = () => {
        document.querySelectorAll("[data-auth-mode]").forEach((b) => b.classList.remove("active"));
        btn.classList.add("active");
        $("authModePassword").classList.toggle("hidden", btn.dataset.authMode !== "password");
        $("authModeEios").classList.toggle("hidden", btn.dataset.authMode !== "eios");
      };
    });

    $("saveApiBaseBtn").onclick = () => {
      state.apiBase = $("apiBaseInput").value.trim() || "http://localhost:5132";
      saveState();
      toast(`API URL: ${state.apiBase}`);
      onSessionChanged();
    };

    $("openLoginModalBtn").onclick = () => openModal("loginModal");
    $("fillDefaultAdminBtn").onclick = () => {
      $("loginEmail").value = "admin@faculty.local";
      $("loginPassword").value = "nJMcmfH4VwDEo2SK!a1";
    };
    $("loginSubmitBtn").onclick = () =>
      loginWithCredentials($("loginEmail").value.trim(), $("loginPassword").value).catch((e) => toast(e.message, "err"));
    $("eiosDemoChk").onchange = () => $("eiosDemoFullNameWrap").classList.toggle("hidden", !$("eiosDemoChk").checked);
    $("eiosLoginSubmitBtn").onclick = () => submitEiosLogin().catch((e) => toast(e.message, "err"));
    $("mcpSubmitBtn").onclick = () => changeInitialPassword().catch((e) => toast(e.message, "err"));

    // Chats
    $("refreshChatsBtn").onclick = () => loadChats().catch((e) => toast(e.message, "err"));
    $("includeArchivedChk").onchange = () => loadChats().catch((e) => toast(e.message, "err"));
    $("sendMessageBtn").onclick = sendMessage;
    $("messageTextInput").onkeydown = (e) => {
      if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); sendMessage(); }
    };

    // File & Voice
    $("attachFileBtn").onclick = () => $("uploadFileInput").click();
    $("uploadFileInput").onchange = () => {
      const f = $("uploadFileInput").files[0];
      if (!f) return;
      state.pendingFile = { file: f, isVoice: false, durationSeconds: null };
      $("pendingFileLabel").textContent = `📎 ${f.name} (${formatSize(f.size)}) ×`;
      $("pendingFileLabel").classList.remove("hidden");
    };
    $("pendingFileLabel").onclick = clearPendingFile;
    $("recordVoiceBtn").onclick = toggleVoiceRecord;

    // Files drawer
    $("toggleFilesBtn").onclick = async () => {
      $("filesDrawer").classList.toggle("hidden");
      if (!$("filesDrawer").classList.contains("hidden")) await loadFiles().catch((e) => toast(e.message, "err"));
    };
    $("closeFilesBtn").onclick = () => $("filesDrawer").classList.add("hidden");
    $("refreshFilesBtn").onclick = () => loadFiles().catch((e) => toast(e.message, "err"));
    $("filesTypeSelect").onchange = () => loadFiles().catch((e) => toast(e.message, "err"));

    // Chat actions
    $("toggleChatNotificationsBtn").onclick = async () => {
      try {
        const next = !state.activeChatDetails.notificationsEnabled;
        await api(`/api/chats/${state.activeChatId}/notifications`, {
          method: "PUT",
          body: JSON.stringify({ notificationsEnabled: next })
        });
        toast(next ? "Уведомления в чате включены" : "Уведомления в чате отключены");
        await Promise.all([openChat(state.activeChatId), loadChats(), loadNotificationPreferences()]);
      } catch (e) { toast(e.message, "err"); }
    };

    $("openChatSettingsBtn").onclick = () => {
      const s = state.activeChatDetails.settings;
      $("setSendMsgChk").checked = s.allowMembersSendMessages;
      $("setSendMediaChk").checked = s.allowMembersSendMedia;
      $("setPinMsgChk").checked = s.allowMembersPinMessages;
      $("setCreatePollsChk").checked = s.allowMembersCreatePolls;
      openModal("chatSettingsModal");
    };
    $("saveChatSettingsBtn").onclick = async () => {
      try {
        await api(`/api/chats/${state.activeChatId}/settings`, {
          method: "PATCH",
          body: JSON.stringify({
            allowMembersSendMessages: $("setSendMsgChk").checked,
            allowMembersSendMedia: $("setSendMediaChk").checked,
            allowMembersPinMessages: $("setPinMsgChk").checked,
            allowMembersCreatePolls: $("setCreatePollsChk").checked
          })
        });
        closeModal("chatSettingsModal");
        toast("Настройки прав беседы сохранены");
        await Promise.all([openChat(state.activeChatId), loadChats()]);
      } catch (e) { toast(e.message, "err"); }
    };

    $("openChatRolesBtn").onclick = () => { renderChatRolesModal(); openModal("chatRolesModal"); };
    $("createChatRoleSubmitBtn").onclick = async () => {
      try {
        await api(`/api/chats/${state.activeChatId}/roles`, {
          method: "POST",
          body: JSON.stringify({
            name: $("newRoleName").value.trim(),
            badgeColor: $("newRoleColor").value,
            canSendMessages: $("nrSendMsg").checked,
            canSendMedia: $("nrSendMedia").checked,
            canPinMessages: $("nrPin").checked,
            canDeleteMessages: $("nrDelete").checked,
            canCreatePolls: $("nrPolls").checked,
            canSendAcknowledgements: $("nrAck").checked,
            canManageRoles: $("nrManageRoles").checked,
            canManageChatSettings: $("nrManageSettings").checked
          })
        });
        $("newRoleName").value = "";
        toast("Кастомная роль создана");
        await openChat(state.activeChatId);
        renderChatRolesModal();
      } catch (e) { toast(e.message, "err"); }
    };

    $("toggleArchiveBtn").onclick = async () => {
      try {
        await api(`/api/chats/${state.activeChatId}/archive`, {
          method: "PATCH",
          body: JSON.stringify({ isArchived: !state.activeChatDetails.isArchived })
        });
        await Promise.all([openChat(state.activeChatId), loadChats()]);
      } catch (e) { toast(e.message, "err"); }
    };

    // Custom group chat modal (available to all, including Students)
    $("openCustomChatModalBtn").onclick = async () => {
      await loadDirectoryUsers();
      const meId = currentUser()?.id;
      const sel = $("customChatUsersSelect");
      sel.innerHTML = state.directoryCache
        .filter((u) => u.id !== meId)
        .map((u) => `<option value="${u.id}">${esc(u.fullName)} (${esc(u.role)}${u.academicGroupName ? ", " + esc(u.academicGroupName) : ""})</option>`)
        .join("");
      openModal("customChatModal");
    };
    $("createCustomChatSubmitBtn").onclick = async () => {
      try {
        const participantUserIds = Array.from($("customChatUsersSelect").selectedOptions).map((o) => o.value);
        const created = await api("/api/chats/custom", {
          method: "POST",
          body: JSON.stringify({ title: $("customChatTitleInput").value.trim(), participantUserIds })
        });
        closeModal("customChatModal");
        $("customChatTitleInput").value = "";
        await loadChats();
        await openChat(created.id);
      } catch (e) { toast(e.message, "err"); }
    };

    // Subject chat modal
    $("openSubjectModalBtn").onclick = async () => {
      try {
        const groups = await api("/api/groups");
        $("subjGroupsSelect").innerHTML = groups.map((g) => `<option value="${g.id}">${esc(g.name)} (${esc(g.faculty)})</option>`).join("");
        openModal("subjectChatModal");
      } catch (e) { toast(e.message, "err"); }
    };
    $("createSubjectSubmitBtn").onclick = async () => {
      try {
        const groupIds = Array.from($("subjGroupsSelect").selectedOptions).map((o) => o.value);
        const created = await api("/api/chats/subject", {
          method: "POST",
          body: JSON.stringify({
            title: $("subjTitleInput").value.trim(),
            groupIds,
            allowMembersSendMessages: $("subjSendMsgChk").checked,
            allowMembersSendMedia: $("subjSendMediaChk").checked,
            allowMembersPinMessages: $("subjPinChk").checked,
            allowMembersCreatePolls: $("subjPollsChk").checked
          })
        });
        closeModal("subjectChatModal");
        $("subjTitleInput").value = "";
        await loadChats();
        await openChat(created.id);
      } catch (e) { toast(e.message, "err"); }
    };

    // Direct chat modal
    $("openDirectModalBtn").onclick = async () => {
      await loadDirectoryUsers();
      const box = $("directUserSuggestions");
      const meId = currentUser()?.id;
      box.innerHTML = "";
      for (const u of state.directoryCache.filter((x) => x.id !== meId)) {
        const b = document.createElement("button");
        b.className = "btn btn-sm btn-secondary";
        b.style.textAlign = "left";
        b.textContent = `${u.fullName} (${u.role}${u.academicGroupName ? ", " + u.academicGroupName : ""})`;
        b.onclick = () => { $("directTargetIdInput").value = u.id; };
        box.appendChild(b);
      }
      openModal("directChatModal");
    };
    $("createDirectSubmitBtn").onclick = async () => {
      await startDirectWithUser($("directTargetIdInput").value.trim());
      closeModal("directChatModal");
    };

    // Bind groups modal
    $("bindGroupsBtn").onclick = () => openModal("bindGroupsModal");
    $("bindGroupsSubmitBtn").onclick = async () => {
      try {
        const groupNames = $("bindGroupNamesInput").value.split(",").map((x) => x.trim()).filter(Boolean);
        await api(`/api/chats/${state.activeChatId}/bind-groups`, {
          method: "POST",
          body: JSON.stringify({ groupNames })
        });
        closeModal("bindGroupsModal");
        await Promise.all([openChat(state.activeChatId), loadChats()]);
      } catch (e) { toast(e.message, "err"); }
    };

    // Poll modal
    $("openPollModalBtn").onclick = () => {
      initPollOptionRows();
      $("pollAckWrap").classList.toggle("hidden", !hasPerm("CanSendAcknowledgements"));
      openModal("pollModal");
    };
    $("addPollOptionBtn").onclick = () => addPollOptionRow("", "");
    $("createPollSubmitBtn").onclick = async () => {
      try {
        const rows = Array.from($("pollOptionsContainer").children);
        const options = rows.map((r) => {
          const optionText = r.querySelector(".poll-opt-text").value.trim();
          const maxRaw = r.querySelector(".poll-opt-max").value.trim();
          return { optionText, maxVotes: maxRaw ? Number(maxRaw) : null };
        }).filter((o) => o.optionText);

        await api("/api/polls", {
          method: "POST",
          body: JSON.stringify({
            chatId: state.activeChatId,
            question: $("pollQuestionInput").value.trim(),
            options,
            allowMultipleChoice: $("pollMultiChk").checked,
            requiresAcknowledgement: $("pollAckChk").checked
          })
        });
        closeModal("pollModal");
        $("pollQuestionInput").value = "";
        await openChat(state.activeChatId);
      } catch (e) { toast(e.message, "err"); }
    };

    // Profile & Notifications
    $("refreshNotificationsBtn").onclick = loadNotificationPreferences;
    $("refreshDirectoryGroupsBtn").onclick = loadDirectoryGroups;
    $("defaultNotifyChk").onchange = async () => {
      try {
        await api("/api/users/me/notifications", {
          method: "PUT",
          body: JSON.stringify({ defaultNotificationsEnabled: $("defaultNotifyChk").checked })
        });
        toast($("defaultNotifyChk").checked
          ? "Уведомления по умолчанию включены (включая новые чаты)"
          : "Уведомления по умолчанию выключены (только выбранные чаты)");
        await Promise.all([loadNotificationPreferences(), loadChats()]);
        if (state.activeChatId) await openChat(state.activeChatId);
      } catch (e) { toast(e.message, "err"); }
    };

    // Admin actions
    $("refreshGroupsBtn").onclick = loadAdminGroups;
    $("refreshUsersBtn").onclick = loadAdminUsers;
    $("applyUsersFilterBtn").onclick = loadAdminUsers;

    $("createGroupBtn").onclick = async () => {
      try {
        await api("/api/groups", {
          method: "POST",
          body: JSON.stringify({
            name: $("cgName").value.trim(),
            course: Number($("cgCourse").value),
            faculty: $("cgFaculty").value.trim()
          })
        });
        $("cgName").value = "";
        toast("Группа создана (включая шаблоны «Замстаросты» и «Профорг»)");
        await Promise.all([loadAdminGroups(), loadDirectoryGroups(), loadChats()]);
      } catch (e) { toast(e.message, "err"); }
    };

    $("createUserBtn").onclick = async () => {
      try {
        const res = await api("/api/admin/users", {
          method: "POST",
          body: JSON.stringify({
            fullName: $("cuFullName").value.trim(),
            email: $("cuEmail").value.trim(),
            role: $("cuRole").value,
            groupName: $("cuGroupName").value.trim() || null,
            sendEmail: $("cuSendEmailChk").checked
          })
        });
        const box = $("createUserResult");
        box.classList.remove("hidden");
        box.innerHTML = `Создан: <strong>${esc(res.user.email)}</strong>, временный пароль: <code>${esc(res.temporaryPassword)}</code>
          <button class="btn btn-sm btn-secondary" id="quickCreatedLoginBtn">Войти</button>`;
        $("quickCreatedLoginBtn").onclick = () => {
          $("mcpCurrentPass").value = res.temporaryPassword;
          loginWithCredentials(res.user.email, res.temporaryPassword);
        };
        await Promise.all([loadAdminUsers(), loadAdminGroups(), loadDirectoryGroups(), loadDirectoryUsers(), loadChats()]);
      } catch (e) { toast(e.message, "err"); }
    };

    $("importCsvBtn").onclick = async () => {
      const f = $("csvFileInput").files[0];
      if (!f) return toast("Выберите CSV-файл", "warn");
      try { await runCsvImport(f); } catch (e) { toast(e.message, "err"); }
    };

    $("importDemoCsvBtn").onclick = async () => {
      const demoCsv = [
        "FullName;Email;GroupName;Role",
        "Петрова Елена Владимировна;petrova@uni.ru;;Employee",
        "Смирнов Игорь Алексеевич;smirnov@uni.ru;413.2;Leader",
        "Козлов Дмитрий Сергеевич;kozlov@uni.ru;413.2;Student",
        "Морозова Анна Павловна;morozova@uni.ru;413.2;Student",
        "Волков Артём Андреевич;volkov@uni.ru;413.1;Leader"
      ].join("\n");
      const file = new File([new Blob(["\uFEFF" + demoCsv], { type: "text/csv;charset=utf-8" })], "demo.csv", { type: "text/csv" });
      try { await runCsvImport(file); } catch (e) { toast(e.message, "err"); }
    };

    $("clearLogsBtn").onclick = () => {
      $("logList").innerHTML = "";
      state.logCount = 0;
      $("logCounter").textContent = "0";
    };
  }

  loadState();
  wireEvents();
  onSessionChanged();
})();
