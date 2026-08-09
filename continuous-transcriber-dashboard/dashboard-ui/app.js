const state = {
  summary: null,
  entries: [],
  visibleEntries: [],
  clips: [],
  rangeStart: 0,
  rangeEnd: 0,
  cursor: 0,
  activeClipIndex: -1,
  activeEntryId: null,
  dragging: null,
  programmaticScroll: false,
  followPlayback: true,
  searchTimer: 0,
  scrollTimer: 0,
  fleet: null,
  selectedDevices: new Set(),
  scheduleDeviceId: null,
  toastTimer: 0,
};

const els = Object.fromEntries([
  "archiveSummary", "refreshButton", "playButton", "backButton", "forwardButton",
  "playbackStatus", "playbackTime", "skipSilence", "timeline", "timelineSelection",
  "activityMarks", "timelineDragOffset", "startHandle", "cursorHandle", "endHandle", "cursorOutput",
  "startInput", "endInput", "searchInput", "matchCount", "transcriptScroller",
  "transcriptList", "loadingState", "audioPlayer", "deviceList", "selectAllDevices",
  "syncSelectedButton", "accessNotice", "toast", "scheduleDialog", "scheduleForm",
  "scheduleTitle", "schedulePreset", "scheduleCron", "cronField", "scheduleEnabled",
  "scheduleMove", "saveSchedule", "closeSchedule",
].map(id => [id, document.getElementById(id)]));

const dateTimeFormatter = new Intl.DateTimeFormat(undefined, {
  weekday: "short", month: "short", day: "numeric", hour: "numeric", minute: "2-digit", second: "2-digit"
});
const timeFormatter = new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit", second: "2-digit" });
const dayFormatter = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", year: "numeric" });

async function initialize() {
  bindEvents();
  await loadFleet();
  await refreshArchive(true);
}

async function loadFleet() {
  const response = await fetch("/api/fleet", { cache: "no-store" });
  if (!response.ok) throw new Error(await apiError(response));
  state.fleet = await response.json();
  const valid = new Set(state.fleet.devices.map(device => device.id));
  state.selectedDevices = new Set([...state.selectedDevices].filter(id => valid.has(id)));
  if (!state.selectedDevices.size) {
    const local = state.fleet.devices.find(device => device.isLocal) || state.fleet.devices[0];
    if (local) state.selectedDevices.add(local.id);
  }
  renderDevices();
}

function renderDevices() {
  els.deviceList.replaceChildren();
  const devices = state.fleet?.devices || [];
  const fragment = document.createDocumentFragment();
  for (const device of devices) {
    const card = document.createElement("article");
    card.className = `device-card${state.selectedDevices.has(device.id) ? " selected" : ""}`;
    const head = document.createElement("div");
    head.className = "device-card-head";
    const label = document.createElement("label");
    label.className = "device-identity";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.className = "device-select";
    input.checked = state.selectedDevices.has(device.id);
    input.addEventListener("change", async () => {
      input.checked ? state.selectedDevices.add(device.id) : state.selectedDevices.delete(device.id);
      if (!state.selectedDevices.size) state.selectedDevices.add(device.id);
      renderDevices();
      await refreshArchive(true);
    });
    const copy = document.createElement("span");
    copy.className = "device-copy";
    const name = document.createElement("strong");
    name.className = "device-name";
    name.textContent = device.name;
    const meta = document.createElement("small");
    meta.className = "device-meta";
    meta.textContent = `${device.hostName || "Unknown host"} · ${device.state}`;
    copy.append(name, meta);
    label.append(input, copy);
    const status = document.createElement("span");
    status.className = `device-status${String(device.state).toLowerCase() === "online" ? " online" : ""}`;
    status.title = device.state;
    head.append(label, status);
    card.append(head);
    if (device.isLocal) {
      const local = document.createElement("span");
      local.className = "device-local-badge";
      local.textContent = "THIS MACHINE · READ DIRECTLY";
      card.append(local);
    } else {
      const actions = document.createElement("div");
      actions.className = "device-card-actions";
      const download = deviceButton("Download transcriptions", () => downloadDevice(device));
      const schedule = deviceButton("Schedule", () => openSchedule(device));
      actions.append(download, schedule);
      card.append(actions);
    }
    fragment.append(card);
  }
  els.deviceList.append(fragment);
  const selectedCount = devices.filter(device => state.selectedDevices.has(device.id)).length;
  els.selectAllDevices.checked = devices.length > 0 && selectedCount === devices.length;
  els.selectAllDevices.indeterminate = selectedCount > 0 && selectedCount < devices.length;
  els.syncSelectedButton.disabled = !devices.some(device => !device.isLocal && state.selectedDevices.has(device.id));
  els.accessNotice.hidden = state.fleet?.canAccessRemote !== false;
  els.accessNotice.textContent = "This machine is managed-only in Opticon. For privacy, this dashboard can read only transcripts already on its own file system; remote devices, downloads, cross-device search, and schedules are unavailable.";
}

function deviceButton(label, action) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "device-button";
  button.textContent = label;
  button.addEventListener("click", action);
  return button;
}

function selectedQuery() { return [...state.selectedDevices].join(","); }

async function refreshArchive(resetRange = false) {
  setLoading("Reading transcripts from selected devices…");
  els.refreshButton.disabled = true;
  try {
    const response = await fetch(`/api/archive/summary?devices=${encodeURIComponent(selectedQuery())}`, { cache: "no-store" });
    if (!response.ok) throw new Error(await apiError(response));
    state.summary = await response.json();
    const availableStart = Date.parse(state.summary.availableStart);
    const availableEnd = Date.parse(state.summary.availableEnd);
    if (resetRange || !state.rangeStart || !state.rangeEnd) {
      state.rangeStart = availableStart;
      state.rangeEnd = Math.max(availableStart + 1000, availableEnd);
      state.cursor = state.rangeStart + (state.rangeEnd - state.rangeStart) / 2;
    } else {
      state.rangeStart = clamp(state.rangeStart, availableStart, availableEnd);
      state.rangeEnd = clamp(state.rangeEnd, state.rangeStart + 1000, availableEnd);
      state.cursor = clamp(state.cursor, state.rangeStart, state.rangeEnd);
    }
    await loadRange();
    updateSummary();
    if (resetRange) requestAnimationFrame(scrollTranscriptToCursor);
  } catch (error) {
    setLoading(`The selected archive could not be read. ${error.message}`);
  } finally {
    els.refreshButton.disabled = false;
  }
}

async function loadRange() {
  setLoading("Loading this time range…");
  state.cursor = clamp(state.cursor, state.rangeStart, state.rangeEnd);
  const params = new URLSearchParams({
    start: new Date(state.rangeStart).toISOString(),
    end: new Date(state.rangeEnd).toISOString(),
    devices: selectedQuery(),
  });
  const response = await fetch(`/api/archive/entries?${params}`, { cache: "no-store" });
  if (!response.ok) throw new Error(await apiError(response));
  const payload = await response.json();
  state.entries = payload.entries || [];
  state.clips = state.entries.filter(entry => entry.audio).sort((a, b) =>
    Date.parse(a.audio.start) - Date.parse(b.audio.start));
  state.activeClipIndex = state.clips.findIndex(entry => entry.id === state.activeEntryId);
  if (state.activeEntryId && state.activeClipIndex < 0) {
    els.audioPlayer.pause();
    els.audioPlayer.removeAttribute("src");
    els.audioPlayer.load();
    state.activeEntryId = null;
  }
  applySearch();
  renderTimeline();
  updateTransportAvailability();
}

function bindEvents() {
  els.refreshButton.addEventListener("click", async () => { await loadFleet(); await refreshArchive(false); });
  els.selectAllDevices.addEventListener("change", async () => {
    state.selectedDevices = els.selectAllDevices.checked ? new Set(state.fleet.devices.map(device => device.id)) : new Set([state.fleet.devices.find(device => device.isLocal)?.id].filter(Boolean));
    renderDevices(); await refreshArchive(true);
  });
  els.syncSelectedButton.addEventListener("click", syncSelected);
  els.schedulePreset.addEventListener("change", () => { els.cronField.hidden = els.schedulePreset.value !== "custom"; });
  els.scheduleForm.addEventListener("submit", saveSchedule);
  els.playButton.addEventListener("click", togglePlayback);
  els.backButton.addEventListener("click", () => movePlayback(-15));
  els.forwardButton.addEventListener("click", () => movePlayback(15));
  els.skipSilence.addEventListener("change", enforceTrimBounds);
  els.startInput.addEventListener("change", () => applyInputRange("start"));
  els.endInput.addEventListener("change", () => applyInputRange("end"));
  els.searchInput.addEventListener("input", () => {
    clearTimeout(state.searchTimer);
    state.searchTimer = setTimeout(applySearch, 120);
  });
  document.addEventListener("keydown", event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
      event.preventDefault();
      els.searchInput.focus();
      els.searchInput.select();
    }
    if (event.code === "Space" && !/INPUT|BUTTON/.test(document.activeElement?.tagName || "")) {
      event.preventDefault();
      togglePlayback();
    }
  });

  for (const [handleName, handle] of [["start", els.startHandle], ["cursor", els.cursorHandle], ["end", els.endHandle]]) {
    handle.addEventListener("pointerdown", event => beginTimelineDrag(event, handleName));
    handle.addEventListener("keydown", event => adjustHandleWithKeyboard(event, handleName));
  }
  els.timeline.addEventListener("pointerdown", event => {
    if (event.target.closest(".timeline-handle")) return;
    beginTimelineDrag(event, "cursor");
  });
  window.addEventListener("pointermove", moveTimelineDrag);
  window.addEventListener("pointerup", endTimelineDrag);
  els.transcriptScroller.addEventListener("scroll", handleManualTranscriptScroll, { passive: true });
  els.transcriptScroller.addEventListener("wheel", suspendPlaybackFollow, { passive: true });
  els.transcriptScroller.addEventListener("touchstart", suspendPlaybackFollow, { passive: true });
  els.transcriptScroller.addEventListener("pointerdown", suspendPlaybackFollow, { passive: true });

  els.audioPlayer.addEventListener("loadedmetadata", handleAudioReady);
  els.audioPlayer.addEventListener("timeupdate", handleAudioProgress);
  els.audioPlayer.addEventListener("play", () => {
    els.playButton.classList.add("is-playing");
    els.playButton.setAttribute("aria-label", "Pause");
    els.playbackStatus.textContent = "PLAYING CONTINUOUS AUDIO";
  });
  els.audioPlayer.addEventListener("pause", () => {
    els.playButton.classList.remove("is-playing");
    els.playButton.setAttribute("aria-label", "Play");
    if (!els.audioPlayer.ended) els.playbackStatus.textContent = "PAUSED";
  });
  els.audioPlayer.addEventListener("ended", playNextClip);
  els.audioPlayer.addEventListener("error", playNextClip);
}

async function downloadDevice(device) {
  try {
    showToast(`Downloading ${device.name}…`);
    const response = await fetch(`/api/devices/${device.id}/download`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ start: new Date(state.rangeStart).toISOString(), end: new Date(state.rangeEnd).toISOString() })
    });
    if (!response.ok) throw new Error(await apiError(response));
    const result = await response.json();
    showToast(`Downloaded ${result.transcriptFiles} transcript file(s) and ${result.audioFiles} audio recording(s) from ${device.name}.`);
    await refreshArchive(false);
  } catch (error) { showToast(error.message, true); }
}

async function syncSelected() {
  const remoteIds = state.fleet.devices.filter(device => !device.isLocal && state.selectedDevices.has(device.id)).map(device => device.id);
  if (!remoteIds.length) return;
  els.syncSelectedButton.disabled = true;
  try {
    showToast(`Syncing ${remoteIds.length} selected ${remoteIds.length === 1 ? "device" : "devices"}…`);
    const response = await fetch("/api/devices/sync", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ deviceIds: remoteIds, start: new Date(state.rangeStart).toISOString(), end: new Date(state.rangeEnd).toISOString() })
    });
    if (!response.ok) throw new Error(await apiError(response));
    const results = await response.json();
    const files = results.reduce((sum, result) => sum + result.transcriptFiles + result.audioFiles, 0);
    showToast(`Sync complete · ${files} transcription file(s) copied. Originals were kept.`);
    await refreshArchive(false);
  } catch (error) { showToast(error.message, true); }
  finally { renderDevices(); }
}

async function openSchedule(device) {
  state.scheduleDeviceId = device.id;
  els.scheduleTitle.textContent = `Transfer schedule · ${device.name}`;
  els.schedulePreset.value = "0 * * * *";
  els.scheduleCron.value = "0 * * * *";
  els.scheduleEnabled.checked = true;
  els.scheduleMove.checked = false;
  els.cronField.hidden = true;
  try {
    const response = await fetch("/api/schedules", { cache: "no-store" });
    if (response.ok) {
      const schedules = await response.json();
      const existing = schedules.find(schedule => schedule.deviceId === device.id);
      if (existing) {
        const presets = [...els.schedulePreset.options].map(option => option.value);
        els.schedulePreset.value = presets.includes(existing.cronExpression) ? existing.cronExpression : "custom";
        els.scheduleCron.value = existing.cronExpression;
        els.cronField.hidden = els.schedulePreset.value !== "custom";
        els.scheduleEnabled.checked = existing.enabled;
        els.scheduleMove.checked = String(existing.mode).toLowerCase() === "move";
      }
    }
  } catch { }
  els.scheduleDialog.showModal();
}

async function saveSchedule(event) {
  event.preventDefault();
  if (!state.scheduleDeviceId) return;
  const cronExpression = els.schedulePreset.value === "custom" ? els.scheduleCron.value.trim() : els.schedulePreset.value;
  els.saveSchedule.disabled = true;
  try {
    const response = await fetch(`/api/devices/${state.scheduleDeviceId}/schedule`, {
      method: "PUT", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ cronExpression, enabled: els.scheduleEnabled.checked, deleteFromOrigin: els.scheduleMove.checked })
    });
    if (!response.ok) throw new Error(await apiError(response));
    els.scheduleDialog.close();
    showToast(els.scheduleMove.checked ? "Schedule saved. Origin files will be deleted only after verified transfer." : "Schedule saved. Origin files will be kept.");
  } catch (error) { showToast(error.message, true); }
  finally { els.saveSchedule.disabled = false; }
}

function showToast(message, error = false) {
  clearTimeout(state.toastTimer);
  els.toast.textContent = message;
  els.toast.classList.toggle("error", error);
  els.toast.hidden = false;
  state.toastTimer = setTimeout(() => { els.toast.hidden = true; }, error ? 7000 : 4200);
}

async function apiError(response) {
  try { return (await response.json()).error || `Request failed (${response.status})`; }
  catch { return `Request failed (${response.status})`; }
}
function renderTranscript() {
  els.transcriptList.replaceChildren();
  els.loadingState.hidden = state.visibleEntries.length > 0;
  if (!state.visibleEntries.length) {
    const query = els.searchInput.value.trim();
    els.loadingState.textContent = query
      ? `No transcript in this range contains “${query}”.`
      : "No transcript text falls inside this time range.";
    els.matchCount.textContent = query ? "0 matches" : "0 entries";
    return;
  }

  const fragment = document.createDocumentFragment();
  const query = els.searchInput.value.trim();
  for (const entry of state.visibleEntries) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "transcript-entry";
    button.dataset.entryId = entry.id;
    button.dataset.timestamp = Date.parse(entry.timestamp);
    if (entry.id === state.activeEntryId) button.classList.add("active");
    button.setAttribute("aria-label", `${entry.deviceName}. ${formatFull(entry.timestamp)}. ${entry.text}`);

    const time = document.createElement("span");
    time.className = "entry-time";
    time.textContent = timeFormatter.format(new Date(entry.timestamp));
    const day = document.createElement("small");
    day.textContent = dayFormatter.format(new Date(entry.timestamp));
    const deviceLabel = document.createElement("span");
    deviceLabel.className = "entry-device";
    deviceLabel.textContent = entry.deviceName;
    time.append(day, deviceLabel);

    const text = document.createElement("span");
    text.className = "entry-text";
    appendHighlightedText(text, entry.text, query);

    const badge = document.createElement("span");
    badge.className = `audio-badge${entry.audio ? "" : " missing"}`;
    badge.textContent = entry.audio ? `${entry.confidence}% · AUDIO` : "TEXT ONLY";
    button.append(time, text, badge);
    button.addEventListener("click", () => selectEntry(entry, true));
    fragment.append(button);
  }
  els.transcriptList.append(fragment);
  els.matchCount.textContent = query
    ? `${state.visibleEntries.length} ${state.visibleEntries.length === 1 ? "match" : "matches"}`
    : `${state.visibleEntries.length} ${state.visibleEntries.length === 1 ? "entry" : "entries"}`;
}

function appendHighlightedText(container, text, query) {
  if (!query) {
    container.textContent = text;
    return;
  }
  const lowerText = text.toLocaleLowerCase();
  const lowerQuery = query.toLocaleLowerCase();
  let position = 0;
  while (position < text.length) {
    const match = lowerText.indexOf(lowerQuery, position);
    if (match < 0) {
      container.append(document.createTextNode(text.slice(position)));
      break;
    }
    if (match > position) container.append(document.createTextNode(text.slice(position, match)));
    const mark = document.createElement("mark");
    mark.textContent = text.slice(match, match + query.length);
    container.append(mark);
    position = match + query.length;
  }
}

function applySearch() {
  const query = els.searchInput.value.trim().toLocaleLowerCase();
  state.visibleEntries = query
    ? state.entries.filter(entry => entry.text.toLocaleLowerCase().includes(query))
    : [...state.entries];
  renderTranscript();
  renderActivityMarks();
}

function renderTimeline(includeActivityMarks = true) {
  const availableStart = Date.parse(state.summary.availableStart);
  const availableEnd = Date.parse(state.summary.availableEnd);
  const span = Math.max(1, availableEnd - availableStart);
  const startPercent = ((state.rangeStart - availableStart) / span) * 100;
  const endPercent = ((state.rangeEnd - availableStart) / span) * 100;
  const cursorPercent = ((state.cursor - availableStart) / span) * 100;
  const draggedPercent = state.dragging?.visualPercent;
  const displayedStartPercent = state.dragging?.handleName === "start" ? draggedPercent : startPercent;
  const displayedEndPercent = state.dragging?.handleName === "end" ? draggedPercent : endPercent;
  const displayedCursorPercent = state.dragging?.handleName === "cursor" ? draggedPercent : cursorPercent;
  els.startHandle.style.left = `${displayedStartPercent}%`;
  els.endHandle.style.left = `${displayedEndPercent}%`;
  els.cursorHandle.style.left = `${displayedCursorPercent}%`;
  els.timelineSelection.style.left = `${displayedStartPercent}%`;
  els.timelineSelection.style.width = `${Math.max(0, displayedEndPercent - displayedStartPercent)}%`;
  els.startInput.value = toLocalInputValue(state.rangeStart);
  els.endInput.value = toLocalInputValue(state.rangeEnd);
  els.cursorOutput.textContent = dateTimeFormatter.format(new Date(state.cursor));
  setSliderAria(els.startHandle, availableStart, availableEnd, state.rangeStart);
  setSliderAria(els.cursorHandle, state.rangeStart, state.rangeEnd, state.cursor);
  setSliderAria(els.endHandle, availableStart, availableEnd, state.rangeEnd);
  if (includeActivityMarks) renderActivityMarks();
}

function renderActivityMarks() {
  els.activityMarks.replaceChildren();
  if (!state.summary) return;
  const availableStart = Date.parse(state.summary.availableStart);
  const availableEnd = Date.parse(state.summary.availableEnd);
  const span = Math.max(1, availableEnd - availableStart);
  const entries = state.visibleEntries.length > 600
    ? state.visibleEntries.filter((_, index) => index % Math.ceil(state.visibleEntries.length / 600) === 0)
    : state.visibleEntries;
  const fragment = document.createDocumentFragment();
  for (const entry of entries) {
    const mark = document.createElement("i");
    mark.className = "activity-mark";
    mark.style.left = `${((Date.parse(entry.timestamp) - availableStart) / span) * 100}%`;
    fragment.append(mark);
  }
  els.activityMarks.append(fragment);
}

function beginTimelineDrag(event, handleName) {
  event.preventDefault();
  if (!state.summary) return;
  if (handleName === "cursor" && !event.target.closest(".timeline-handle")) {
    state.cursor = timelineValueAt(event.clientX);
  }
  state.dragging = {
    handleName,
    originTime: handleName === "start" ? state.rangeStart : handleName === "end" ? state.rangeEnd : state.cursor,
    originClientX: event.clientX,
  };
  els.timeline.setPointerCapture?.(event.pointerId);
  updateDraggedHandle(event.clientX);
}

function moveTimelineDrag(event) {
  if (!state.dragging) return;
  updateDraggedHandle(event.clientX);
}

async function endTimelineDrag() {
  if (!state.dragging) return;
  const { handleName: dragged } = state.dragging;
  state.dragging = null;
  els.timelineDragOffset.hidden = true;
  if (dragged === "cursor") {
    state.followPlayback = true;
    scrollTranscriptToCursor();
    seekToWallTime(state.cursor, false);
  } else {
    state.cursor = clamp(state.cursor, state.rangeStart, state.rangeEnd);
    await loadRange();
  }
}

function updateDraggedHandle(clientX) {
  const drag = state.dragging;
  if (!drag) return;
  const rect = els.timeline.getBoundingClientRect();
  const availableStart = Date.parse(state.summary.availableStart);
  const availableEnd = Date.parse(state.summary.availableEnd);
  const value = nonLinearDragValue(drag, clientX, rect, availableStart, availableEnd);
  drag.visualPercent = clamp((clientX - rect.left) / rect.width, 0, 1) * 100;
  if (drag.handleName === "start") {
    state.rangeStart = Math.min(value, state.rangeEnd - 1000);
    if (state.cursor < state.rangeStart) state.cursor = state.rangeStart;
  } else if (drag.handleName === "end") {
    state.rangeEnd = Math.max(value, state.rangeStart + 1000);
    if (state.cursor > state.rangeEnd) state.cursor = state.rangeEnd;
  } else {
    state.cursor = clamp(value, state.rangeStart, state.rangeEnd);
    scrollTranscriptToCursor();
  }
  renderTimelineDragOffset(clientX, rect, value - drag.originTime);
  renderTimeline(false);
}

function timelineValueAt(clientX) {
  const rect = els.timeline.getBoundingClientRect();
  const availableStart = Date.parse(state.summary.availableStart);
  const availableEnd = Date.parse(state.summary.availableEnd);
  return availableStart + clamp((clientX - rect.left) / rect.width, 0, 1) * (availableEnd - availableStart);
}

function nonLinearDragValue(drag, clientX, rect, availableStart, availableEnd) {
  const deltaPixels = clientX - drag.originClientX;
  const direction = Math.sign(deltaPixels);
  if (!direction) return drag.originTime;

  const bounds = dragBounds(drag.handleName, drag.originTime, availableStart, availableEnd);
  const maxDelta = direction < 0 ? bounds.backward : bounds.forward;
  const availablePixels = direction < 0
    ? drag.originClientX - rect.left
    : rect.right - drag.originClientX;
  // Squaring the pointer distance gives precise adjustments close to the setpoint
  // and accelerates smoothly toward the earliest/latest available timestamp.
  const progress = clamp(Math.abs(deltaPixels) / Math.max(1, availablePixels), 0, 1);
  return drag.originTime + direction * maxDelta * progress * progress;
}

function dragBounds(handleName, originTime, availableStart, availableEnd) {
  if (handleName === "start") {
    return { backward: originTime - availableStart, forward: state.rangeEnd - 1000 - originTime };
  }
  if (handleName === "end") {
    return { backward: originTime - state.rangeStart - 1000, forward: availableEnd - originTime };
  }
  return { backward: originTime - state.rangeStart, forward: state.rangeEnd - originTime };
}

function renderTimelineDragOffset(clientX, rect, deltaMilliseconds) {
  const offset = Math.round(deltaMilliseconds / 86400000);
  const sign = offset > 0 ? "+" : "";
  els.timelineDragOffset.textContent = `${sign}${offset} ${Math.abs(offset) === 1 ? "day" : "days"}`;
  els.timelineDragOffset.style.left = `${clamp(clientX - rect.left, 0, rect.width)}px`;
  els.timelineDragOffset.hidden = false;
}

function adjustHandleWithKeyboard(event, handleName) {
  if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
  event.preventDefault();
  const direction = event.key === "ArrowLeft" ? -1 : event.key === "ArrowRight" ? 1 : 0;
  const span = Math.max(1000, state.rangeEnd - state.rangeStart);
  const step = event.shiftKey ? 60 * 60 * 1000 : Math.max(1000, span / 100);
  if (handleName === "cursor") {
    state.cursor = event.key === "Home" ? state.rangeStart
      : event.key === "End" ? state.rangeEnd
      : clamp(state.cursor + direction * step, state.rangeStart, state.rangeEnd);
    renderTimeline();
    scrollTranscriptToCursor();
    seekToWallTime(state.cursor, false);
    return;
  }
  if (handleName === "start") state.rangeStart = clamp(state.rangeStart + direction * step, Date.parse(state.summary.availableStart), state.rangeEnd - 1000);
  if (handleName === "end") state.rangeEnd = clamp(state.rangeEnd + direction * step, state.rangeStart + 1000, Date.parse(state.summary.availableEnd));
  state.cursor = clamp(state.cursor, state.rangeStart, state.rangeEnd);
  renderTimeline();
  loadRange();
}

async function applyInputRange(which) {
  const parsed = Date.parse(which === "start" ? els.startInput.value : els.endInput.value);
  if (!Number.isFinite(parsed)) return;
  const availableStart = Date.parse(state.summary.availableStart);
  const availableEnd = Date.parse(state.summary.availableEnd);
  if (which === "start") state.rangeStart = clamp(parsed, availableStart, state.rangeEnd - 1000);
  else state.rangeEnd = clamp(parsed, state.rangeStart + 1000, availableEnd);
  state.cursor = clamp(state.cursor, state.rangeStart, state.rangeEnd);
  await loadRange();
  renderTimeline();
}

function scrollTranscriptToCursor() {
  if (!state.visibleEntries.length) return;
  const target = state.visibleEntries.find(entry => Date.parse(entry.timestamp) >= state.cursor)
    || state.visibleEntries[state.visibleEntries.length - 1];
  const element = els.transcriptList.querySelector(`[data-entry-id="${CSS.escape(target.id)}"]`);
  if (!element) return;
  state.programmaticScroll = true;
  const scrollerBounds = els.transcriptScroller.getBoundingClientRect();
  const elementBounds = element.getBoundingClientRect();
  els.transcriptScroller.scrollTo({
    top: Math.max(0, els.transcriptScroller.scrollTop + elementBounds.top - scrollerBounds.top),
    behavior: state.dragging ? "auto" : "smooth",
  });
  setTimeout(() => { state.programmaticScroll = false; }, state.dragging ? 40 : 400);
}

function handleManualTranscriptScroll() {
  if (state.programmaticScroll || state.dragging) return;
  state.followPlayback = false;
  clearTimeout(state.scrollTimer);
  state.scrollTimer = setTimeout(() => {
    const scrollerTop = els.transcriptScroller.getBoundingClientRect().top + 8;
    const items = [...els.transcriptList.querySelectorAll(".transcript-entry")];
    const topItem = items.find(item => item.getBoundingClientRect().bottom > scrollerTop);
    if (!topItem) return;
    state.cursor = clamp(Number(topItem.dataset.timestamp), state.rangeStart, state.rangeEnd);
    renderTimeline(false);
  }, 80);
}

function suspendPlaybackFollow() {
  state.followPlayback = false;
  state.programmaticScroll = false;
}

function selectEntry(entry, autoplay) {
  state.followPlayback = true;
  state.cursor = clamp(Date.parse(entry.timestamp), state.rangeStart, state.rangeEnd);
  renderTimeline(false);
  if (!entry.audio) {
    setActiveEntry(entry.id, state.followPlayback);
    els.playbackStatus.textContent = "TEXT ONLY";
    els.playbackTime.textContent = formatFull(entry.timestamp);
    return;
  }
  const index = state.clips.findIndex(candidate => candidate.id === entry.id);
  if (index >= 0) loadClip(index, effectiveClipStart(entry), autoplay);
}

function togglePlayback() {
  if (!state.clips.length) return;
  if (!els.audioPlayer.src) {
    seekToWallTime(state.cursor, true);
  } else if (els.audioPlayer.paused) {
    enforceTrimBounds();
    els.audioPlayer.play().catch(() => {});
  } else {
    els.audioPlayer.pause();
  }
}

function loadClip(index, seekSeconds, autoplay) {
  if (index < 0 || index >= state.clips.length) {
    els.audioPlayer.pause();
    els.playbackStatus.textContent = "END OF RANGE";
    return;
  }
  const entry = state.clips[index];
  state.activeClipIndex = index;
  state.activeEntryId = entry.id;
  els.audioPlayer.dataset.pendingSeek = String(clamp(seekSeconds, 0, entry.audio.durationSeconds));
  els.audioPlayer.dataset.autoplay = autoplay ? "true" : "false";
  els.audioPlayer.src = entry.audio.url;
  els.audioPlayer.load();
  setActiveEntry(entry.id, state.followPlayback);
  els.playbackStatus.textContent = "LOADING RECORDING";
}

function handleAudioReady() {
  const entry = state.clips[state.activeClipIndex];
  if (!entry) return;
  const requested = Number(els.audioPlayer.dataset.pendingSeek || effectiveClipStart(entry));
  els.audioPlayer.currentTime = clamp(requested, effectiveClipStart(entry), effectiveClipEnd(entry));
  updatePlaybackClock(entry);
  if (els.audioPlayer.dataset.autoplay === "true") els.audioPlayer.play().catch(() => {});
}

function handleAudioProgress() {
  const entry = state.clips[state.activeClipIndex];
  if (!entry) return;
  if (els.skipSilence.checked && els.audioPlayer.currentTime < entry.audio.trimStartSeconds) {
    els.audioPlayer.currentTime = entry.audio.trimStartSeconds;
  }
  if (els.audioPlayer.currentTime >= effectiveClipEnd(entry) - .04 && !els.audioPlayer.paused) {
    playNextClip();
    return;
  }
  const wallTime = Date.parse(entry.audio.start) + els.audioPlayer.currentTime * 1000;
  state.cursor = clamp(wallTime, state.rangeStart, state.rangeEnd);
  renderTimeline(false);
  updatePlaybackClock(entry);
  setActiveEntry(entry.id, state.followPlayback);
}

function playNextClip() {
  const wasPlaying = !els.audioPlayer.paused || els.audioPlayer.ended;
  const next = state.activeClipIndex + 1;
  if (next >= state.clips.length) {
    els.audioPlayer.pause();
    els.playbackStatus.textContent = "END OF RANGE";
    return;
  }
  loadClip(next, effectiveClipStart(state.clips[next]), wasPlaying);
}

function movePlayback(seconds) {
  const entry = state.clips[state.activeClipIndex];
  const currentWall = entry
    ? Date.parse(entry.audio.start) + els.audioPlayer.currentTime * 1000
    : state.cursor;
  seekToWallTime(clamp(currentWall + seconds * 1000, state.rangeStart, state.rangeEnd), !els.audioPlayer.paused);
}

function seekToWallTime(wallTime, autoplay) {
  if (!state.clips.length) return;
  let index = state.clips.findIndex(entry => {
    const start = Date.parse(entry.audio.start);
    const end = Date.parse(entry.audio.end);
    return wallTime >= start && wallTime <= end;
  });
  if (index < 0) {
    index = state.clips.findIndex(entry => Date.parse(entry.audio.start) >= wallTime);
    if (index < 0) index = state.clips.length - 1;
  }
  const entry = state.clips[index];
  const seek = clamp((wallTime - Date.parse(entry.audio.start)) / 1000, effectiveClipStart(entry), effectiveClipEnd(entry));
  loadClip(index, seek, autoplay);
}

function enforceTrimBounds() {
  const entry = state.clips[state.activeClipIndex];
  if (!entry || !els.skipSilence.checked) return;
  if (els.audioPlayer.currentTime < entry.audio.trimStartSeconds) els.audioPlayer.currentTime = entry.audio.trimStartSeconds;
  if (els.audioPlayer.currentTime > entry.audio.trimEndSeconds) playNextClip();
}

function effectiveClipStart(entry) { return els.skipSilence.checked ? entry.audio.trimStartSeconds : 0; }
function effectiveClipEnd(entry) { return els.skipSilence.checked ? entry.audio.trimEndSeconds : entry.audio.durationSeconds; }

function setActiveEntry(id, keepVisible = false) {
  if (state.activeEntryId !== id) state.activeEntryId = id;
  for (const element of els.transcriptList.querySelectorAll(".transcript-entry.active")) element.classList.remove("active");
  const active = els.transcriptList.querySelector(`[data-entry-id="${CSS.escape(id)}"]`);
  if (!active) return;
  active.classList.add("active");
  if (keepVisible) {
    const viewport = els.transcriptScroller.getBoundingClientRect();
    const bounds = active.getBoundingClientRect();
    if (bounds.top < viewport.top + 20 || bounds.bottom > viewport.bottom - 20) {
      state.programmaticScroll = true;
      els.transcriptScroller.scrollTo({
        top: Math.max(
          0,
          els.transcriptScroller.scrollTop + bounds.top - viewport.top
            - (els.transcriptScroller.clientHeight - active.offsetHeight) / 2,
        ),
        behavior: "smooth",
      });
      setTimeout(() => { state.programmaticScroll = false; }, 400);
    }
  }
}

function updatePlaybackClock(entry) {
  const wallTime = Date.parse(entry.audio.start) + els.audioPlayer.currentTime * 1000;
  els.playbackTime.textContent = `${dateTimeFormatter.format(new Date(wallTime))} · ${formatDuration(els.audioPlayer.currentTime)} in this recording`;
}

function updateTransportAvailability() {
  const disabled = state.clips.length === 0;
  els.playButton.disabled = disabled;
  els.backButton.disabled = disabled;
  els.forwardButton.disabled = disabled;
  if (disabled) {
    els.playbackStatus.textContent = state.entries.length ? "NO RETAINED AUDIO IN RANGE" : "NO TRANSCRIPT IN RANGE";
    els.playbackTime.textContent = "Adjust the range or refresh the archive";
  }
}

function updateSummary() {
  const transcripts = state.summary.transcriptCount;
  const recordings = state.summary.recordingCount;
  const deviceCount = state.selectedDevices.size;
  els.archiveSummary.textContent = `${transcripts.toLocaleString()} transcript ${transcripts === 1 ? "entry" : "entries"} · ${recordings.toLocaleString()} retained ${recordings === 1 ? "recording" : "recordings"} · ${deviceCount} selected ${deviceCount === 1 ? "device" : "devices"}`;
}

function setLoading(message) {
  els.loadingState.hidden = false;
  els.loadingState.textContent = message;
  els.transcriptList.replaceChildren();
}

function setSliderAria(element, min, max, now) {
  element.setAttribute("aria-valuemin", String(Math.round(min)));
  element.setAttribute("aria-valuemax", String(Math.round(max)));
  element.setAttribute("aria-valuenow", String(Math.round(now)));
  element.setAttribute("aria-valuetext", dateTimeFormatter.format(new Date(now)));
}

function toLocalInputValue(milliseconds) {
  const date = new Date(milliseconds);
  const offset = date.getTimezoneOffset() * 60000;
  return new Date(milliseconds - offset).toISOString().slice(0, 19);
}

function formatFull(value) { return dateTimeFormatter.format(new Date(value)); }
function formatDuration(seconds) {
  const whole = Math.max(0, Math.floor(seconds));
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, "0")}`;
}
function clamp(value, min, max) { return Math.min(max, Math.max(min, value)); }

initialize();
