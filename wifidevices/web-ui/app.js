const ZOOM_HOUR_STEPS = [6, 12, 24, 48, 72, 168, 336, 720, 2160];
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
  timelineScrollTimer: null
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
  rangeHours: document.querySelector("#rangeHours"),
  timelineChart: document.querySelector("#timelineChart"),
  timelineScroll: document.querySelector("#timelineScroll"),
  timelineScrollLabel: document.querySelector("#timelineScrollLabel"),
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

els.rangeHours.addEventListener("change", () => {
  setRangeHours(Number(els.rangeHours.value), true);
});

els.timelineScroll.addEventListener("input", () => {
  setTimelineOffset(timelineOffsetFromScrollValue(Number(els.timelineScroll.value)), false);
  clearTimeout(appState.timelineScrollTimer);
  appState.timelineScrollTimer = setTimeout(loadHistory, 300);
});

els.timelineScroll.addEventListener("change", () => {
  clearTimeout(appState.timelineScrollTimer);
  setTimelineOffset(timelineOffsetFromScrollValue(Number(els.timelineScroll.value)), true);
});

els.timelineChart.parentElement.addEventListener("wheel", event => {
  event.preventDefault();
  const direction = event.deltaY < 0 ? -1 : 1;
  zoomTimeline(direction);
}, { passive: false });

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

  syncRangeControl();
  syncTimelineScrollControl();
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

function setRangeHours(hours, reloadHistory) {
  appState.rangeHours = normalizeRangeHours(hours);
  appState.timelineOffsetHours = normalizeTimelineOffset(appState.timelineOffsetHours);
  persistUiPreferences();
  syncRangeControl();
  syncTimelineScrollControl();
  if (reloadHistory) {
    loadHistory();
  } else {
    renderHistory();
  }
}

function syncRangeControl() {
  const value = String(appState.rangeHours);
  let option = [...els.rangeHours.options].find(item => item.value === value);
  if (!option) {
    option = document.createElement("option");
    option.value = value;
    option.textContent = formatRangeLabel(appState.rangeHours);
    els.rangeHours.append(option);
  }
  els.rangeHours.value = value;
}

function formatRangeLabel(hours) {
  if (hours % 24 === 0 && hours >= 24) {
    const days = hours / 24;
    return `${days} day${days === 1 ? "" : "s"}`;
  }
  return `${hours} hours`;
}

function zoomTimeline(direction) {
  const current = appState.rangeHours;
  const index = ZOOM_HOUR_STEPS.findIndex(hours => hours >= current);
  const currentIndex = index === -1 ? ZOOM_HOUR_STEPS.length - 1 : index;
  const nextIndex = Math.min(ZOOM_HOUR_STEPS.length - 1, Math.max(0, currentIndex + direction));
  const next = ZOOM_HOUR_STEPS[nextIndex];
  if (next !== current) {
    setRangeHours(next, true);
  }
}

function normalizeTimelineOffset(hours) {
  const parsed = Number(hours);
  const step = timelineScrollStepHours();
  const max = timelineScrollMaxHours();
  if (!Number.isFinite(parsed)) {
    return 0;
  }

  return Math.min(max, Math.max(0, Math.round(parsed / step) * step));
}

function timelineScrollMaxHours() {
  const step = timelineScrollStepHours();
  const firstSample = appState.history?.bounds?.firstSampleUtc
    ? new Date(appState.history.bounds.firstSampleUtc)
    : null;
  if (!firstSample || Number.isNaN(firstSample.getTime())) {
    return 24;
  }

  const currentEnd = currentTimelineEnd();
  const historySpanHours = Math.ceil((currentEnd - firstSample) / (60 * 60 * 1000));
  const max = Math.max(24, Math.min(MAX_HISTORY_HOURS - appState.rangeHours, historySpanHours - appState.rangeHours));
  return Math.floor(max / step) * step;
}

function timelineScrollStepHours() {
  return 1;
}

function timelineOffsetFromScrollValue(value) {
  const max = timelineScrollMaxHours();
  return normalizeTimelineOffset(max - value);
}

function setTimelineOffset(hours, reloadHistory) {
  appState.timelineOffsetHours = normalizeTimelineOffset(hours);
  persistUiPreferences();
  syncTimelineScrollControl();
  if (reloadHistory) {
    loadHistory();
  } else {
    renderHistory();
  }
}

function syncTimelineScrollControl() {
  if (!els.timelineScroll) {
    return;
  }

  appState.timelineOffsetHours = normalizeTimelineOffset(appState.timelineOffsetHours);
  const max = timelineScrollMaxHours();
  const step = timelineScrollStepHours();
  els.timelineScroll.min = "0";
  els.timelineScroll.max = String(max);
  els.timelineScroll.step = String(step);
  els.timelineScroll.value = String(max - appState.timelineOffsetHours);
  els.timelineScroll.disabled = max === 0;

  const { start, end, now } = timelineWindow();
  els.timelineScrollLabel.textContent = formatTimelineWindowLabel(start, end, now);
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
  const hours = Math.min(MAX_HISTORY_HOURS, appState.rangeHours + appState.timelineOffsetHours);
  appState.history = await fetchJson(`/api/history?hours=${hours}&macs=${macs}`);
  const clampedOffset = normalizeTimelineOffset(appState.timelineOffsetHours);
  if (clampedOffset !== appState.timelineOffsetHours) {
    appState.timelineOffsetHours = clampedOffset;
    persistUiPreferences();
    appState.history = await fetchJson(`/api/history?hours=${appState.rangeHours + appState.timelineOffsetHours}&macs=${macs}`);
  }
  renderHistory();
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
  renderHeatmap();
  renderEvents();
}

function renderTimeline() {
  const rows = chartRows();
  const svg = els.timelineChart;
  syncTimelineScrollControl();
  svg.textContent = "";

  if (rows.length === 0) {
    svg.setAttribute("height", "120");
    svg.setAttribute("viewBox", "0 0 760 120");
    drawSvgText(svg, 24, 62, appState.groupFilters.length > 0 ? "No matching group samples in range." : "Select devices to show their online timeline.", "empty-svg");
    return;
  }

  const width = Math.max(appState.rangeHours <= 24 ? 1180 : 820, svg.parentElement.clientWidth - 16);
  const rowHeight = 50;
  const top = 34;
  const left = 230;
  const right = 24;
  const bottom = 36;
  const chartWidth = width - left - right;
  const height = top + bottom + rows.length * rowHeight;
  const { start, end, now } = timelineWindow();

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

    const samples = row.samples
      .map(sample => ({ ...sample, time: new Date(sample.sampledAtUtc) }))
      .filter(sample => sample.time >= start && sample.time <= end)
      .sort((a, b) => a.time - b.time);

    const trackY = y + 14;
    drawRect(svg, left, trackY, chartWidth, 18, "#edf1ee", "timeline-track-bg", 9);
    drawLine(svg, left, y + rowHeight - 2, width - right, y + rowHeight - 2, "#f0f2f0", 1, "row-divider");

    if (samples.length === 0) {
      drawSvgText(svg, left + 10, y + 27, "No samples in range", "no-samples");
    }

    const onlineSamples = samples.filter(sample => sample.online).length;
    const onlinePct = samples.length ? Math.round((onlineSamples / samples.length) * 100) : null;
    drawSvgText(svg, left - 56, y + 27, onlinePct === null ? "--" : `${onlinePct}%`, "row-percent");

    for (let i = 0; i < samples.length; i++) {
      const current = samples[i];
      const next = samples[i + 1];
      const maxKnownUntil = current.time.getTime() + UNKNOWN_GAP_MS;
      const nextTime = next?.time.getTime() ?? now.getTime();
      const segmentEnd = new Date(Math.min(nextTime, maxKnownUntil, now.getTime(), end.getTime()));
      if (segmentEnd <= current.time) {
        continue;
      }

      const x1 = timeToX(current.time, start, end, left, chartWidth);
      const x2 = timeToX(segmentEnd, start, end, left, chartWidth);
      if (current.online) {
        const segmentWidth = Math.max(2, x2 - x1);
        const segmentX = Math.max(left, x2 - segmentWidth);
        drawRect(svg, segmentX, trackY, segmentWidth, 18, "#16845f", "timeline-online", 9);
      }
    }

    const events = row.events
      .map(event => ({ ...event, time: new Date(event.atUtc) }))
      .filter(event => event.time >= start && event.time <= end);

    for (const event of events) {
      const x = timeToX(event.time, start, end, left, chartWidth);
      drawLine(svg, x, trackY - 6, x, trackY + 24, event.online ? "#0e6147" : "#bd4f43", 2, "timeline-event-line");
      drawCircle(svg, x, trackY + 9, 4, event.online ? "#0e6147" : "#bd4f43", "timeline-event-dot");
    }
  });
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

  if (appState.groupFilters.length > 0) {
    const rows = appState.groupFilters.map(group => groupChartRow(group)).filter(Boolean);
    const samples = rows.flatMap(row => row.samples);

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
  const samples = rows.flatMap(row => row.samples);

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
  const events = appState.groupFilters.length > 0
    ? appState.groupFilters.map(group => groupChartRow(group)).filter(Boolean).flatMap(row => row.events)
    : (appState.history.events || []).filter(event => appState.selected.has(event.mac));

  const sortedEvents = events
    .sort((a, b) => new Date(b.atUtc) - new Date(a.atUtc))
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
  const samplesByMac = groupBy(appState.history.samples || [], sample => sample.mac);
  const eventsByMac = groupBy(appState.history.events || [], event => event.mac);
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
  const samplesByMac = groupBy(appState.history.samples || [], sample => sample.mac);
  const eventsByMac = groupBy(appState.history.events || [], event => event.mac);
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

  const samples = aggregateGroupSamples(group, memberMacs);
  const events = aggregateGroupEvents(group, samples);
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
