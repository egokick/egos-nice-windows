const MAX_HISTORY_HOURS = 2160;
const UNKNOWN_GAP_MS = 60 * 60 * 1000;

const appState = {
  devices: [],
  groups: [],
  groupFilters: readStoredGroupFilters(),
  expandedGroups: readStoredExpandedGroups(),
  hiddenTimelineChildren: readStoredHiddenTimelineChildren(),
  showIgnored: localStorage.getItem("wifiDevices:showIgnored") === "true",
  selected: readStoredSelection(),
  rangeHours: normalizeRangeHours(Number(localStorage.getItem("wifiDevices:rangeHours") || "168")),
  timelineOffsetHours: 0,
  history: { samples: [], events: [], bounds: { firstSampleUtc: null, lastSampleUtc: null } },
  nameTimers: new Map(),
  timelineRenderFrame: null
};

let preferencesSaveTimer = null;
appState.timelineOffsetHours = normalizeTimelineOffset(Number(localStorage.getItem("wifiDevices:timelineOffsetHours") || "0"));

const els = {
  pollSummary: document.querySelector("#pollSummary"),
  sourceStatus: document.querySelector("#sourceStatus"),
  pollAlert: document.querySelector("#pollAlert"),
  onlineCount: document.querySelector("#onlineCount"),
  knownCount: document.querySelector("#knownCount"),
  exportSettings: document.querySelector("#exportSettings"),
  pollNow: document.querySelector("#pollNow"),
  selectAll: document.querySelector("#selectAll"),
  selectNone: document.querySelector("#selectNone"),
  deviceSearch: document.querySelector("#deviceSearch"),
  showIgnored: document.querySelector("#showIgnored"),
  groupFilterButton: document.querySelector("#groupFilterButton"),
  groupFilterMenu: document.querySelector("#groupFilterMenu"),
  newGroupName: document.querySelector("#newGroupName"),
  createGroup: document.querySelector("#createGroup"),
  deviceList: document.querySelector("#deviceList"),
  selectionSummary: document.querySelector("#selectionSummary"),
  timelineRangeControl: document.querySelector("#timelineRangeControl"),
  timelineStart: document.querySelector("#timelineStart"),
  timelineEnd: document.querySelector("#timelineEnd"),
  timelineStartLabel: document.querySelector("#timelineStartLabel"),
  timelineEndLabel: document.querySelector("#timelineEndLabel"),
  timelineRangeLength: document.querySelector("#timelineRangeLength"),
  timelineRangeSelection: document.querySelector("#timelineRangeSelection"),
  timelineMinLabel: document.querySelector("#timelineMinLabel"),
  timelineMaxLabel: document.querySelector("#timelineMaxLabel"),
  timelineRangeHelp: document.querySelector("#timelineRangeHelp"),
  timelineChart: document.querySelector("#timelineChart"),
  heatmap: document.querySelector("#heatmap"),
  heatmapCaption: document.querySelector("#heatmapCaption"),
  eventList: document.querySelector("#eventList"),
  eventCount: document.querySelector("#eventCount")
};

els.pollNow.addEventListener("click", async () => {
  els.pollNow.disabled = true;
  try {
    await fetchJson("/api/poll", { method: "POST" });
    await refreshAll();
  } finally {
    els.pollNow.disabled = false;
  }
});

els.exportSettings.addEventListener("click", () => {
  const settings = JSON.stringify(preferencesPayload(), null, 2);
  const blob = new Blob([settings], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = "wifi-devices-settings.json";
  link.click();
  URL.revokeObjectURL(url);
});

els.selectAll.addEventListener("click", () => {
  appState.selected = new Set(appState.devices
    .filter(device => !device.ignored || appState.showIgnored)
    .map(device => device.mac));
  persistSelection();
  renderDevices();
  loadHistory();
});

els.selectNone.addEventListener("click", () => {
  appState.selected.clear();
  persistSelection();
  renderDevices();
  renderHistory();
});

els.deviceSearch.addEventListener("input", renderDevices);

els.showIgnored.addEventListener("change", () => {
  appState.showIgnored = els.showIgnored.checked;
  persistUiPreferences();
  pruneSelection();
  renderDevices();
  loadHistory();
});

els.groupFilterButton.addEventListener("click", event => {
  event.stopPropagation();
  els.groupFilterMenu.hidden = !els.groupFilterMenu.hidden;
});

document.addEventListener("click", event => {
  if (!event.target.closest(".group-filter")) {
    els.groupFilterMenu.hidden = true;
  }
});

els.createGroup.addEventListener("click", async () => {
  const name = els.newGroupName.value.trim();
  if (!name) {
    return;
  }

  await fetchJson("/api/groups", {
    method: "POST",
    body: JSON.stringify({ name })
  });
  els.newGroupName.value = "";
  await refreshAll();
  appState.groupFilters = [name];
  persistGroupFilters();
  renderGroupControls();
  renderDevices();
});

async function deleteGroup(group) {
  await fetchJson(`/api/groups/${encodeURIComponent(group)}`, { method: "DELETE" });
  appState.groupFilters = appState.groupFilters.filter(value => value !== group);
  persistGroupFilters();
  await refreshAll();
}

for (const [input, handle] of [[els.timelineStart, "start"], [els.timelineEnd, "end"]]) {
  input.addEventListener("input", () => updateTimelineRangeFromInput(handle));
  input.addEventListener("change", commitTimelineRange);
  input.addEventListener("pointerdown", () => setActiveTimelineHandle(input));
  input.addEventListener("focus", () => setActiveTimelineHandle(input));
}

els.timelineChart.parentElement.addEventListener("auxclick", event => {
  if (event.button === 1) {
    event.preventDefault();
  }
});

window.addEventListener("resize", () => {
  renderTimeline();
});

initializeApp();

async function initializeApp() {
  try {
    const preferences = await fetchJson("/api/ui-preferences");
    if (preferences.isConfigured) {
      applyUiPreferences(preferences);
    } else {
      await saveUiPreferences();
    }
  } catch (error) {
    console.warn("Unable to load shared Wi-Fi preferences; continuing with local preferences.", error);
  }
  syncTimelineRangeControl();
  els.showIgnored.checked = appState.showIgnored;
  await refreshAll();
  setInterval(refreshAll, 30000);
}

function readStoredGroupFilters() {
  try {
    const parsed = JSON.parse(localStorage.getItem("wifiDevices:groupFilters") || "[]");
    if (Array.isArray(parsed)) {
      return parsed.filter(value => typeof value === "string" && value.trim());
    }
  } catch {
    const legacy = localStorage.getItem("wifiDevices:groupFilter");
    return legacy ? [legacy] : [];
  }

  const legacy = localStorage.getItem("wifiDevices:groupFilter");
  return legacy ? [legacy] : [];
}

function persistGroupFilters() {
  persistUiPreferences();
}

function normalizeRangeHours(hours) {
  const parsed = Number(hours);
  if (!Number.isFinite(parsed)) {
    return 168;
  }

  return Math.min(MAX_HISTORY_HOURS, Math.max(6, Math.round(parsed)));
}

function normalizeTimelineOffset(hours) {
  const parsed = Number(hours);
  return Number.isFinite(parsed) ? Math.min(MAX_HISTORY_HOURS, Math.max(0, Math.round(parsed))) : 0;
}

function formatRangeLabel(hours) {
  if (hours >= 24) {
    const days = Math.round((hours / 24) * 10) / 10;
    return `${days} day${days === 1 ? "" : "s"}`;
  }
  return `${hours} hour${hours === 1 ? "" : "s"}`;
}

function setActiveTimelineHandle(input) {
  els.timelineStart.classList.toggle("is-active", input === els.timelineStart);
  els.timelineEnd.classList.toggle("is-active", input === els.timelineEnd);
}

function timelineRangeDomain() {
  const end = currentTimelineEnd();
  const firstSample = appState.history?.bounds?.firstSampleUtc ? new Date(appState.history.bounds.firstSampleUtc) : null;
  if (!firstSample || Number.isNaN(firstSample.getTime())) {
    return { hasData: false, start: end, end, totalHours: 0 };
  }

  const historyHours = Math.max(1, Math.min(MAX_HISTORY_HOURS, Math.floor((end - firstSample) / (60 * 60 * 1000))));
  const start = new Date(end.getTime() - historyHours * 60 * 60 * 1000);
  return { hasData: true, start, end, totalHours: historyHours };
}

function selectedTimelineRange(domain = timelineRangeDomain()) {
  const visibleHours = Math.min(appState.rangeHours, domain.totalHours);
  const offsetHours = Math.min(appState.timelineOffsetHours, Math.max(0, domain.totalHours - visibleHours));
  const endHour = domain.totalHours - offsetHours;
  return { startHour: Math.max(0, endHour - visibleHours), endHour };
}

function updateTimelineRangeFromInput(handle) {
  const domain = timelineRangeDomain();
  if (!domain.hasData || domain.totalHours === 0) return;

  let startHour = Math.round(Number(els.timelineStart.value));
  let endHour = Math.round(Number(els.timelineEnd.value));
  if (handle === "start") {
    startHour = Math.min(Math.max(0, startHour), Math.max(0, endHour - 1));
  } else {
    endHour = Math.max(Math.min(domain.totalHours, endHour), Math.min(domain.totalHours, startHour + 1));
  }

  const selectedHours = Math.max(6, endHour - startHour);
  if (handle === "start" && endHour - startHour < 6) startHour = Math.max(0, endHour - 6);
  if (handle === "end" && endHour - startHour < 6) endHour = Math.min(domain.totalHours, startHour + 6);
  appState.rangeHours = normalizeRangeHours(selectedHours);
  appState.timelineOffsetHours = Math.max(0, domain.totalHours - endHour);
  syncTimelineRangeControl();
  scheduleTimelineRender();
}

function commitTimelineRange() {
  persistUiPreferences();
  renderTimelineDetails();
}

function scheduleTimelineRender() {
  if (appState.timelineRenderFrame !== null) return;
  appState.timelineRenderFrame = window.requestAnimationFrame(() => {
    appState.timelineRenderFrame = null;
    renderTimeline();
  });
}

function syncTimelineRangeControl() {
  const domain = timelineRangeDomain();
  for (const input of [els.timelineStart, els.timelineEnd]) {
    input.min = "0";
    input.max = String(domain.totalHours);
    input.step = "1";
    input.disabled = !domain.hasData || domain.totalHours < 1;
  }

  els.timelineRangeControl.classList.toggle("is-empty", !domain.hasData);
  if (!domain.hasData) {
    els.timelineStartLabel.textContent = "--";
    els.timelineEndLabel.textContent = "--";
    els.timelineRangeLength.textContent = "No timeline history";
    els.timelineRangeHelp.textContent = "Date controls are available after the first device sample is recorded.";
    els.timelineMinLabel.textContent = "Earliest data";
    els.timelineMaxLabel.textContent = "Latest data";
    els.timelineRangeSelection.style.left = "0%";
    els.timelineRangeSelection.style.width = "100%";
    return;
  }

  const { startHour, endHour } = selectedTimelineRange(domain);
  const start = new Date(domain.start.getTime() + startHour * 60 * 60 * 1000);
  const end = new Date(domain.start.getTime() + endHour * 60 * 60 * 1000);
  els.timelineStart.value = String(startHour);
  els.timelineEnd.value = String(endHour);
  els.timelineStartLabel.textContent = formatTimelineDateTime(start);
  els.timelineEndLabel.textContent = endHour === domain.totalHours ? `Now · ${formatTimelineDateTime(end)}` : formatTimelineDateTime(end);
  els.timelineRangeLength.textContent = formatRangeLabel(endHour - startHour);
  els.timelineRangeHelp.textContent = "Drag either handle, or use the arrow keys when focused.";
  els.timelineMinLabel.textContent = `Earliest data · ${formatTimelineDateTime(domain.start)}`;
  els.timelineMaxLabel.textContent = `Latest · ${formatTimelineDateTime(domain.end)}`;
  els.timelineStart.setAttribute("aria-valuetext", formatTimelineDateTime(start));
  els.timelineEnd.setAttribute("aria-valuetext", endHour === domain.totalHours ? `Now, ${formatTimelineDateTime(end)}` : formatTimelineDateTime(end));
  els.timelineRangeSelection.style.left = `${(startHour / domain.totalHours) * 100}%`;
  els.timelineRangeSelection.style.width = `${((endHour - startHour) / domain.totalHours) * 100}%`;
}
function readStoredExpandedGroups() {
  try {
    const parsed = JSON.parse(localStorage.getItem("wifiDevices:expandedGroups") || "[]");
    if (Array.isArray(parsed)) {
      return new Set(parsed.filter(value => typeof value === "string" && value.trim()));
    }
  } catch {
    return new Set();
  }

  return new Set();
}

function persistExpandedGroups() {
  persistUiPreferences();
}

function readStoredHiddenTimelineChildren() {
  try {
    const parsed = JSON.parse(localStorage.getItem("wifiDevices:hiddenTimelineChildren") || "[]");
    return new Set(Array.isArray(parsed) ? parsed.filter(Boolean) : []);
  } catch {
    return new Set();
  }
}

function persistHiddenTimelineChildren() {
  persistUiPreferences();
}

async function refreshAll() {
  try {
    const state = await fetchJson("/api/state");
    appState.devices = state.devices || [];
    appState.groups = state.groups || [];
    renderGroupControls();
    pruneSelection();
    ensureInitialSelection();
    renderSummary(state);
    renderDevices();
    await loadHistory();
  } catch (error) {
    els.pollSummary.textContent = `Unable to load state: ${error.message}`;
    els.pollSummary.classList.add("toast");
  }
}

async function loadHistory() {
  const historyMacs = historyMacsForCurrentView();
  if (historyMacs.length === 0) {
    appState.history = { samples: [], events: [], bounds: { firstSampleUtc: null, lastSampleUtc: null } };
    renderHistory();
    return;
  }

  const macs = encodeURIComponent(historyMacs.join(","));
  appState.history = prepareHistory(await fetchJson(`/api/history?hours=${MAX_HISTORY_HOURS}&macs=${macs}`));
  renderHistory();
}

function prepareHistory(history) {
  const samples = history.samples || [];
  const events = history.events || [];
  for (const sample of samples) sample.timelineMs = Date.parse(sample.sampledAtUtc);
  for (const event of events) event.timelineMs = Date.parse(event.atUtc);

  history.samplesByMac = groupBy(samples, sample => sample.mac);
  history.eventsByMac = groupBy(events, event => event.mac);
  for (const series of history.samplesByMac.values()) series.sort((a, b) => a.timelineMs - b.timelineMs);
  for (const series of history.eventsByMac.values()) series.sort((a, b) => a.timelineMs - b.timelineMs);
  history.groupSeriesByName = new Map();
  return history;
}

function timelineItemsInWindow(items, startMs, endMs) {
  const startIndex = lowerBound(items, startMs);
  const endIndex = upperBound(items, endMs);
  return items.slice(startIndex, endIndex);
}

function lowerBound(items, value) {
  let low = 0;
  let high = items.length;
  while (low < high) {
    const middle = (low + high) >>> 1;
    if (items[middle].timelineMs < value) low = middle + 1;
    else high = middle;
  }
  return low;
}

function upperBound(items, value) {
  let low = 0;
  let high = items.length;
  while (low < high) {
    const middle = (low + high) >>> 1;
    if (items[middle].timelineMs <= value) low = middle + 1;
    else high = middle;
  }
  return low;
}
function historyMacsForCurrentView() {
  if (appState.groupFilters.length > 0) {
    return appState.devices
      .filter(device =>
        (!device.ignored || appState.showIgnored)
        && (device.groups || []).some(group => appState.groupFilters.includes(group)))
      .map(device => device.mac);
  }

  return [...appState.selected];
}

async function fetchJson(url, options = {}) {
  const response = await fetch(url, {
    headers: { "Content-Type": "application/json" },
    ...options
  });

  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }

  return response.json();
}

function renderSummary(state) {
  els.onlineCount.textContent = String(state.onlineCount ?? 0);
  els.knownCount.textContent = String(state.knownCount ?? 0);

  const poll = state.poll || {};
  const completed = poll.lastCompletedUtc ? formatDateTime(poll.lastCompletedUtc) : "not completed";
  const source = poll.source || "no source";
  const status = poll.isRunning ? "polling" : poll.lastSucceeded ? "poll success" : "poll failed";
  els.pollSummary.textContent = `${status} - ${completed}`;
  els.sourceStatus.textContent = poll.lastSucceeded
    ? `${source} - ${poll.deviceCount || 0} devices`
    : poll.error || source;
  els.sourceStatus.classList.toggle("toast", Boolean(poll.error));
  renderPollAlert(poll);
}

function renderGroupControls() {
  const groups = appState.groups || [];
  appState.groupFilters = appState.groupFilters.filter(group => groups.includes(group));
  for (const expandedGroup of [...appState.expandedGroups]) {
    if (!groups.includes(expandedGroup)) {
      appState.expandedGroups.delete(expandedGroup);
    }
  }
  persistExpandedGroups();

  els.groupFilterMenu.textContent = "";
  const allRow = document.createElement("label");
  allRow.className = "group-filter-option";
  const allCheckbox = document.createElement("input");
  allCheckbox.type = "checkbox";
  allCheckbox.checked = appState.groupFilters.length === 0;
  allCheckbox.addEventListener("change", () => {
    appState.groupFilters = [];
    persistGroupFilters();
    renderGroupControls();
    renderDevices();
    loadHistory();
  });
  allRow.append(allCheckbox, document.createTextNode("All devices"));
  els.groupFilterMenu.append(allRow);

  for (const group of groups) {
    const filterOption = document.createElement("label");
    filterOption.className = "group-filter-option";
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = appState.groupFilters.includes(group);
    checkbox.addEventListener("change", () => {
      if (checkbox.checked) {
        appState.groupFilters = [...new Set([...appState.groupFilters, group])];
      } else {
        appState.groupFilters = appState.groupFilters.filter(value => value !== group);
      }
      persistGroupFilters();
      renderGroupControls();
      renderDevices();
      loadHistory();
    });
    const label = document.createElement("span");
    label.textContent = group;
    const deleteButton = document.createElement("button");
    deleteButton.type = "button";
    deleteButton.className = "group-delete";
    deleteButton.textContent = "x";
    deleteButton.title = `Delete ${group}`;
    deleteButton.setAttribute("aria-label", `Delete ${group}`);
    deleteButton.addEventListener("click", async event => {
      event.preventDefault();
      event.stopPropagation();
      await deleteGroup(group);
    });
    filterOption.append(checkbox, label, deleteButton);
    els.groupFilterMenu.append(filterOption);
  }

  els.groupFilterButton.textContent = appState.groupFilters.length === 0
    ? "All devices"
    : appState.groupFilters.length === 1
      ? appState.groupFilters[0]
      : `${appState.groupFilters.length} groups`;
  persistGroupFilters();
}

function renderPollAlert(poll) {
  const hasFailure = Boolean(poll.error);
  if (!hasFailure) {
    els.pollAlert.hidden = true;
    els.pollAlert.textContent = "";
    els.pollAlert.className = "poll-alert";
    return;
  }

  els.pollAlert.hidden = false;
  els.pollAlert.className = "poll-alert poll-alert-failed";
  els.pollAlert.textContent = `Device poll failed: ${poll.error}`;
}

function renderDevices() {
  const query = els.deviceSearch.value.trim().toLowerCase();
  const filtered = appState.devices.filter(device => {
    if (device.ignored && !appState.showIgnored) {
      return false;
    }

    const groups = device.groups || [];
    if (appState.groupFilters.length > 0 && !appState.groupFilters.some(group => groups.includes(group))) {
      return false;
    }

    const haystack = [
      device.displayName,
      device.name,
      ...(device.groups || []),
      device.hostName,
      device.networkName,
      device.networkBand,
      device.connectionType,
      device.mac,
      device.lastIpAddress
    ].filter(Boolean).join(" ").toLowerCase();
    return !query || haystack.includes(query);
  });

  els.deviceList.textContent = "";
  if (filtered.length === 0) {
    const empty = document.createElement("div");
    empty.className = "empty-state";
    empty.textContent = appState.devices.length === 0 ? "No devices recorded yet." : "No matching devices.";
    els.deviceList.append(empty);
    updateSelectionSummary();
    return;
  }

  for (const device of filtered) {
    const row = document.createElement("label");
    row.className = `device-row ${device.ignored ? "ignored" : ""}`;

    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = appState.selected.has(device.mac);
    checkbox.addEventListener("change", () => {
      if (checkbox.checked) {
        appState.selected.add(device.mac);
      } else {
        appState.selected.delete(device.mac);
      }
      persistSelection();
      updateSelectionSummary();
      loadHistory();
    });

    const dot = document.createElement("span");
    dot.className = `status-dot ${device.stale ? "stale" : device.online ? "online" : ""}`;

    const main = document.createElement("div");
    main.className = "device-main";

    const name = document.createElement("input");
    name.className = "device-name";
    name.value = device.name || "";
    name.placeholder = device.hostName ? `Alias for ${device.hostName}` : "Name this device";
    name.addEventListener("click", event => event.stopPropagation());
    name.addEventListener("input", () => queueNameSave(device.mac, name.value));
    name.addEventListener("keydown", event => {
      if (event.key === "Enter") {
        name.blur();
      }
    });

    const meta = document.createElement("div");
    meta.className = "device-meta";

    const pill = document.createElement("span");
    pill.className = `state-pill ${device.stale ? "stale" : device.online ? "online" : ""}`;
    pill.textContent = device.stale ? "stale" : device.online ? "online" : "offline";

    const mac = document.createElement("code");
    mac.textContent = device.mac;

    const ip = document.createElement("span");
    ip.textContent = device.lastIpAddress || "no IP";

    const routerName = document.createElement("span");
    routerName.textContent = device.hostName ? `device: ${device.hostName}` : "device: no name";

    const network = document.createElement("span");
    const networkParts = [device.networkName, device.networkBand, device.connectionType].filter(Boolean);
    network.textContent = networkParts.length > 0 ? `network: ${networkParts.join(" / ")}` : "network: unknown";

    const changed = document.createElement("span");
    changed.textContent = device.lastChangedUtc ? `changed ${formatRelative(device.lastChangedUtc)}` : "new";

    const groups = document.createElement("input");
    groups.className = "device-groups";
    groups.value = (device.groups || []).join(", ");
    groups.placeholder = "Groups";
    groups.addEventListener("click", event => event.stopPropagation());
    groups.addEventListener("change", async () => {
      const groupList = groups.value.split(",").map(value => value.trim()).filter(Boolean);
      await fetchJson(`/api/devices/${encodeURIComponent(device.mac)}/groups`, {
        method: "POST",
        body: JSON.stringify({ groups: groupList })
      });
      await refreshAll();
    });

    const actions = document.createElement("div");
    actions.className = "device-actions";
    const ignoreButton = document.createElement("button");
    ignoreButton.type = "button";
    ignoreButton.textContent = device.ignored ? "Unignore" : "Ignore";
    ignoreButton.addEventListener("click", async event => {
      event.preventDefault();
      event.stopPropagation();
      await setIgnored(device.mac, !device.ignored);
    });
    actions.append(ignoreButton);

    meta.append(pill, mac, ip, routerName, network, changed);
    main.append(name, meta, groups, actions);
    row.append(checkbox, dot, main);
    els.deviceList.append(row);
  }

  updateSelectionSummary();
}

function renderHistory() {
  updateSelectionSummary();
  renderTimeline();
  renderTimelineDetails();
}

function renderTimelineDetails() {
  renderHeatmap();
  renderEvents();
}

function renderTimeline() {
  const rows = chartRows();
  const svg = els.timelineChart;
  syncTimelineRangeControl();
  svg.textContent = "";

  if (rows.length === 0) {
    svg.setAttribute("height", "120");
    svg.setAttribute("viewBox", "0 0 760 120");
    drawSvgText(svg, 24, 62, appState.groupFilters.length > 0 ? "No matching group samples in range." : "Select devices to show their online timeline.", "empty-svg");
    return;
  }

  const width = Math.max(320, svg.parentElement.clientWidth - 16);
  const rowHeight = 50;
  const top = 34;
  const left = width < 620 ? 160 : 230;
  const right = width < 620 ? 10 : 24;
  const bottom = 36;
  const chartWidth = width - left - right;
  const height = top + bottom + rows.length * rowHeight;
  const { start, end, now } = timelineWindow();
  const startMs = start.getTime();
  const endMs = end.getTime();
  const nowMs = now.getTime();

  svg.setAttribute("height", String(height));
  svg.setAttribute("viewBox", `0 0 ${width} ${height}`);

  drawAxis(svg, start, end, left, top, chartWidth, height - bottom);
  if (now >= start && now <= end) {
    const nowX = timeToX(now, start, end, left, chartWidth);
    drawLine(svg, nowX, top - 14, nowX, height - bottom, "#bd4f43", 1, "now-line");
    drawSvgText(svg, Math.min(nowX + 6, left + chartWidth - 20), top - 18, "now", "axis-label");
  }

  rows.forEach((row, index) => {
    const y = top + index * rowHeight;
    const label = row.kind === "group"
      ? `${row.expanded ? "v" : ">"} ${row.label}`
      : row.label;
    const detail = row.detail;
    const labelX = row.kind === "device-child" ? 46 : 12;
    if (row.kind === "device-child") {
      drawEyeToggle(svg, 14, y + 10, row);
    }

    const labelElement = drawSvgText(
      svg,
      labelX,
      y + 19,
      label,
      row.kind === "group" ? "row-label row-label-toggle" : "row-label");
    if (row.kind === "group") {
      labelElement.setAttribute("role", "button");
      labelElement.setAttribute("tabindex", "0");
      labelElement.setAttribute("aria-label", `${row.expanded ? "Collapse" : "Expand"} ${row.label}`);
      const toggle = () => toggleGroupExpanded(row.key);
      labelElement.addEventListener("click", toggle);
      labelElement.addEventListener("keydown", event => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          toggle();
        }
      });
      if (row.expanded && row.hiddenChildren > 0) {
        drawShowHiddenToggle(svg, left - 30, y + 10, row);
      }
    }
    drawSvgText(svg, labelX, y + 36, detail, "row-sub-label");

    const samples = timelineItemsInWindow(row.samples, startMs, endMs);

    const trackY = y + 14;
    drawRect(svg, left, trackY, chartWidth, 18, "#edf1ee", "timeline-track-bg", 9);
    drawLine(svg, left, y + rowHeight - 2, width - right, y + rowHeight - 2, "#f0f2f0", 1, "row-divider");

    if (samples.length === 0) {
      drawSvgText(svg, left + 10, y + 27, "No samples in range", "no-samples");
    }

    const onlineSamples = samples.filter(sample => sample.online).length;
    const onlinePct = samples.length ? Math.round((onlineSamples / samples.length) * 100) : null;
    drawSvgText(svg, left - 56, y + 27, onlinePct === null ? "--" : `${onlinePct}%`, "row-percent");

    let onlineStart = null;
    let onlineEnd = null;
    const drawOnlineSegment = () => {
      if (onlineStart === null || onlineEnd === null || onlineEnd <= onlineStart) return;
      const x1 = timeToX(onlineStart, start, end, left, chartWidth);
      const x2 = timeToX(onlineEnd, start, end, left, chartWidth);
      const segmentWidth = Math.max(2, x2 - x1);
      drawRect(svg, Math.max(left, x2 - segmentWidth), trackY, segmentWidth, 18, "#16845f", "timeline-online", 9);
      onlineStart = null;
      onlineEnd = null;
    };

    for (let i = 0; i < samples.length; i++) {
      const current = samples[i];
      const next = samples[i + 1];
      const maxKnownUntil = current.timelineMs + UNKNOWN_GAP_MS;
      const nextTime = next?.timelineMs ?? nowMs;
      const segmentEnd = Math.min(nextTime, maxKnownUntil, nowMs, endMs);
      if (segmentEnd <= current.timelineMs) continue;

      if (!current.online) {
        drawOnlineSegment();
        continue;
      }
      if (onlineEnd !== null && current.timelineMs > onlineEnd) drawOnlineSegment();
      if (onlineStart === null) onlineStart = current.timelineMs;
      onlineEnd = segmentEnd;
    }
    drawOnlineSegment();

    const events = timelineItemsInWindow(row.events, startMs, endMs);

    for (const event of events) {
      const x = timeToX(event.timelineMs, start, end, left, chartWidth);
      drawLine(svg, x, trackY - 6, x, trackY + 24, event.online ? "#0e6147" : "#bd4f43", 2, "timeline-event-line");
      drawCircle(svg, x, trackY + 9, 4, event.online ? "#0e6147" : "#bd4f43", "timeline-event-dot");
    }
  });

  attachTimelineHover(svg, start, end, left, chartWidth, top, height - bottom, width);
}

function attachTimelineHover(svg, start, end, left, chartWidth, top, baseline, svgWidth) {
  const hover = document.createElementNS("http://www.w3.org/2000/svg", "g");
  hover.setAttribute("class", "timeline-hover");
  hover.setAttribute("visibility", "hidden");
  hover.setAttribute("pointer-events", "none");

  const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
  line.setAttribute("class", "timeline-hover-line");
  line.setAttribute("y1", String(top - 10));
  line.setAttribute("y2", String(baseline));
  hover.append(line);

  const labelBackground = document.createElementNS("http://www.w3.org/2000/svg", "rect");
  labelBackground.setAttribute("class", "timeline-hover-label-bg");
  labelBackground.setAttribute("y", "4");
  labelBackground.setAttribute("height", "22");
  labelBackground.setAttribute("rx", "5");
  hover.append(labelBackground);

  const label = document.createElementNS("http://www.w3.org/2000/svg", "text");
  label.setAttribute("class", "timeline-hover-label");
  label.setAttribute("y", "19");
  hover.append(label);
  svg.append(hover);

  const update = event => {
    const bounds = svg.getBoundingClientRect();
    if (!bounds.width) return;
    const x = ((event.clientX - bounds.left) / bounds.width) * svgWidth;
    if (x < left || x > left + chartWidth) {
      hover.setAttribute("visibility", "hidden");
      return;
    }

    const progress = (x - left) / chartWidth;
    const time = new Date(start.getTime() + (end.getTime() - start.getTime()) * progress);
    const text = formatTimelineDateTime(time);
    const labelWidth = text.length * 6.8 + 16;
    const labelX = Math.max(left, Math.min(x - labelWidth / 2, left + chartWidth - labelWidth));
    line.setAttribute("x1", x.toFixed(1));
    line.setAttribute("x2", x.toFixed(1));
    labelBackground.setAttribute("x", labelX.toFixed(1));
    labelBackground.setAttribute("width", labelWidth.toFixed(1));
    label.setAttribute("x", (labelX + 8).toFixed(1));
    label.textContent = text;
    hover.setAttribute("visibility", "visible");
  };

  svg.addEventListener("pointermove", update);
  svg.addEventListener("pointerleave", () => hover.setAttribute("visibility", "hidden"));
}

function drawAxis(svg, start, end, left, top, width, baseline) {
  drawLine(svg, left, baseline, left + width, baseline, "#b8c2bc", 1, "axis-baseline");
  const ticks = timelineTicks(start, end);
  for (const tick of ticks) {
    const x = timeToX(tick.time, start, end, left, width);
    drawLine(svg, x, top - 10, x, baseline, tick.isEndpoint ? "#d5ddd7" : "#edf0ee", 1, "axis-grid");
    drawSvgText(svg, x - 18, baseline + 22, formatTick(tick.time, tick.isEndpoint), "axis-label");
  }
}

function timelineWindow() {
  const now = new Date();
  const offsetMs = appState.timelineOffsetHours * 60 * 60 * 1000;
  if (appState.rangeHours >= 24 && appState.rangeHours % 24 === 0) {
    const end = new Date(currentTimelineEnd().getTime() - offsetMs);
    const start = new Date(end.getTime() - appState.rangeHours * 60 * 60 * 1000);
    return { start, end, now };
  }

  const end = new Date(currentTimelineEnd().getTime() - offsetMs);
  const start = new Date(end.getTime() - appState.rangeHours * 60 * 60 * 1000);
  return { start, end, now };
}

function currentTimelineEnd() {
  const now = new Date();
  if (appState.rangeHours >= 24 && appState.rangeHours % 24 === 0) {
    return nextLocalMidnight(now);
  }

  return roundUpToNextHour(now);
}

function nextLocalMidnight(value) {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate() + 1);
}

function roundUpToNextHour(value) {
  const rounded = new Date(value);
  rounded.setMinutes(0, 0, 0);
  if (rounded <= value) {
    rounded.setHours(rounded.getHours() + 1);
  }
  return rounded;
}

function timelineTicks(start, end) {
  if (appState.rangeHours <= 24) {
    const ticks = [];
    const cursor = new Date(start);
    cursor.setMinutes(0, 0, 0);
    if (cursor < start) {
      cursor.setHours(cursor.getHours() + 1);
    }

    while (cursor <= end) {
      ticks.push({
        time: new Date(cursor),
        isEndpoint: cursor.getTime() === start.getTime() || cursor.getTime() === end.getTime()
      });
      cursor.setHours(cursor.getHours() + 1);
    }
    return ticks;
  }

  const tickCount = 6;
  const ticks = [];
  for (let i = 0; i <= tickCount; i++) {
    const fraction = i / tickCount;
    ticks.push({
      time: new Date(start.getTime() + (end - start) * fraction),
      isEndpoint: i === 0 || i === tickCount
    });
  }
  return ticks;
}

function renderHeatmap() {
  els.heatmap.textContent = "";

  const { start, end } = timelineWindow();
  const startMs = start.getTime();
  const endMs = end.getTime();
  if (appState.groupFilters.length > 0) {
    const rows = appState.groupFilters.map(group => groupChartRow(group)).filter(Boolean)
      .map(row => ({ ...row, samples: timelineItemsInWindow(row.samples, startMs, endMs) }));
    const samples = rows.flatMap(row => timelineItemsInWindow(row.samples, startMs, endMs));

    if (rows.length === 0) {
      els.heatmapCaption.textContent = "No matching groups";
      els.heatmap.innerHTML = `<div class="empty-state">No matching group samples.</div>`;
      return;
    }

    els.heatmapCaption.textContent = `${rows.length} group${rows.length === 1 ? "" : "s"} shown separately, ${samples.length} samples in range`;
    for (const row of rows) {
      renderHeatmapBlock(row.label, row.samples);
    }
    return;
  }

  const rows = chartRows();
  const samples = rows.flatMap(row => timelineItemsInWindow(row.samples, startMs, endMs));

  if (rows.length === 0) {
    els.heatmapCaption.textContent = "Select one or more devices";
    els.heatmap.innerHTML = `<div class="empty-state">No devices selected.</div>`;
    return;
  }

  els.heatmapCaption.textContent = `${samples.length} samples in range`;
  renderHeatmapBlock(null, samples);
}

function renderHeatmapBlock(title, samples) {
  if (title) {
    const blockTitle = document.createElement("div");
    blockTitle.className = "heatmap-title";
    blockTitle.textContent = title;
    els.heatmap.append(blockTitle);
  }

  const buckets = new Map();
  for (const sample of samples) {
    const date = new Date(sample.sampledAtUtc);
    const key = `${date.getDay()}:${date.getHours()}`;
    const bucket = buckets.get(key) || { total: 0, online: 0 };
    bucket.total += 1;
    bucket.online += sample.online ? 1 : 0;
    buckets.set(key, bucket);
  }

  const grid = document.createElement("div");
  grid.className = "heatmap-grid";
  grid.append(document.createElement("span"));
  for (let hour = 0; hour < 24; hour++) {
    const label = document.createElement("span");
    label.className = "heatmap-hour";
    label.textContent = hour % 3 === 0 ? String(hour).padStart(2, "0") : "";
    grid.append(label);
  }

  const days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  for (let day = 0; day < 7; day++) {
    const dayLabel = document.createElement("span");
    dayLabel.className = "heatmap-label";
    dayLabel.textContent = days[day];
    grid.append(dayLabel);

    for (let hour = 0; hour < 24; hour++) {
      const bucket = buckets.get(`${day}:${hour}`);
      const pct = bucket ? bucket.online / bucket.total : 0;
      const cell = document.createElement("span");
      cell.className = "heat-cell";
      cell.style.background = bucket ? onlineHeatColor(pct) : "#eef1f2";
      cell.title = bucket
        ? `${days[day]} ${String(hour).padStart(2, "0")}:00 - ${Math.round(pct * 100)}% online (${bucket.online}/${bucket.total})`
        : `${days[day]} ${String(hour).padStart(2, "0")}:00 - no samples`;
      grid.append(cell);
    }
  }

  els.heatmap.append(grid);
}

function renderEvents() {
  const { start, end } = timelineWindow();
  const startMs = start.getTime();
  const endMs = end.getTime();
  const events = appState.groupFilters.length > 0
    ? appState.groupFilters.map(group => groupChartRow(group)).filter(Boolean).flatMap(row => timelineItemsInWindow(row.events, startMs, endMs))
    : timelineItemsInWindow(appState.history.events || [], startMs, endMs).filter(event => appState.selected.has(event.mac));

  const sortedEvents = events
    .sort((a, b) => b.timelineMs - a.timelineMs)
    .slice(0, 80);

  els.eventCount.textContent = `${sortedEvents.length} events`;
  els.eventList.textContent = "";

  if (sortedEvents.length === 0) {
    const empty = document.createElement("div");
    empty.className = "empty-state";
    empty.textContent = appState.groupFilters.length > 0
      ? "No group online/offline changes in this range."
      : appState.selected.size === 0 ? "No devices selected." : "No online/offline changes in this range.";
    els.eventList.append(empty);
    return;
  }

  for (const event of sortedEvents) {
    const row = document.createElement("div");
    row.className = "event-row";

    const pill = document.createElement("span");
    pill.className = `state-pill ${event.online ? "online" : ""}`;
    pill.textContent = event.online ? "online" : "offline";

    const main = document.createElement("div");
    const device = document.createElement("div");
    device.className = "event-device";
    device.textContent = event.displayName || event.mac;
    const time = document.createElement("div");
    time.className = "event-time";
    time.textContent = formatDateTime(event.atUtc);
    main.append(device, time);

    const ip = document.createElement("div");
    ip.className = "event-ip";
    ip.textContent = event.ipAddress || "";

    row.append(pill, main, ip);
    els.eventList.append(row);
  }
}

function queueNameSave(mac, name) {
  clearTimeout(appState.nameTimers.get(mac));
  const timer = setTimeout(async () => {
    await fetchJson(`/api/devices/${encodeURIComponent(mac)}/name`, {
      method: "POST",
      body: JSON.stringify({ name })
    });
    const device = appState.devices.find(item => item.mac === mac);
    if (device) {
      device.name = name.trim() || null;
      device.displayName = device.name || device.hostName || device.mac;
    }
    updateSelectionSummary();
    renderTimeline();
    renderEvents();
  }, 450);
  appState.nameTimers.set(mac, timer);
}

async function setIgnored(mac, ignored) {
  await fetchJson(`/api/devices/${encodeURIComponent(mac)}/ignore`, {
    method: "POST",
    body: JSON.stringify({ ignored })
  });

  if (ignored) {
    appState.selected.delete(mac);
    persistSelection();
  }

  await refreshAll();
}

function ensureInitialSelection() {
  if (appState.selected.size > 0 || appState.devices.length === 0) {
    return;
  }

  const visibleDevices = appState.devices.filter(device => !device.ignored || appState.showIgnored);
  const online = visibleDevices.filter(device => device.online).map(device => device.mac);
  appState.selected = new Set(online.length > 0 ? online : visibleDevices.map(device => device.mac));
  persistSelection();
}

function pruneSelection() {
  const known = new Set(appState.devices
    .filter(device => !device.ignored || appState.showIgnored)
    .map(device => device.mac));
  for (const mac of [...appState.selected]) {
    if (!known.has(mac)) {
      appState.selected.delete(mac);
    }
  }
  persistSelection();
}

function persistSelection() {
  persistUiPreferences();
}

function readStoredSelection() {
  try {
    const parsed = JSON.parse(localStorage.getItem("wifiDevices:selected") || "[]");
    return new Set(Array.isArray(parsed) ? parsed.filter(value => typeof value === "string" && value.trim()) : []);
  } catch {
    return new Set();
  }
}

function applyUiPreferences(preferences) {
  appState.groupFilters = Array.isArray(preferences.groupFilters) ? preferences.groupFilters : [];
  appState.expandedGroups = new Set(Array.isArray(preferences.expandedGroups) ? preferences.expandedGroups : []);
  appState.hiddenTimelineChildren = new Set(Array.isArray(preferences.hiddenTimelineChildren) ? preferences.hiddenTimelineChildren : []);
  appState.showIgnored = Boolean(preferences.showIgnored);
  appState.selected = new Set(Array.isArray(preferences.selected) ? preferences.selected : []);
  appState.rangeHours = normalizeRangeHours(preferences.rangeHours);
  appState.timelineOffsetHours = normalizeTimelineOffset(preferences.timelineOffsetHours);
  writeLocalPreferences();
}

function preferencesPayload() {
  return {
    groupFilters: appState.groupFilters,
    expandedGroups: [...appState.expandedGroups],
    hiddenTimelineChildren: [...appState.hiddenTimelineChildren],
    showIgnored: appState.showIgnored,
    selected: [...appState.selected],
    rangeHours: appState.rangeHours,
    timelineOffsetHours: appState.timelineOffsetHours
  };
}

function writeLocalPreferences() {
  const preferences = preferencesPayload();
  localStorage.setItem("wifiDevices:groupFilters", JSON.stringify(preferences.groupFilters));
  localStorage.removeItem("wifiDevices:groupFilter");
  localStorage.setItem("wifiDevices:expandedGroups", JSON.stringify(preferences.expandedGroups));
  localStorage.setItem("wifiDevices:hiddenTimelineChildren", JSON.stringify(preferences.hiddenTimelineChildren));
  localStorage.setItem("wifiDevices:showIgnored", String(preferences.showIgnored));
  localStorage.setItem("wifiDevices:selected", JSON.stringify(preferences.selected));
  localStorage.setItem("wifiDevices:rangeHours", String(preferences.rangeHours));
  localStorage.setItem("wifiDevices:timelineOffsetHours", String(preferences.timelineOffsetHours));
}

function persistUiPreferences() {
  writeLocalPreferences();
  clearTimeout(preferencesSaveTimer);
  preferencesSaveTimer = setTimeout(() => {
    saveUiPreferences().catch(error => console.warn("Unable to save shared Wi-Fi preferences.", error));
  }, 150);
}

async function saveUiPreferences() {
  return fetchJson("/api/ui-preferences", {
    method: "PUT",
    body: JSON.stringify(preferencesPayload())
  });
}

function selectedDevicesInOrder() {
  return appState.devices.filter(device => appState.selected.has(device.mac) && (!device.ignored || appState.showIgnored));
}

function chartRows() {
  if (appState.groupFilters.length > 0) {
    return appState.groupFilters.flatMap(group => {
      const groupRow = groupChartRow(group);
      if (!groupRow) {
        return [];
      }

      if (!appState.expandedGroups.has(group)) {
        return [groupRow];
      }

      return [groupRow, ...deviceRowsForGroup(group)];
    });
  }

  return selectedDeviceRows();
}

function selectedDeviceRows() {
  const samplesByMac = appState.history.samplesByMac || new Map();
  const eventsByMac = appState.history.eventsByMac || new Map();
  return selectedDevicesInOrder().map(device => ({
    kind: "device",
    key: device.mac,
    label: device.displayName || device.hostName || device.mac,
    detail: [device.lastIpAddress, device.mac].filter(Boolean).join("  "),
    samples: samplesByMac.get(device.mac) || [],
    events: eventsByMac.get(device.mac) || []
  }));
}

function deviceRowsForGroup(group) {
  const samplesByMac = appState.history.samplesByMac || new Map();
  const eventsByMac = appState.history.eventsByMac || new Map();
  return appState.devices
    .filter(device =>
      (!device.ignored || appState.showIgnored)
      && (device.groups || []).includes(group)
      && !appState.hiddenTimelineChildren.has(hiddenTimelineChildKey(group, device.mac)))
    .map(device => ({
      kind: "device-child",
      parentGroup: group,
      key: `${group}:${device.mac}`,
      mac: device.mac,
      label: device.displayName || device.hostName || device.mac,
      detail: [device.lastIpAddress, device.networkBand, device.mac].filter(Boolean).join("  "),
      samples: samplesByMac.get(device.mac) || [],
      events: eventsByMac.get(device.mac) || []
    }));
}

function groupChartRow(group) {
  const memberDevices = appState.devices.filter(device =>
    (!device.ignored || appState.showIgnored)
    && (device.groups || []).includes(group));
  const memberMacs = new Set(memberDevices.map(device => device.mac));
  if (memberMacs.size === 0) {
    return null;
  }

  const cached = appState.history.groupSeriesByName?.get(group);
  const series = cached || {
    samples: aggregateGroupSamples(group, memberMacs),
    events: null
  };
  if (!series.events) series.events = aggregateGroupEvents(group, series.samples);
  appState.history.groupSeriesByName?.set(group, series);
  const { samples, events } = series;
  const hiddenChildren = memberDevices.filter(device => appState.hiddenTimelineChildren.has(hiddenTimelineChildKey(group, device.mac))).length;
  const hiddenDetail = hiddenChildren > 0 ? `, ${hiddenChildren} hidden` : "";
  return {
    kind: "group",
    key: group,
    label: group,
    detail: `${memberDevices.length} device${memberDevices.length === 1 ? "" : "s"}${hiddenDetail} - click name to ${appState.expandedGroups.has(group) ? "collapse" : "expand"}`,
    expanded: appState.expandedGroups.has(group),
    hiddenChildren,
    samples,
    events
  };
}

function toggleGroupExpanded(group) {
  if (appState.expandedGroups.has(group)) {
    appState.expandedGroups.delete(group);
  } else {
    appState.expandedGroups.add(group);
  }
  persistExpandedGroups();
  renderTimeline();
}

function hiddenTimelineChildKey(group, mac) {
  return `${group}:${mac}`;
}

function toggleTimelineChild(row) {
  const key = hiddenTimelineChildKey(row.parentGroup, row.mac);
  if (appState.hiddenTimelineChildren.has(key)) {
    appState.hiddenTimelineChildren.delete(key);
  } else {
    appState.hiddenTimelineChildren.add(key);
  }

  persistHiddenTimelineChildren();
  renderTimeline();
}

function showHiddenTimelineChildren(group) {
  for (const key of [...appState.hiddenTimelineChildren]) {
    if (key.startsWith(`${group}:`)) {
      appState.hiddenTimelineChildren.delete(key);
    }
  }

  persistHiddenTimelineChildren();
  renderTimeline();
}

function aggregateGroupSamples(group, memberMacs) {
  const byTime = new Map();
  for (const sample of appState.history.samples || []) {
    if (!memberMacs.has(sample.mac)) {
      continue;
    }

    const key = sample.sampledAtUtc;
    const bucket = byTime.get(key) || {
      sampledAtUtc: sample.sampledAtUtc,
      timelineMs: sample.timelineMs,
      mac: `group:${group}`,
      ipAddress: null,
      online: false,
      source: sample.source,
      hostName: group,
      networkName: null,
      networkBand: null,
      connectionType: null
    };
    bucket.online = bucket.online || Boolean(sample.online);
    byTime.set(key, bucket);
  }

  return [...byTime.values()].sort((a, b) => new Date(a.sampledAtUtc) - new Date(b.sampledAtUtc));
}

function aggregateGroupEvents(group, samples) {
  const events = [];
  let previous = null;
  for (const sample of samples) {
    if (previous === null || previous !== sample.online) {
      events.push({
        atUtc: sample.sampledAtUtc,
        timelineMs: sample.timelineMs,
        mac: `group:${group}`,
        displayName: group,
        online: sample.online,
        ipAddress: null,
        source: sample.source
      });
      previous = sample.online;
    }
  }
  return events;
}

function updateSelectionSummary() {
  if (appState.groupFilters.length > 0) {
    const memberDevices = appState.devices.filter(device =>
      (!device.ignored || appState.showIgnored)
      && (device.groups || []).some(group => appState.groupFilters.includes(group)));
    const online = memberDevices.filter(device => device.online && !device.stale).length;
    els.selectionSummary.textContent = `${appState.groupFilters.length} group${appState.groupFilters.length === 1 ? "" : "s"}, ${memberDevices.length} devices, ${online} currently online`;
    return;
  }

  const count = appState.selected.size;
  const online = appState.devices.filter(device => appState.selected.has(device.mac) && (!device.ignored || appState.showIgnored) && device.online && !device.stale).length;
  els.selectionSummary.textContent = count === 0 ? "No devices selected" : `${count} selected, ${online} currently online`;
}

function groupBy(values, keySelector) {
  const map = new Map();
  for (const value of values) {
    const key = keySelector(value);
    const group = map.get(key) || [];
    group.push(value);
    map.set(key, group);
  }
  return map;
}

function timeToX(time, start, end, left, width) {
  const span = end - start || 1;
  return left + ((time - start) / span) * width;
}

function drawRect(svg, x, y, width, height, fill, className = "", radius = 3) {
  const rect = document.createElementNS("http://www.w3.org/2000/svg", "rect");
  rect.setAttribute("x", x.toFixed(1));
  rect.setAttribute("y", y.toFixed(1));
  rect.setAttribute("width", width.toFixed(1));
  rect.setAttribute("height", height.toFixed(1));
  rect.setAttribute("rx", String(radius));
  rect.setAttribute("fill", fill);
  if (className) {
    rect.setAttribute("class", className);
  }
  svg.append(rect);
}

function drawLine(svg, x1, y1, x2, y2, stroke, width, className = "") {
  const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
  line.setAttribute("x1", x1.toFixed(1));
  line.setAttribute("y1", y1.toFixed(1));
  line.setAttribute("x2", x2.toFixed(1));
  line.setAttribute("y2", y2.toFixed(1));
  line.setAttribute("stroke", stroke);
  line.setAttribute("stroke-width", String(width));
  if (className) {
    line.setAttribute("class", className);
  }
  svg.append(line);
}

function drawCircle(svg, x, y, radius, fill, className = "") {
  const circle = document.createElementNS("http://www.w3.org/2000/svg", "circle");
  circle.setAttribute("cx", x.toFixed(1));
  circle.setAttribute("cy", y.toFixed(1));
  circle.setAttribute("r", String(radius));
  circle.setAttribute("fill", fill);
  if (className) {
    circle.setAttribute("class", className);
  }
  svg.append(circle);
}

function drawEyeToggle(svg, x, y, row) {
  const group = document.createElementNS("http://www.w3.org/2000/svg", "g");
  group.setAttribute("class", `timeline-eye-toggle${row.hidden ? " timeline-eye-hidden" : ""}`);
  group.setAttribute("role", "button");
  group.setAttribute("tabindex", "0");
  group.setAttribute("aria-label", `${row.hidden ? "Show" : "Hide"} ${row.label} in online timeline`);

  const hit = document.createElementNS("http://www.w3.org/2000/svg", "rect");
  hit.setAttribute("x", String(x - 4));
  hit.setAttribute("y", String(y - 4));
  hit.setAttribute("width", "28");
  hit.setAttribute("height", "26");
  hit.setAttribute("rx", "6");
  hit.setAttribute("class", "timeline-eye-hit");
  group.append(hit);

  const eye = document.createElementNS("http://www.w3.org/2000/svg", "path");
  eye.setAttribute("d", `M ${x} ${y + 7} C ${x + 4} ${y + 1}, ${x + 14} ${y + 1}, ${x + 18} ${y + 7} C ${x + 14} ${y + 13}, ${x + 4} ${y + 13}, ${x} ${y + 7} Z`);
  eye.setAttribute("class", "timeline-eye-shape");
  group.append(eye);

  const pupil = document.createElementNS("http://www.w3.org/2000/svg", "circle");
  pupil.setAttribute("cx", String(x + 9));
  pupil.setAttribute("cy", String(y + 7));
  pupil.setAttribute("r", "2.7");
  pupil.setAttribute("class", "timeline-eye-pupil");
  group.append(pupil);

  if (row.hidden) {
    const slash = document.createElementNS("http://www.w3.org/2000/svg", "line");
    slash.setAttribute("x1", String(x + 1));
    slash.setAttribute("y1", String(y + 15));
    slash.setAttribute("x2", String(x + 17));
    slash.setAttribute("y2", String(y - 1));
    slash.setAttribute("class", "timeline-eye-slash");
    group.append(slash);
  }

  const toggle = event => {
    event.preventDefault();
    event.stopPropagation();
    toggleTimelineChild(row);
  };
  group.addEventListener("click", toggle);
  group.addEventListener("keydown", event => {
    if (event.key === "Enter" || event.key === " ") {
      toggle(event);
    }
  });

  svg.append(group);
}

function drawShowHiddenToggle(svg, x, y, row) {
  const group = document.createElementNS("http://www.w3.org/2000/svg", "g");
  group.setAttribute("class", "timeline-eye-toggle timeline-eye-restore");
  group.setAttribute("role", "button");
  group.setAttribute("tabindex", "0");
  group.setAttribute("aria-label", `Show ${row.hiddenChildren} hidden device${row.hiddenChildren === 1 ? "" : "s"} in ${row.label}`);

  const title = document.createElementNS("http://www.w3.org/2000/svg", "title");
  title.textContent = `Show ${row.hiddenChildren} hidden`;
  group.append(title);

  const badge = document.createElementNS("http://www.w3.org/2000/svg", "rect");
  badge.setAttribute("x", String(x - 6));
  badge.setAttribute("y", String(y - 4));
  badge.setAttribute("width", "28");
  badge.setAttribute("height", "26");
  badge.setAttribute("rx", "7");
  badge.setAttribute("class", "timeline-eye-hit");
  group.append(badge);

  const eye = document.createElementNS("http://www.w3.org/2000/svg", "path");
  eye.setAttribute("d", `M ${x} ${y + 7} C ${x + 4} ${y + 1}, ${x + 14} ${y + 1}, ${x + 18} ${y + 7} C ${x + 14} ${y + 13}, ${x + 4} ${y + 13}, ${x} ${y + 7} Z`);
  eye.setAttribute("class", "timeline-eye-shape");
  group.append(eye);

  const pupil = document.createElementNS("http://www.w3.org/2000/svg", "circle");
  pupil.setAttribute("cx", String(x + 9));
  pupil.setAttribute("cy", String(y + 7));
  pupil.setAttribute("r", "2.7");
  pupil.setAttribute("class", "timeline-eye-pupil");
  group.append(pupil);

  const toggle = event => {
    event.preventDefault();
    event.stopPropagation();
    showHiddenTimelineChildren(row.key);
  };
  group.addEventListener("click", toggle);
  group.addEventListener("keydown", event => {
    if (event.key === "Enter" || event.key === " ") {
      toggle(event);
    }
  });

  svg.append(group);
}

function drawSvgText(svg, x, y, text, className) {
  const label = document.createElementNS("http://www.w3.org/2000/svg", "text");
  label.setAttribute("x", String(x));
  label.setAttribute("y", String(y));
  label.setAttribute("class", className);
  const limit = className.includes("row-label") ? 28 : className === "row-sub-label" ? 34 : 64;
  label.textContent = text.length > limit ? `${text.slice(0, limit - 1)}...` : text;
  svg.append(label);
  return label;
}

function onlineHeatColor(pct) {
  const lightness = 93 - pct * 43;
  const saturation = 22 + pct * 48;
  return `hsl(158 ${saturation.toFixed(0)}% ${lightness.toFixed(0)}%)`;
}

function formatDateTime(value) {
  return new Date(value).toLocaleString([], {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  });
}

function formatTimelineDateTime(value) {
  return new Date(value).toLocaleString([], {
    month: "short",
    day: "numeric",
    year: "numeric",
    hour: "numeric",
    minute: "2-digit"
  });
}
function formatTimelineWindowLabel(start, end, now) {
  const dateOptions = { month: "short", day: "numeric" };
  const timeOptions = { hour: "numeric" };
  const current = now >= start && now <= end ? "Current window" : `${formatRelative(end)} window`;

  if (appState.rangeHours >= 24 && appState.rangeHours % 24 === 0) {
    const startLabel = start.toLocaleDateString([], dateOptions);
    const endLabel = end.toLocaleDateString([], dateOptions);
    return `${current}: ${startLabel} - ${endLabel}`;
  }

  const startLabel = `${start.toLocaleDateString([], dateOptions)} ${start.toLocaleTimeString([], timeOptions)}`;
  const endLabel = `${end.toLocaleDateString([], dateOptions)} ${end.toLocaleTimeString([], timeOptions)}`;
  return `${current}: ${startLabel} - ${endLabel}`;
}

function formatRelative(value) {
  const deltaMs = Date.now() - new Date(value).getTime();
  const minutes = Math.max(0, Math.round(deltaMs / 60000));
  if (minutes < 1) {
    return "now";
  }
  if (minutes < 60) {
    return `${minutes}m ago`;
  }
  const hours = Math.round(minutes / 60);
  if (hours < 48) {
    return `${hours}h ago`;
  }
  return `${Math.round(hours / 24)}d ago`;
}

function formatTick(date, isEndpoint = false) {
  if (appState.rangeHours <= 24) {
    if (appState.rangeHours === 24 && isEndpoint) {
      return date.toLocaleDateString([], { month: "short", day: "numeric" });
    }
    return date.toLocaleTimeString([], { hour: "numeric" });
  }
  return date.toLocaleDateString([], { month: "short", day: "numeric" });
}
