const state = {
  browserAccountButtonKey: "",
  browserAccounts: [],
  browserAccountOptionsKey: "",
  browserAccountSelectionInFlight: false,
  captionTrackPreference: null,
  captionTrackPreferenceLanguageCode: "",
  captionTrackRequests: new Map(),
  captionTracksByVideoId: new Map(),
  cards: new Map(),
  currentVideoId: null,
  currentCaptionTracks: [],
  currentVideoTitle: "",
  downloadedVideoCount: 0,
  isBusy: false,
  libraryVersion: -1,
  lastVideoRefreshAt: 0,
  offlineNotified: false,
  refreshTimer: null,
  searchMatcher: null,
  settingsCanRefreshTotal: false,
  settingsLoaded: false,
  settingsLoading: false,
  settingsOpen: false,
  settingsRefreshing: false,
  settingsSaving: false,
  showWatchedOnly: false,
  searchTerm: "",
  configuredDownloadCount: null,
  syncScopeDownloadedCount: null,
  syncScopeFailedCount: null,
  syncScopeTargetCount: null,
  hotspotAction: "",
  hotspotActionInFlight: false,
  lastPhoneAccessRefreshAt: 0,
  phoneAccessInfo: null,
  phoneAccessLoaded: false,
  phoneAccessLoading: false,
  selectedBrowserAccountKey: "",
  selectedIds: new Set(),
  syncAuthMessage: "Authentication has not been verified yet for the selected account.",
  syncAuthState: "missing",
  syncButtonAnimationFrame: 0,
  syncButtonAnimationTimer: null,
  toastTimer: null,
  watchedMarkingIds: new Set(),
  watchLaterTotalCount: null,
  youtubeAccounts: [],
  youtubeAccountCacheByBrowserKey: new Map(),
  youtubeAccountButtonKey: "",
  youtubeAccountOptionsKey: "",
  youtubeAccountsSourceBrowserKey: "",
  isRefreshingYouTubeAccounts: false,
  youtubeAccountSelectionInFlight: false,
  selectedYouTubeAccountKey: "",
  videos: new Map(),
};

const elements = {
  activityFeed: document.getElementById("activityFeed"),
  browserAccountButton: document.getElementById("browserAccountButton"),
  browserAccountList: document.getElementById("browserAccountList"),
  browserAccountMenu: document.getElementById("browserAccountMenu"),
  browserAccountPicker: document.getElementById("browserAccountPicker"),
  captionPicker: document.getElementById("captionPicker"),
  captionSelect: document.getElementById("captionSelect"),
  captionStatus: document.getElementById("captionStatus"),
  clearSelectionButton: document.getElementById("clearSelectionButton"),
  emptyStateText: document.getElementById("emptyStateText"),
  emptyStateTitle: document.getElementById("emptyStateTitle"),
  emptyState: document.getElementById("emptyState"),
  libraryGrid: document.getElementById("libraryGrid"),
  monitorClose: document.getElementById("monitorClose"),
  monitorPanel: document.getElementById("monitorPanel"),
  monitorPill: document.getElementById("monitorPill"),
  monitorToggle: document.getElementById("monitorToggle"),
  openDownloadsButton: document.getElementById("openDownloadsButton"),
  phoneAccessCandidates: document.getElementById("phoneAccessCandidates"),
  phoneAccessClients: document.getElementById("phoneAccessClients"),
  phoneAccessControlHint: document.getElementById("phoneAccessControlHint"),
  phoneAccessInstruction: document.getElementById("phoneAccessInstruction"),
  phoneAccessRecommendedUrl: document.getElementById("phoneAccessRecommendedUrl"),
  phoneAccessRefreshButton: document.getElementById("phoneAccessRefreshButton"),
  phoneAccessSsid: document.getElementById("phoneAccessSsid"),
  phoneAccessStartButton: document.getElementById("phoneAccessStartButton"),
  phoneAccessState: document.getElementById("phoneAccessState"),
  phoneAccessStatus: document.getElementById("phoneAccessStatus"),
  phoneAccessStopButton: document.getElementById("phoneAccessStopButton"),
  phoneAccessWifi: document.getElementById("phoneAccessWifi"),
  playerPanel: document.getElementById("playerPanel"),
  playerPlaceholder: document.getElementById("playerPlaceholder"),
  playerTitle: document.getElementById("playerTitle"),
  refreshSummary: document.getElementById("refreshSummary"),
  removeSelectedButton: document.getElementById("removeSelectedButton"),
  searchInput: document.getElementById("searchInput"),
  searchPanel: document.getElementById("searchPanel"),
  searchToggle: document.getElementById("searchToggle"),
  selectionSummary: document.getElementById("selectionSummary"),
  settingsButton: document.getElementById("settingsButton"),
  settingsBrowser: document.getElementById("settingsBrowser"),
  settingsClose: document.getElementById("settingsClose"),
  settingsDownloadCount: document.getElementById("settingsDownloadCount"),
  settingsPanel: document.getElementById("settingsPanel"),
  settingsProfile: document.getElementById("settingsProfile"),
  settingsRefreshButton: document.getElementById("settingsRefreshButton"),
  settingsSaveButton: document.getElementById("settingsSaveButton"),
  settingsSummary: document.getElementById("settingsSummary"),
  statusText: document.getElementById("statusText"),
  syncButton: document.getElementById("syncButton"),
  toast: document.getElementById("toast"),
  videoPlayer: document.getElementById("videoPlayer"),
  watchedVideosButton: document.getElementById("watchedVideosButton"),
  youtubeAccountButton: document.getElementById("youtubeAccountButton"),
  youtubeAccountList: document.getElementById("youtubeAccountList"),
  youtubeAccountMenu: document.getElementById("youtubeAccountMenu"),
  youtubeAccountPicker: document.getElementById("youtubeAccountPicker"),
  youtubeAccountRefreshStatus: document.getElementById("youtubeAccountRefreshStatus"),
};

document.addEventListener("DOMContentLoaded", async () => {
  wireEvents();
  await refreshStatus(true);
  state.refreshTimer = window.setInterval(() => {
    void refreshStatus(false);
  }, 1500);
});

window.addEventListener("unhandledrejection", (event) => {
  const message = event.reason instanceof Error
    ? event.reason.message
    : typeof event.reason === "string"
      ? event.reason
      : "The browser UI hit an unexpected error.";
  showToast(message, true);
});

function wireEvents() {
  elements.browserAccountButton.addEventListener("click", () => {
    if (elements.browserAccountButton.disabled) {
      return;
    }

    setYouTubeAccountMenuOpen(false);
    setBrowserAccountMenuOpen(elements.browserAccountList.classList.contains("hidden"));
  });

  elements.browserAccountButton.addEventListener("keydown", (event) => {
    if (event.key !== "ArrowDown") {
      return;
    }

    event.preventDefault();
    setYouTubeAccountMenuOpen(false);
    setBrowserAccountMenuOpen(true);
    focusSelectedBrowserAccountOption();
  });

  elements.browserAccountList.addEventListener("click", async (event) => {
    const option = event.target instanceof Element
      ? event.target.closest(".account-menu-option")
      : null;
    if (!(option instanceof HTMLButtonElement) || option.disabled) {
      return;
    }

    await selectBrowserAccount(option.dataset.accountKey || "");
  });

  elements.browserAccountList.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") {
      return;
    }

    event.preventDefault();
    setBrowserAccountMenuOpen(false);
    elements.browserAccountButton.focus();
  });

  elements.youtubeAccountButton.addEventListener("click", () => {
    if (elements.youtubeAccountButton.disabled) {
      return;
    }

    setBrowserAccountMenuOpen(false);
    setYouTubeAccountMenuOpen(elements.youtubeAccountList.classList.contains("hidden"));
  });

  elements.youtubeAccountButton.addEventListener("keydown", (event) => {
    if (event.key !== "ArrowDown") {
      return;
    }

    event.preventDefault();
    setBrowserAccountMenuOpen(false);
    setYouTubeAccountMenuOpen(true);
    focusSelectedYouTubeAccountOption();
  });

  elements.youtubeAccountList.addEventListener("click", async (event) => {
    const option = event.target instanceof Element
      ? event.target.closest(".account-menu-option")
      : null;
    if (!(option instanceof HTMLButtonElement) || option.disabled) {
      return;
    }

    await selectYouTubeAccount(option.dataset.accountKey || "");
  });

  elements.youtubeAccountList.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") {
      return;
    }

    event.preventDefault();
    setYouTubeAccountMenuOpen(false);
    elements.youtubeAccountButton.focus();
  });

  document.addEventListener("click", (event) => {
    if (!(event.target instanceof Node)) {
      return;
    }

    if (!elements.browserAccountList.classList.contains("hidden")
      && !elements.browserAccountMenu.contains(event.target)) {
      setBrowserAccountMenuOpen(false);
    }

    if (!elements.youtubeAccountList.classList.contains("hidden")
      && !elements.youtubeAccountMenu.contains(event.target)) {
      setYouTubeAccountMenuOpen(false);
    }
  });

  elements.syncButton.addEventListener("click", async () => {
    await runCommand("/api/sync", null, "Sync requested.");
  });

  elements.openDownloadsButton.addEventListener("click", async () => {
    await runCommand("/api/downloads/open", null, "Opened the downloads folder for the selected account.");
  });

  elements.settingsButton.addEventListener("click", async () => {
    await toggleSettingsPanel();
  });

  elements.settingsClose.addEventListener("click", () => {
    setSettingsOpen(false);
  });

  elements.settingsRefreshButton.addEventListener("click", async () => {
    await refreshSettingsSummary();
  });

  elements.settingsSaveButton.addEventListener("click", async () => {
    await saveSettings();
  });

  elements.phoneAccessRefreshButton.addEventListener("click", async () => {
    await refreshPhoneAccess(true);
  });

  elements.phoneAccessStartButton.addEventListener("click", async () => {
    await requestHotspot("start");
  });

  elements.phoneAccessStopButton.addEventListener("click", async () => {
    await requestHotspot("stop");
  });

  elements.clearSelectionButton.addEventListener("click", () => {
    clearSelectedVideos();
    updateSelectionUi();
  });

  elements.removeSelectedButton.addEventListener("click", async () => {
    const ids = [...state.selectedIds];
    if (ids.length === 0) {
      showToast("Select one or more videos first.", true);
      return;
    }

    if (state.showWatchedOnly) {
      await restoreVideos(ids);
      return;
    }

    await markVideos(ids, true);
  });

  elements.watchedVideosButton.addEventListener("click", () => {
    clearSelectedVideos();
    state.showWatchedOnly = !state.showWatchedOnly;
    applyFilters();
    updateSelectionUi();
    updateEmptyState();
  });

  elements.searchInput.addEventListener("input", () => {
    state.searchTerm = elements.searchInput.value.trim().toLowerCase();
    state.searchMatcher = buildSearchMatcher(state.searchTerm);
    applyFilters();
    updateSelectionUi();
    updateEmptyState();
  });

  elements.searchToggle.addEventListener("click", () => {
    const isOpen = elements.searchPanel.classList.contains("is-open");
    if (isOpen && elements.searchInput.value.trim() !== "") {
      elements.searchInput.value = "";
      state.searchTerm = "";
      state.searchMatcher = null;
      applyFilters();
      updateSelectionUi();
      updateEmptyState();
    }

    const shouldOpen = !isOpen;
    setSearchOpen(shouldOpen);
    if (shouldOpen) {
      elements.searchInput.focus();
      elements.searchInput.select();
    }
  });

  elements.monitorToggle.addEventListener("click", () => {
    setMonitorOpen(elements.monitorPanel.classList.contains("hidden"));
  });

  elements.monitorClose.addEventListener("click", () => {
    setMonitorOpen(false);
  });

  elements.videoPlayer.addEventListener("error", () => {
    showToast("This video could not be played in the browser.", true);
  });

  elements.videoPlayer.addEventListener("timeupdate", () => {
    void maybeMarkCurrentVideoWatched(false);
  });

  elements.videoPlayer.addEventListener("ended", () => {
    void maybeMarkCurrentVideoWatched(true);
  });

  elements.captionSelect.addEventListener("change", () => {
    const selectedTrackKey = elements.captionSelect.value || "";
    state.captionTrackPreference = selectedTrackKey;
    state.captionTrackPreferenceLanguageCode = selectedTrackKey === ""
      ? ""
      : state.currentCaptionTracks.find((track) => track.trackKey === selectedTrackKey)?.languageCode || "";
    syncCaptionSelectWidth();
    applyCaptionSelection(selectedTrackKey);
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      if (!elements.browserAccountList.classList.contains("hidden")) {
        setBrowserAccountMenuOpen(false);
      }

      if (!elements.youtubeAccountList.classList.contains("hidden")) {
        setYouTubeAccountMenuOpen(false);
      }

      if (!elements.monitorPanel.classList.contains("hidden")) {
        setMonitorOpen(false);
      }

      if (!elements.settingsPanel.classList.contains("hidden")) {
        setSettingsOpen(false);
      }

      if (elements.searchPanel.classList.contains("is-open") && elements.searchInput.value.trim() === "") {
        setSearchOpen(false);
      }
    }
  });
}

async function toggleSettingsPanel() {
  if (state.settingsOpen) {
    setSettingsOpen(false);
    return;
  }

  await openSettingsPanel();
}

async function openSettingsPanel() {
  setBrowserAccountMenuOpen(false);
  setYouTubeAccountMenuOpen(false);
  setSettingsOpen(true);
  await Promise.all([
    loadSettingsPanel(true),
    refreshPhoneAccess(true),
  ]);
}

function setSettingsOpen(isOpen) {
  state.settingsOpen = isOpen;
  elements.settingsPanel.classList.toggle("hidden", !isOpen);
  elements.settingsPanel.setAttribute("aria-hidden", String(!isOpen));
  elements.settingsButton.setAttribute("aria-expanded", String(isOpen));
}

async function loadSettingsPanel(forceReload) {
  if (state.settingsLoading) {
    return;
  }

  if (!forceReload && state.settingsLoaded) {
    updateSettingsControls();
    return;
  }

  state.settingsLoading = true;
  elements.settingsSummary.textContent = "Loading settings...";
  updateSettingsControls();
  let response = null;

  try {
    response = await fetchJson("/api/settings");
    applySettingsResponse(response);
    state.settingsLoaded = true;
  } catch (error) {
    const message = error instanceof Error ? error.message : "Could not load settings.";
    elements.settingsSummary.textContent = message;
    showToast(message, true);
  } finally {
    state.settingsLoading = false;
    updateSettingsControls();
  }

  if (state.settingsOpen && state.settingsLoaded && response?.shouldAutoRefreshSummary) {
    await refreshSettingsSummary();
  }
}

function applySettingsResponse(response) {
  renderSettingsBrowserOptions(response.availableBrowsers, response.browserCookies);
  elements.settingsDownloadCount.value = Number.isInteger(response.downloadCount)
    ? String(response.downloadCount)
    : "150";
  elements.settingsProfile.value = typeof response.browserProfile === "string" && response.browserProfile.trim() !== ""
    ? response.browserProfile
    : "Default";
  state.settingsCanRefreshTotal = Boolean(response.canRefreshTotal);
  elements.settingsSummary.textContent = response.summaryMessage || "Refresh Total inspects Watch Later using the settings below.";
}

function applySettingsSummaryResponse(response) {
  if (Number.isInteger(response.downloadCount)) {
    elements.settingsDownloadCount.value = String(response.downloadCount);
  }

  if (typeof response.browserCookies === "string" && response.browserCookies !== "") {
    elements.settingsBrowser.value = response.browserCookies;
  }

  if (typeof response.browserProfile === "string" && response.browserProfile.trim() !== "") {
    elements.settingsProfile.value = response.browserProfile;
  }

  state.settingsCanRefreshTotal = Boolean(response.canRefreshTotal);
  elements.settingsSummary.textContent = response.summaryMessage || "Refresh complete.";
}

function renderSettingsBrowserOptions(options, selectedValue) {
  elements.settingsBrowser.replaceChildren();
  for (const option of Array.isArray(options) ? options : []) {
    const element = document.createElement("option");
    element.value = option.value || "";
    element.textContent = option.label || option.value || "Browser";
    element.selected = element.value === selectedValue;
    elements.settingsBrowser.append(element);
  }

  if (elements.settingsBrowser.options.length > 0 && elements.settingsBrowser.value === "") {
    elements.settingsBrowser.selectedIndex = 0;
  }
}

function buildSettingsPayload() {
  const parsedDownloadCount = Number.parseInt(elements.settingsDownloadCount.value, 10);
  return {
    downloadCount: Number.isInteger(parsedDownloadCount) ? parsedDownloadCount : 1,
    browserCookies: elements.settingsBrowser.value || "",
    browserProfile: elements.settingsProfile.value.trim() || "Default",
  };
}

function updateSettingsControls() {
  const isWorking = state.settingsLoading || state.settingsRefreshing || state.settingsSaving;
  elements.settingsDownloadCount.disabled = isWorking;
  elements.settingsBrowser.disabled = isWorking;
  elements.settingsProfile.disabled = isWorking;
  elements.settingsRefreshButton.disabled = isWorking || !state.settingsCanRefreshTotal || state.isBusy;
  elements.settingsSaveButton.disabled = isWorking;
  elements.settingsRefreshButton.textContent = state.settingsLoading
    ? "Loading..."
    : state.settingsRefreshing
      ? "Refreshing..."
      : "Refresh Total";
  elements.settingsSaveButton.textContent = state.settingsSaving ? "Saving..." : "Save Settings";
  updatePhoneAccessControls();
}

async function refreshSettingsSummary() {
  if (state.settingsLoading || state.settingsRefreshing || !state.settingsOpen) {
    return;
  }

  state.settingsRefreshing = true;
  elements.settingsSummary.textContent = "Refreshing Watch Later total...";
  updateSettingsControls();

  try {
    const response = await post("/api/settings/summary", buildSettingsPayload());
    applySettingsSummaryResponse(response);
  } catch (error) {
    const message = error instanceof Error ? error.message : "Could not refresh Watch Later total.";
    elements.settingsSummary.textContent = message;
    showToast(message, true);
  } finally {
    state.settingsRefreshing = false;
    updateSettingsControls();
  }
}

async function saveSettings() {
  if (state.settingsLoading || state.settingsSaving || !state.settingsOpen) {
    return;
  }

  state.settingsSaving = true;
  updateSettingsControls();

  try {
    const response = await post("/api/settings/save", buildSettingsPayload());
    state.settingsLoaded = false;
    await refreshStatus(true);
    await loadSettingsPanel(true);
    showToast(response.message || "Settings saved.", false);
  } catch (error) {
    showToast(error instanceof Error ? error.message : "Could not save settings.", true);
  } finally {
    state.settingsSaving = false;
    updateSettingsControls();
  }
}

async function refreshPhoneAccess(forceReload) {
  if ((!state.settingsOpen && !forceReload) || state.phoneAccessLoading || state.hotspotActionInFlight) {
    return;
  }

  if (!forceReload && state.phoneAccessLoaded && Date.now() - state.lastPhoneAccessRefreshAt < 5000) {
    return;
  }

  state.phoneAccessLoading = true;
  if (!state.phoneAccessLoaded) {
    elements.phoneAccessStatus.textContent = "Checking laptop network access...";
  }
  updatePhoneAccessControls();

  try {
    const info = await fetchJson("/api/network-info");
    state.phoneAccessInfo = info;
    state.phoneAccessLoaded = true;
    state.lastPhoneAccessRefreshAt = Date.now();
    renderPhoneAccess(info);
  } catch (error) {
    const message = error instanceof Error ? error.message : "Could not load phone access details.";
    renderPhoneAccessError(message, state.phoneAccessInfo);
  } finally {
    state.phoneAccessLoading = false;
    updatePhoneAccessControls();
  }
}

function renderPhoneAccess(info) {
  state.phoneAccessInfo = info;
  const canControlHotspot = Boolean(info.canControlHotspot);

  const hotspotState = typeof info.hotspotState === "string" ? info.hotspotState : "Unknown";
  const stateClass = hotspotState.toLowerCase() === "on"
    ? "phone-access-pill is-on"
    : hotspotState.toLowerCase() === "off"
      ? "phone-access-pill is-off"
      : hotspotState.toLowerCase() === "intransition"
        ? "phone-access-pill is-transition"
        : "phone-access-pill";

  elements.phoneAccessState.className = stateClass;
  elements.phoneAccessState.textContent = hotspotState;
  elements.phoneAccessSsid.textContent = info.hotspotSsid || "Not detected";
  elements.phoneAccessWifi.textContent = info.currentWifiName || "No active Wi-Fi profile";
  elements.phoneAccessClients.textContent = Number.isInteger(info.maxClients)
    ? `Windows hotspot allows up to ${info.maxClients} clients.`
    : "Windows did not report a hotspot client limit.";
  elements.phoneAccessInstruction.textContent = info.instruction || "Connect the phone to the same network as this laptop.";
  elements.phoneAccessControlHint.classList.toggle("hidden", canControlHotspot);
  elements.phoneAccessRecommendedUrl.textContent = info.recommendedUrl || window.location.href;
  elements.phoneAccessRecommendedUrl.href = info.recommendedUrl || window.location.href;
  if (!state.hotspotActionInFlight) {
    elements.phoneAccessStatus.textContent = buildPhoneAccessStatus(info);
  }

  elements.phoneAccessCandidates.replaceChildren();
  const candidates = Array.isArray(info.candidateUrls) ? info.candidateUrls : [];
  if (candidates.length === 0) {
    const empty = document.createElement("li");
    empty.textContent = "No private LAN addresses are available on this laptop right now.";
    elements.phoneAccessCandidates.append(empty);
  } else {
    for (const candidate of candidates) {
      const item = document.createElement("li");

      const url = document.createElement("div");
      url.className = "phone-access-candidate-url";
      url.textContent = candidate.url;

      const meta = document.createElement("div");
      meta.className = "phone-access-candidate-meta";
      meta.textContent = `${candidate.interfaceName} (${candidate.interfaceType}) - ${candidate.address}`;

      item.append(url, meta);
      elements.phoneAccessCandidates.append(item);
    }
  }
}

function renderPhoneAccessError(message, previousInfo) {
  if (previousInfo) {
    renderPhoneAccess(previousInfo);
    elements.phoneAccessStatus.textContent = `${message} Showing the last known hotspot details.`;
    return;
  }

  state.phoneAccessLoaded = false;
  state.phoneAccessInfo = null;
  elements.phoneAccessState.className = "phone-access-pill is-error";
  elements.phoneAccessState.textContent = "Unavailable";
  elements.phoneAccessSsid.textContent = "Unavailable";
  elements.phoneAccessWifi.textContent = "Unavailable";
  elements.phoneAccessClients.textContent = "";
  elements.phoneAccessInstruction.textContent = message;
  elements.phoneAccessControlHint.classList.add("hidden");
  elements.phoneAccessRecommendedUrl.textContent = window.location.href;
  elements.phoneAccessRecommendedUrl.href = window.location.href;
  elements.phoneAccessStatus.textContent = message;
  elements.phoneAccessCandidates.innerHTML = "<li>The laptop network details could not be loaded.</li>";
}

function buildPhoneAccessStatus(info) {
  if (!info.canControlHotspot) {
    return "Hotspot control is only available on the laptop running YouTube Sync. Clients can still use the SSID and phone URL shown here.";
  }

  const hotspotState = String(info.hotspotState || "Unknown").toLowerCase();
  if (hotspotState === "on") {
    return `Hotspot is on${info.hotspotSsid ? ` as '${info.hotspotSsid}'` : ""}. Phones can open ${info.recommendedUrl}.`;
  }

  if (hotspotState === "off") {
    return "Hotspot is off. Start it here when you want the laptop to broadcast its own SSID.";
  }

  if (hotspotState === "intransition") {
    return "Windows is still changing hotspot state. Wait a few seconds for the final SSID and phone URL.";
  }

  return "Phone access details are available, but Windows did not report a stable hotspot state.";
}

function updatePhoneAccessControls() {
  const canControlHotspot = Boolean(state.phoneAccessInfo?.canControlHotspot);
  const hotspotState = String(state.phoneAccessInfo?.hotspotState || "Unknown").toLowerCase();
  const isBusy = state.phoneAccessLoading || state.hotspotActionInFlight;
  elements.phoneAccessRefreshButton.disabled = isBusy;
  elements.phoneAccessStartButton.classList.toggle("hidden", !canControlHotspot);
  elements.phoneAccessStopButton.classList.toggle("hidden", !canControlHotspot);
  elements.phoneAccessStartButton.disabled = !canControlHotspot || isBusy || hotspotState === "on" || hotspotState === "intransition";
  elements.phoneAccessStopButton.disabled = !canControlHotspot || isBusy || hotspotState === "off" || hotspotState === "unknown" || hotspotState === "intransition";
  elements.phoneAccessRefreshButton.textContent = state.phoneAccessLoading ? "Refreshing..." : "Refresh Access";
  elements.phoneAccessStartButton.textContent =
    state.hotspotActionInFlight && state.hotspotAction === "start" ? "Starting..." : "Start Hotspot";
  elements.phoneAccessStopButton.textContent =
    state.hotspotActionInFlight && state.hotspotAction === "stop" ? "Stopping..." : "Stop Hotspot";
}

async function requestHotspot(action) {
  if (state.hotspotActionInFlight || !state.settingsOpen) {
    return;
  }

  state.hotspotActionInFlight = true;
  state.hotspotAction = action;
  elements.phoneAccessStatus.textContent = action === "start"
    ? "Starting Windows Mobile Hotspot..."
    : "Stopping Windows Mobile Hotspot...";
  updatePhoneAccessControls();

  try {
    const response = await post(`/api/hotspot/${action}`);
    if (response.snapshot) {
      state.phoneAccessLoaded = true;
      state.lastPhoneAccessRefreshAt = Date.now();
      renderPhoneAccess(response.snapshot);
    }

    elements.phoneAccessStatus.textContent = response.message || "Hotspot state updated.";
    showToast(response.message || "Hotspot state updated.", false);
  } catch (error) {
    const message = error instanceof Error ? error.message : "Hotspot control failed.";
    elements.phoneAccessStatus.textContent = message;
    showToast(message, true);
    await refreshPhoneAccess(true);
  } finally {
    state.hotspotActionInFlight = false;
    state.hotspotAction = "";
    updatePhoneAccessControls();
  }
}

async function refreshStatus(forceVideoRefresh) {
  try {
    const status = await fetchJson("/api/status");
    state.offlineNotified = false;
    updateStatus(status);

    if (state.settingsOpen && Date.now() - state.lastPhoneAccessRefreshAt >= 5000) {
      void refreshPhoneAccess(false);
    }

    const shouldRefreshVideos =
      forceVideoRefresh ||
      status.libraryVersion !== state.libraryVersion ||
      Date.now() - state.lastVideoRefreshAt > 20000;

    if (shouldRefreshVideos) {
      state.libraryVersion = status.libraryVersion;
      await refreshVideos();
    }
  } catch (error) {
    const message = error instanceof Error ? error.message : trayUnavailableMessage();
    state.isBusy = false;
    elements.monitorPill.dataset.state = "offline";
    elements.monitorPill.textContent = "Offline";
    elements.statusText.textContent = message;
    setMonitorOpen(true);
    updateSettingsControls();
    if (!state.offlineNotified) {
      showToast(message, true);
      state.offlineNotified = true;
    }
  }
}

async function refreshVideos() {
  const videos = await fetchJson("/api/videos");
  state.lastVideoRefreshAt = Date.now();
  state.downloadedVideoCount = Array.isArray(videos) ? videos.length : 0;
  reconcileVideos(videos);
  applyFilters();
  updateSelectionUi();
  updateEmptyState();
  elements.refreshSummary.textContent = `Updated ${new Date().toLocaleTimeString([], { hour: "numeric", minute: "2-digit", second: "2-digit" })}`;
}

function clearSelectedVideos(videoIds = null) {
  const targetIds = videoIds === null
    ? new Set(state.selectedIds)
    : new Set(videoIds);
  for (const videoId of targetIds) {
    state.selectedIds.delete(videoId);
    const card = state.cards.get(videoId);
    if (!card) {
      continue;
    }

    const selector = card.querySelector(".video-selector");
    selector.checked = false;
    card.classList.remove("selected");
  }
}

function isWatchedVideo(video) {
  return Boolean(video && (video.isWatched || video.isHidden));
}

function countWatchedVideos() {
  let watchedCount = 0;
  for (const video of state.videos.values()) {
    if (isWatchedVideo(video)) {
      watchedCount += 1;
    }
  }

  return watchedCount;
}

function getVisibleVideoCount() {
  return [...state.cards.values()].filter((card) => !card.classList.contains("is-hidden")).length;
}

async function maybeMarkCurrentVideoWatched(force) {
  const videoId = elements.videoPlayer.dataset.videoId || state.currentVideoId;
  if (!videoId || state.watchedMarkingIds.has(videoId)) {
    return;
  }

  const video = state.videos.get(videoId);
  if (!video || isWatchedVideo(video)) {
    return;
  }

  if (!force) {
    const duration = Number.isFinite(elements.videoPlayer.duration) ? elements.videoPlayer.duration : 0;
    if (duration <= 0 || elements.videoPlayer.currentTime < duration * 0.9) {
      return;
    }
  }

  await markVideos([videoId], false, true);
}

async function markVideos(videoIds, markHidden, silent = false) {
  const normalizedIds = [...new Set((Array.isArray(videoIds) ? videoIds : [])
    .filter((videoId) => typeof videoId === "string" && videoId.trim() !== "")
    .map((videoId) => videoId.trim()))];
  const idsToUpdate = normalizedIds.filter((videoId) => {
    const video = state.videos.get(videoId);
    if (!video) {
      return false;
    }

    return markHidden ? !video.isHidden : !isWatchedVideo(video);
  });

  if (idsToUpdate.length === 0) {
    return false;
  }

  for (const videoId of idsToUpdate) {
    state.watchedMarkingIds.add(videoId);
  }

  try {
    const response = await post("/api/remove", { videoIds: idsToUpdate, markHidden });
    applyLocalVideoStateUpdate(idsToUpdate, markHidden);
    if (!silent) {
      showToast(response?.message || (markHidden ? "Videos hidden." : "Marked as watched."), false);
    }

    return true;
  } catch (error) {
    if (!silent) {
      showToast(error instanceof Error ? error.message : "The request failed.", true);
    }

    return false;
  } finally {
    for (const videoId of idsToUpdate) {
      state.watchedMarkingIds.delete(videoId);
    }
  }
}

function applyLocalVideoStateUpdate(videoIds, markHidden) {
  clearSelectedVideos(videoIds);
  for (const videoId of videoIds) {
    const video = state.videos.get(videoId);
    if (!video) {
      continue;
    }

    const updatedVideo = {
      ...video,
      isWatched: true,
      isHidden: markHidden || Boolean(video.isHidden),
    };
    state.videos.set(videoId, updatedVideo);

    const card = state.cards.get(videoId);
    if (card) {
      updateCard(card, updatedVideo);
    }
  }

  applyFilters();
  updateSelectionUi();
  updateEmptyState();
}

async function restoreVideos(videoIds, silent = false) {
  const normalizedIds = [...new Set((Array.isArray(videoIds) ? videoIds : [])
    .filter((videoId) => typeof videoId === "string" && videoId.trim() !== "")
    .map((videoId) => videoId.trim()))];
  const idsToUpdate = normalizedIds.filter((videoId) => {
    const video = state.videos.get(videoId);
    return isWatchedVideo(video);
  });

  if (idsToUpdate.length === 0) {
    return false;
  }

  for (const videoId of idsToUpdate) {
    state.watchedMarkingIds.add(videoId);
  }

  try {
    const response = await post("/api/restore", { videoIds: idsToUpdate });
    applyLocalVideoRestore(idsToUpdate);
    if (!silent) {
      showToast(response?.message || "Videos restored to the main library.", false);
    }

    return true;
  } catch (error) {
    if (!silent) {
      showToast(error instanceof Error ? error.message : "The request failed.", true);
    }

    return false;
  } finally {
    for (const videoId of idsToUpdate) {
      state.watchedMarkingIds.delete(videoId);
    }
  }
}

function applyLocalVideoRestore(videoIds) {
  clearSelectedVideos(videoIds);
  for (const videoId of videoIds) {
    const video = state.videos.get(videoId);
    if (!video) {
      continue;
    }

    const updatedVideo = {
      ...video,
      isWatched: false,
      isHidden: false,
    };
    state.videos.set(videoId, updatedVideo);

    const card = state.cards.get(videoId);
    if (card) {
      updateCard(card, updatedVideo);
    }
  }

  applyFilters();
  updateSelectionUi();
  updateEmptyState();
}

function reconcileVideos(videos) {
  state.videos = new Map(videos.map((video) => [video.videoId, video]));
  const nextIds = new Set(videos.map((video) => video.videoId));

  for (const [videoId, card] of state.cards) {
    if (nextIds.has(videoId)) {
      continue;
    }

    state.captionTrackRequests.delete(videoId);
    state.captionTracksByVideoId.delete(videoId);
    state.cards.delete(videoId);
    state.selectedIds.delete(videoId);
    card.remove();
  }

  for (const video of videos) {
    let card = state.cards.get(video.videoId);
    if (!card) {
      card = createCard(video);
      state.cards.set(video.videoId, card);
    } else {
      updateCard(card, video);
    }

    elements.libraryGrid.append(card);
  }

  if (state.currentVideoId && !state.videos.has(state.currentVideoId)) {
    clearPlayer();
    return;
  }

  if (state.currentVideoId && state.videos.has(state.currentVideoId)) {
    syncPlayerMetadata(state.videos.get(state.currentVideoId));
  }
}

function createCard(video) {
  const card = document.createElement("article");
  card.className = "video-card";
  card.dataset.videoId = video.videoId;
  card.innerHTML = `
    <div class="thumb-shell">
      <button class="thumb-button" type="button" aria-label="">
        <img class="thumbnail placeholder" alt="">
      </button>
      <input class="video-selector" type="checkbox" aria-label="Select video">
    </div>
    <div class="card-body">
      <h2 class="video-title"></h2>
      <div class="video-meta">
        <p class="video-uploader hidden"></p>
        <span class="index-pill"></span>
      </div>
    </div>
  `;

  const selector = card.querySelector(".video-selector");
  selector.addEventListener("click", (event) => {
    event.stopPropagation();
  });
  selector.addEventListener("change", () => {
    if (selector.checked) {
      state.selectedIds.add(video.videoId);
      card.classList.add("selected");
    } else {
      state.selectedIds.delete(video.videoId);
      card.classList.remove("selected");
    }

    updateSelectionUi();
  });

  card.querySelector(".thumb-button").addEventListener("click", async () => {
    await playVideo(video.videoId);
  });

  updateCard(card, video);
  return card;
}

function updateCard(card, video) {
  card.dataset.videoId = video.videoId;
  const uploaderName = (video.uploaderName || "").trim();
  card.dataset.search = `${video.title} ${uploaderName} ${video.displayIndex}`.toLowerCase();

  const selector = card.querySelector(".video-selector");
  selector.checked = state.selectedIds.has(video.videoId);
  card.classList.toggle("selected", selector.checked);

  const thumbButton = card.querySelector(".thumb-button");
  thumbButton.setAttribute("aria-label", "Play " + video.title);

  card.querySelector(".index-pill").textContent = video.displayIndex;
  card.querySelector(".video-title").textContent = video.title;
  const uploader = card.querySelector(".video-uploader");
  if (uploaderName !== "") {
    uploader.textContent = uploaderName;
    uploader.classList.remove("hidden");
  } else {
    uploader.textContent = "";
    uploader.classList.add("hidden");
  }

  const image = card.querySelector(".thumbnail");
  if (image.dataset.src !== video.thumbnailUrl) {
    image.dataset.src = video.thumbnailUrl;
    image.classList.add("placeholder");
    image.src = video.thumbnailUrl;
  }

  image.alt = video.title;
  image.onerror = () => {
    image.classList.add("placeholder");
  };
  image.onload = () => {
    image.classList.remove("placeholder");
  };
}

async function playVideo(videoId) {
  const video = state.videos.get(videoId);
  if (!video) {
    return;
  }

  state.currentVideoId = video.videoId;
  state.currentVideoTitle = video.title;
  syncPlayerMetadata(video);
  elements.playerPlaceholder.classList.add("hidden");
  elements.videoPlayer.classList.remove("hidden");

  if (elements.videoPlayer.dataset.videoId !== video.videoId) {
    clearManagedCaptionTracks();
    elements.videoPlayer.dataset.videoId = video.videoId;
    elements.videoPlayer.src = video.streamUrl;
    elements.videoPlayer.load();
  }

  void refreshCaptionsForCurrentVideo(video.videoId);

  try {
    await elements.videoPlayer.play();
  } catch {
    // Browsers may delay autoplay until the media pipeline is ready. Controls remain visible either way.
  }

  elements.playerPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

function syncPlayerMetadata(video) {
  if (!video) {
    return;
  }

  elements.playerTitle.textContent = video.title;
  state.currentVideoTitle = video.title;
}

function clearPlayer() {
  state.currentVideoId = null;
  state.currentCaptionTracks = [];
  state.currentVideoTitle = "";
  elements.playerTitle.textContent = "Select a video";
  elements.playerPlaceholder.classList.remove("hidden");
  elements.videoPlayer.classList.add("hidden");
  elements.videoPlayer.pause();
  clearManagedCaptionTracks();
  resetCaptionUi();
  elements.videoPlayer.removeAttribute("src");
  elements.videoPlayer.load();
  delete elements.videoPlayer.dataset.videoId;
}

async function refreshCaptionsForCurrentVideo(videoId) {
  const video = state.videos.get(videoId);
  if (!video || state.currentVideoId !== videoId) {
    return;
  }

  if (state.captionTracksByVideoId.has(videoId)) {
    renderCaptionOptions(state.captionTracksByVideoId.get(videoId));
    return;
  }

  renderCaptionLoadingState();

  try {
    const tracks = await ensureCaptionTracks(video);
    if (state.currentVideoId !== videoId) {
      return;
    }

    renderCaptionOptions(tracks);
  } catch {
    if (state.currentVideoId !== videoId) {
      return;
    }

    renderCaptionUnavailable("Could not load captions for this video.");
  }
}

async function ensureCaptionTracks(video) {
  if (!video) {
    return [];
  }

  if (state.captionTracksByVideoId.has(video.videoId)) {
    return state.captionTracksByVideoId.get(video.videoId);
  }

  if (state.captionTrackRequests.has(video.videoId)) {
    return await state.captionTrackRequests.get(video.videoId);
  }

  const request = fetchJson(video.captionsUrl || `/api/videos/${encodeURIComponent(video.videoId)}/captions`)
    .then((tracks) => {
      const normalizedTracks = Array.isArray(tracks) ? tracks : [];
      state.captionTracksByVideoId.set(video.videoId, normalizedTracks);
      return normalizedTracks;
    })
    .finally(() => {
      state.captionTrackRequests.delete(video.videoId);
    });

  state.captionTrackRequests.set(video.videoId, request);
  return await request;
}

function renderCaptionLoadingState() {
  if (!state.currentVideoId) {
    resetCaptionUi();
    return;
  }

  state.currentCaptionTracks = [];
  elements.captionPicker.classList.remove("hidden");
  elements.captionStatus.classList.add("hidden");

  const loadingOption = document.createElement("option");
  loadingOption.value = "";
  loadingOption.textContent = "Loading captions...";
  elements.captionSelect.replaceChildren(loadingOption);
  elements.captionSelect.disabled = true;
  syncCaptionSelectWidth();
}

function renderCaptionOptions(tracks) {
  const captionTracks = Array.isArray(tracks) ? tracks : [];
  state.currentCaptionTracks = captionTracks;

  if (!state.currentVideoId) {
    resetCaptionUi();
    return;
  }

  if (captionTracks.length === 0) {
    clearManagedCaptionTracks();
    renderCaptionUnavailable("No downloaded captions for this video.");
    return;
  }

  const selectedTrackKey = resolvePreferredCaptionTrackKey(captionTracks);
  const options = document.createDocumentFragment();

  const offOption = document.createElement("option");
  offOption.value = "";
  offOption.textContent = "Off";
  options.append(offOption);

  for (const track of captionTracks) {
    const option = document.createElement("option");
    option.value = track.trackKey;
    option.textContent = track.label || track.trackKey;
    options.append(option);
  }

  elements.captionPicker.classList.remove("hidden");
  elements.captionStatus.classList.add("hidden");
  elements.captionSelect.replaceChildren(options);
  elements.captionSelect.disabled = false;
  elements.captionSelect.value = selectedTrackKey;
  syncCaptionSelectWidth();
  syncPlayerCaptionTracks(captionTracks, selectedTrackKey);
}

function renderCaptionUnavailable(message) {
  elements.captionPicker.classList.add("hidden");
  clearCaptionSelectWidth();
  if (message) {
    elements.captionStatus.classList.remove("hidden");
    elements.captionStatus.textContent = message;
  } else {
    elements.captionStatus.classList.add("hidden");
    elements.captionStatus.textContent = "";
  }
}

function resetCaptionUi() {
  elements.captionPicker.classList.add("hidden");
  elements.captionStatus.classList.add("hidden");
  elements.captionStatus.textContent = "";
  elements.captionSelect.replaceChildren();
  elements.captionSelect.disabled = true;
  clearCaptionSelectWidth();
}

function syncCaptionSelectWidth() {
  const selectedOption = elements.captionSelect.selectedOptions[0];
  const label = (selectedOption?.textContent || "").trim();
  if (label === "") {
    clearCaptionSelectWidth();
    return;
  }

  const styles = window.getComputedStyle(elements.captionSelect);
  const measure = document.createElement("span");
  measure.textContent = label;
  measure.style.position = "absolute";
  measure.style.visibility = "hidden";
  measure.style.whiteSpace = "pre";
  measure.style.fontFamily = styles.fontFamily;
  measure.style.fontSize = styles.fontSize;
  measure.style.fontWeight = styles.fontWeight;
  measure.style.fontStyle = styles.fontStyle;
  measure.style.letterSpacing = styles.letterSpacing;
  document.body.append(measure);

  const textWidth = measure.getBoundingClientRect().width;
  measure.remove();

  const paddingLeft = Number.parseFloat(styles.paddingLeft) || 0;
  const paddingRight = Number.parseFloat(styles.paddingRight) || 0;
  const borderLeft = Number.parseFloat(styles.borderLeftWidth) || 0;
  const borderRight = Number.parseFloat(styles.borderRightWidth) || 0;
  const indicatorAllowance = 28;
  const totalWidth = Math.ceil(
    textWidth
    + paddingLeft
    + paddingRight
    + borderLeft
    + borderRight
    + indicatorAllowance);

  elements.captionSelect.style.width = `${totalWidth}px`;
}

function clearCaptionSelectWidth() {
  elements.captionSelect.style.width = "";
}

function resolvePreferredCaptionTrackKey(tracks) {
  if (state.captionTrackPreference === "") {
    return "";
  }

  if (typeof state.captionTrackPreference === "string" && state.captionTrackPreference) {
    const exactTrack = tracks.find((track) => track.trackKey === state.captionTrackPreference);
    if (exactTrack) {
      return exactTrack.trackKey;
    }

    if (state.captionTrackPreferenceLanguageCode !== "") {
      const languageTrack = tracks.find((track) => track.languageCode === state.captionTrackPreferenceLanguageCode);
      if (languageTrack) {
        return languageTrack.trackKey;
      }
    }
  }

  return pickDefaultCaptionTrack(tracks);
}

function pickDefaultCaptionTrack(tracks) {
  return tracks.find((track) => track.trackKey === "en")?.trackKey
    || tracks.find((track) => track.trackKey.endsWith("-orig"))?.trackKey
    || tracks.find((track) => track.languageCode === "en")?.trackKey
    || tracks[0]?.trackKey
    || "";
}

function syncPlayerCaptionTracks(tracks, selectedTrackKey) {
  clearManagedCaptionTracks();

  for (const track of tracks) {
    const trackElement = document.createElement("track");
    trackElement.kind = "captions";
    trackElement.label = track.label || track.trackKey;
    trackElement.srclang = track.languageCode || "en";
    trackElement.src = track.trackUrl;
    trackElement.default = track.trackKey === selectedTrackKey;
    trackElement.dataset.managedCaption = "true";
    trackElement.dataset.trackKey = track.trackKey;
    elements.videoPlayer.append(trackElement);
  }

  window.requestAnimationFrame(() => {
    if (state.currentVideoId !== elements.videoPlayer.dataset.videoId) {
      return;
    }

    setActiveCaptionTrack(selectedTrackKey);
  });
}

function clearManagedCaptionTracks() {
  for (const trackElement of elements.videoPlayer.querySelectorAll("track[data-managed-caption='true']")) {
    try {
      trackElement.track.mode = "disabled";
    } catch {
      // Ignore browser track cleanup issues while replacing caption elements.
    }

    trackElement.remove();
  }
}

function applyCaptionSelection(selectedTrackKey) {
  setActiveCaptionTrack(selectedTrackKey);
}

function setActiveCaptionTrack(selectedTrackKey) {
  for (const trackElement of elements.videoPlayer.querySelectorAll("track[data-managed-caption='true']")) {
    const shouldShow = selectedTrackKey !== "" && trackElement.dataset.trackKey === selectedTrackKey;
    try {
      trackElement.track.mode = shouldShow ? "showing" : "disabled";
    } catch {
      // Some browsers populate HTMLTrackElement.track asynchronously; the next render will retry.
    }
  }
}

function applyFilters() {
  let visibleCount = 0;
  for (const card of state.cards.values()) {
    const video = state.videos.get(card.dataset.videoId);
    const matchesSearch = !state.searchMatcher || state.searchMatcher(card.dataset.search);
    const matchesWatchState = state.showWatchedOnly
      ? isWatchedVideo(video)
      : !isWatchedVideo(video);
    const isVisible = matchesSearch && matchesWatchState;
    card.classList.toggle("is-hidden", !isVisible);
    if (isVisible) {
      visibleCount += 1;
    }
  }

  if (state.searchTerm && visibleCount === 0 && state.cards.size > 0) {
    elements.refreshSummary.textContent = "No matches for the current search.";
  }

  return visibleCount;
}

function getRemoveSelectedButtonLabel() {
  return state.showWatchedOnly ? "Unhide Selected" : "Hide Selected";
}

function getWatchedVideosButtonLabel() {
  return state.showWatchedOnly ? "Unwatched Videos" : "Watched Videos";
}

function buildSearchMatcher(searchTerm) {
  if (!searchTerm) {
    return null;
  }

  if (!searchTerm.includes("*")) {
    return (haystack) => haystack.includes(searchTerm);
  }

  const escaped = searchTerm.replace(/[.+?^${}()|[\]\\]/g, "\\$&");
  const pattern = escaped.replace(/\*/g, ".*");
  const regex = new RegExp(pattern, "i");
  return (haystack) => regex.test(haystack);
}

function updateSelectionUi() {
  const selectedCount = state.selectedIds.size;
  const visibleCount = getVisibleVideoCount();

  elements.selectionSummary.textContent =
    selectedCount === 0
      ? buildLibrarySummary()
      : `${selectedCount} selected across ${visibleCount} visible videos`;

  elements.removeSelectedButton.disabled = selectedCount === 0;
  elements.removeSelectedButton.textContent = getRemoveSelectedButtonLabel();
  elements.watchedVideosButton.textContent = getWatchedVideosButtonLabel();
  elements.watchedVideosButton.classList.toggle("is-active", state.showWatchedOnly);
  elements.watchedVideosButton.setAttribute("aria-pressed", String(state.showWatchedOnly));
}

function buildLibrarySummary() {
  const downloadedCount = state.videos.size > 0 || state.downloadedVideoCount === 0
    ? state.videos.size
    : state.downloadedVideoCount;
  const watchedCount = countWatchedVideos();
  const activeCount = Math.max(downloadedCount - watchedCount, 0);
  const configuredDownloadCount = Number.isInteger(state.configuredDownloadCount)
    ? Math.max(state.configuredDownloadCount, 0)
    : null;
  const lines = [];
  const syncScopeLine = buildSyncScopeSummaryLine(configuredDownloadCount);

  if (syncScopeLine) {
    lines.push(syncScopeLine);
  }

  lines.push(`${activeCount} videos in the main library`);
  lines.push(`${watchedCount} watched or hidden`);

  if (configuredDownloadCount !== null) {
    lines.push(`sync limit ${configuredDownloadCount}`);
  }

  return lines.join("\n");
}

function buildSyncScopeSummaryLine(configuredDownloadCount) {
  const targetCount = Number.isInteger(state.syncScopeTargetCount)
    ? Math.max(state.syncScopeTargetCount, 0)
    : null;
  const downloadedCount = Number.isInteger(state.syncScopeDownloadedCount)
    ? Math.max(state.syncScopeDownloadedCount, 0)
    : null;
  const failedCount = Number.isInteger(state.syncScopeFailedCount)
    ? Math.max(state.syncScopeFailedCount, 0)
    : null;

  if (targetCount === null || downloadedCount === null) {
    return "";
  }

  const boundedTargetCount = configuredDownloadCount !== null
    ? Math.min(targetCount, configuredDownloadCount)
    : targetCount;
  let text = `${downloadedCount}/${boundedTargetCount} downloaded`;
  if (failedCount !== null && failedCount > 0) {
    text += ` - ${failedCount} failed`;
  }

  return text;
}

function updateStatus(status) {
  const previousSelectionKey = `${state.selectedBrowserAccountKey}|${state.selectedYouTubeAccountKey}`;
  const previousBrowserAccountKey = state.selectedBrowserAccountKey;
  state.isBusy = Boolean(status.isBusy);
  state.isRefreshingYouTubeAccounts = Boolean(status.isRefreshingYouTubeAccounts);
  state.syncAuthState = normalizeSyncAuthState(status.syncAuthState);
  state.syncAuthMessage = typeof status.syncAuthMessage === "string" && status.syncAuthMessage.trim() !== ""
    ? status.syncAuthMessage.trim()
    : "Authentication has not been verified yet for the selected account.";
  state.downloadedVideoCount = Number.isInteger(status.videoCount)
    ? status.videoCount
    : state.downloadedVideoCount;
  state.configuredDownloadCount = Number.isInteger(status.configuredDownloadCount)
    ? status.configuredDownloadCount
    : null;
  state.syncScopeDownloadedCount = Number.isInteger(status.syncScopeDownloadedCount)
    ? status.syncScopeDownloadedCount
    : null;
  state.syncScopeTargetCount = Number.isInteger(status.syncScopeTargetCount)
    ? status.syncScopeTargetCount
    : null;
  state.syncScopeFailedCount = Number.isInteger(status.syncScopeFailedCount)
    ? status.syncScopeFailedCount
    : null;
  state.watchLaterTotalCount = Number.isInteger(status.watchLaterTotalCount)
    ? status.watchLaterTotalCount
    : null;
  elements.monitorPill.dataset.state = status.isBusy ? "busy" : "ready";
  elements.monitorPill.textContent = status.isBusy ? "Busy" : "Ready";
  elements.statusText.textContent = status.status;
  elements.openDownloadsButton.classList.toggle("hidden", !status.canOpenDownloadsFolder);
  elements.openDownloadsButton.disabled = !status.canOpenDownloadsFolder;
  renderBrowserAccountPicker(
    Array.isArray(status.availableBrowserAccounts) ? status.availableBrowserAccounts : [],
    status.selectedBrowserAccountKey || "",
  );
  renderYouTubeAccountPicker(
    Array.isArray(status.availableYouTubeAccounts) ? status.availableYouTubeAccounts : [],
    status.selectedYouTubeAccountKey || "",
    {
      browserAccountKey: state.selectedBrowserAccountKey,
      previousBrowserAccountKey,
      isRefreshing: state.isRefreshingYouTubeAccounts,
    },
  );
  const nextSelectionKey = `${state.selectedBrowserAccountKey}|${state.selectedYouTubeAccountKey}`;
  if (previousSelectionKey !== nextSelectionKey) {
    state.captionTrackRequests.clear();
    state.captionTracksByVideoId.clear();
    state.currentCaptionTracks = [];
    clearManagedCaptionTracks();
    resetCaptionUi();
  }

  elements.syncButton.disabled = state.isBusy;
  updateSyncButtonState(state.isBusy);
  elements.settingsButton.disabled = false;
  elements.clearSelectionButton.disabled = state.isBusy && state.selectedIds.size === 0;
  elements.removeSelectedButton.disabled = state.selectedIds.size === 0;
  elements.removeSelectedButton.textContent = getRemoveSelectedButtonLabel();
  if (state.settingsOpen && state.isBusy && !state.settingsLoading && !state.settingsRefreshing && !state.settingsSaving) {
    elements.settingsSummary.textContent =
      "The app is busy. You can still change settings now; refresh the Watch Later total after the current operation finishes.";
  }
  updateSettingsControls();
  updateSelectionUi();
  renderActivityFeed(status.recentMessages || []);
}

function updateSyncButtonState(isBusy) {
  const authState = normalizeSyncAuthState(state.syncAuthState);
  elements.syncButton.dataset.authState = authState;
  elements.syncButton.title = state.syncAuthMessage;

  if (!isBusy) {
    if (state.syncButtonAnimationTimer !== null) {
      window.clearInterval(state.syncButtonAnimationTimer);
      state.syncButtonAnimationTimer = null;
    }

    state.syncButtonAnimationFrame = 0;
    elements.syncButton.textContent = "Sync Now";
    elements.syncButton.setAttribute("aria-label", `Sync Now. ${state.syncAuthMessage}`);
    return;
  }

  const frames = ["Syncing.", "Syncing..", "Syncing..."];
  elements.syncButton.textContent = frames[state.syncButtonAnimationFrame % frames.length];
  elements.syncButton.setAttribute("aria-label", `${elements.syncButton.textContent} ${state.syncAuthMessage}`);

  if (state.syncButtonAnimationTimer !== null) {
    return;
  }

  state.syncButtonAnimationTimer = window.setInterval(() => {
    state.syncButtonAnimationFrame = (state.syncButtonAnimationFrame + 1) % frames.length;
    elements.syncButton.textContent = frames[state.syncButtonAnimationFrame];
    elements.syncButton.setAttribute("aria-label", `${elements.syncButton.textContent} ${state.syncAuthMessage}`);
  }, 500);
}

function normalizeSyncAuthState(value) {
  return value === "ready" || value === "failed" ? value : "missing";
}

async function selectBrowserAccount(accountKey) {
  if (
    state.browserAccountSelectionInFlight ||
    accountKey === "" ||
    accountKey === state.selectedBrowserAccountKey
  ) {
    return;
  }

  const previousAccountKey = state.selectedBrowserAccountKey;
  state.browserAccountSelectionInFlight = true;
  setBrowserAccountMenuOpen(false);
  setYouTubeAccountMenuOpen(false);
  renderBrowserAccountPicker(state.browserAccounts, previousAccountKey);

  try {
    const response = await post("/api/browser-account/select", { accountKey });
    showToast(response.message || "Switched browser account.", false);
    await refreshStatus(true);
  } catch (error) {
    state.selectedBrowserAccountKey = previousAccountKey;
    showToast(error instanceof Error ? error.message : "Could not switch browser accounts.", true);
    await refreshStatus(false);
  } finally {
    state.browserAccountSelectionInFlight = false;
    renderBrowserAccountPicker(state.browserAccounts, state.selectedBrowserAccountKey);
  }
}

async function selectYouTubeAccount(accountKey) {
  if (
    state.youtubeAccountSelectionInFlight ||
    accountKey === "" ||
    accountKey === state.selectedYouTubeAccountKey
  ) {
    return;
  }

  const previousAccountKey = state.selectedYouTubeAccountKey;
  state.youtubeAccountSelectionInFlight = true;
  setYouTubeAccountMenuOpen(false);
  renderYouTubeAccountPicker(state.youtubeAccounts, previousAccountKey);

  try {
    const response = await post("/api/youtube-account/select", { accountKey });
    showToast(response.message || "Switched YouTube account.", false);
    await refreshStatus(true);
  } catch (error) {
    state.selectedYouTubeAccountKey = previousAccountKey;
    showToast(error instanceof Error ? error.message : "Could not switch YouTube accounts.", true);
    await refreshStatus(false);
  } finally {
    state.youtubeAccountSelectionInFlight = false;
    renderYouTubeAccountPicker(state.youtubeAccounts, state.selectedYouTubeAccountKey);
  }
}

function setBrowserAccountMenuOpen(isOpen) {
  const shouldOpen =
    isOpen &&
    !elements.browserAccountButton.disabled &&
    !elements.browserAccountPicker.classList.contains("hidden");
  elements.browserAccountMenu.classList.toggle("is-open", shouldOpen);
  elements.browserAccountList.classList.toggle("hidden", !shouldOpen);
  elements.browserAccountButton.setAttribute("aria-expanded", String(shouldOpen));
}

function focusSelectedBrowserAccountOption() {
  const selectedOption =
    elements.browserAccountList.querySelector(".account-menu-option.is-selected")
    || elements.browserAccountList.querySelector(".account-menu-option");
  if (selectedOption instanceof HTMLElement) {
    selectedOption.focus();
  }
}

function setYouTubeAccountMenuOpen(isOpen) {
  const shouldOpen =
    isOpen &&
    !elements.youtubeAccountButton.disabled &&
    !elements.youtubeAccountPicker.classList.contains("hidden");
  elements.youtubeAccountMenu.classList.toggle("is-open", shouldOpen);
  elements.youtubeAccountList.classList.toggle("hidden", !shouldOpen);
  elements.youtubeAccountButton.setAttribute("aria-expanded", String(shouldOpen));
}

function focusSelectedYouTubeAccountOption() {
  const selectedOption =
    elements.youtubeAccountList.querySelector(".account-menu-option.is-selected")
    || elements.youtubeAccountList.querySelector(".account-menu-option");
  if (selectedOption instanceof HTMLElement) {
    selectedOption.focus();
  }
}

function renderBrowserAccountPicker(accounts, selectedAccountKey) {
  state.browserAccounts = Array.isArray(accounts) ? accounts : [];
  if (state.browserAccounts.length === 0) {
    state.browserAccountButtonKey = "";
    state.browserAccountOptionsKey = "";
    state.selectedBrowserAccountKey = "";
    elements.browserAccountPicker.classList.add("hidden");
    elements.browserAccountButton.replaceChildren();
    elements.browserAccountButton.disabled = true;
    elements.browserAccountList.replaceChildren();
    setBrowserAccountMenuOpen(false);
    return;
  }

  elements.browserAccountPicker.classList.remove("hidden");
  const selectedKey = selectedAccountKey || state.browserAccounts[0].accountKey;
  const selectedAccount = state.browserAccounts.find((account) => account.accountKey === selectedKey)
    || state.browserAccounts[0];
  const accountOptionsKey = state.browserAccounts
    .map((account) => `${account.accountKey}|${account.label}|${account.avatarUrl || ""}`)
    .join("||");

  if (state.browserAccountOptionsKey !== accountOptionsKey) {
    const fragment = document.createDocumentFragment();
    for (const account of state.browserAccounts) {
      fragment.append(createBrowserAccountOption(account));
    }

    elements.browserAccountList.replaceChildren(fragment);
    state.browserAccountOptionsKey = accountOptionsKey;
  }

  const buttonKey = `${selectedAccount.accountKey}|${selectedAccount.label}|${selectedAccount.avatarUrl || ""}`;
  if (state.browserAccountButtonKey !== buttonKey) {
    renderBrowserAccountButton(selectedAccount);
    state.browserAccountButtonKey = buttonKey;
  }

  elements.browserAccountButton.disabled =
    state.browserAccountSelectionInFlight ||
    state.youtubeAccountSelectionInFlight ||
    state.browserAccounts.length === 1;
  state.selectedBrowserAccountKey = selectedAccount.accountKey;

  for (const option of elements.browserAccountList.querySelectorAll(".account-menu-option")) {
    if (!(option instanceof HTMLButtonElement)) {
      continue;
    }

    const isSelected = option.dataset.accountKey === selectedAccount.accountKey;
    option.classList.toggle("is-selected", isSelected);
    option.disabled = state.browserAccountSelectionInFlight || state.youtubeAccountSelectionInFlight;
    option.setAttribute("aria-selected", String(isSelected));
    option.tabIndex = isSelected ? 0 : -1;
  }

  if (elements.browserAccountButton.disabled) {
    setBrowserAccountMenuOpen(false);
  }
}

function renderBrowserAccountButton(account) {
  const label = account.label || "Browser account";
  elements.browserAccountButton.title = label;
  elements.browserAccountButton.setAttribute("aria-label", label);

  const avatar = createAccountAvatar(
    account.avatarUrl,
    account.displayName || account.email || account.label || account.browserName,
  );
  avatar.classList.add("account-avatar-compact");

  elements.browserAccountButton.replaceChildren(avatar);
}

function createBrowserAccountOption(account) {
  const option = document.createElement("button");
  option.type = "button";
  option.className = "account-menu-option";
  option.dataset.accountKey = account.accountKey;
  option.role = "option";
  option.title = account.label || "Browser account";
  option.append(createBrowserAccountContent(account));
  return option;
}

function createBrowserAccountContent(account) {
  const content = document.createElement("span");
  content.className = "account-choice-content";
  content.append(createAccountAvatar(
    account.avatarUrl,
    account.displayName || account.email || account.label || account.browserName,
  ));

  const text = document.createElement("span");
  text.className = "account-choice-text";

  const primary = document.createElement("span");
  primary.className = "account-choice-primary";
  primary.textContent = account.displayName || account.email || account.label || "Browser account";
  text.append(primary);

  const secondaryText = buildBrowserAccountSecondaryText(account, primary.textContent);
  if (secondaryText !== "") {
    const secondary = document.createElement("span");
    secondary.className = "account-choice-secondary";
    secondary.textContent = secondaryText;
    text.append(secondary);
  }

  content.append(text);
  return content;
}

function buildBrowserAccountSecondaryText(account, primaryText) {
  const parts = [];
  if (account.email && account.email !== primaryText) {
    parts.push(account.email);
  }

  const browserProfile = [account.browserName, account.profile].filter(Boolean).join(" / ");
  if (browserProfile !== "") {
    parts.push(browserProfile);
  }

  return parts.join(" / ");
}

function renderYouTubeAccountPicker(accounts, selectedAccountKey, options = {}) {
  const browserAccountKey = typeof options.browserAccountKey === "string"
    ? options.browserAccountKey
    : state.selectedBrowserAccountKey;
  const isRefreshing = Boolean(options.isRefreshing);
  const nextAccounts = Array.isArray(accounts) ? accounts : [];
  let effectiveAccounts = nextAccounts;
  let effectiveSelectedAccountKey = typeof selectedAccountKey === "string" ? selectedAccountKey : "";

  updateYouTubeAccountRefreshStatus(browserAccountKey, isRefreshing);

  if (nextAccounts.length > 0) {
    rememberYouTubeAccountsForBrowser(browserAccountKey, nextAccounts, effectiveSelectedAccountKey);
  } else {
    const cachedEntry = getCachedYouTubeAccountsForBrowser(browserAccountKey);
    if (cachedEntry !== null && cachedEntry.accounts.length > 0) {
      effectiveAccounts = cachedEntry.accounts;
      effectiveSelectedAccountKey = effectiveSelectedAccountKey || cachedEntry.selectedAccountKey || "";
    }
  }

  state.youtubeAccounts = effectiveAccounts;
  state.youtubeAccountsSourceBrowserKey = state.youtubeAccounts.length > 0 ? browserAccountKey : "";

  if (state.youtubeAccounts.length === 0) {
    state.youtubeAccountOptionsKey = "";
    state.selectedYouTubeAccountKey = effectiveSelectedAccountKey;
    elements.youtubeAccountList.replaceChildren();
    setYouTubeAccountMenuOpen(false);
    state.youtubeAccountButtonKey = "";
    elements.youtubeAccountPicker.classList.add("hidden");
    elements.youtubeAccountButton.replaceChildren();
    elements.youtubeAccountButton.disabled = true;
    return;
  }

  elements.youtubeAccountPicker.classList.remove("hidden");
  const selectedKey =
    effectiveSelectedAccountKey
    || state.selectedYouTubeAccountKey
    || state.youtubeAccounts[0].accountKey;
  const selectedAccount = state.youtubeAccounts.find((account) => account.accountKey === selectedKey)
    || state.youtubeAccounts[0];
  const accountOptionsKey = state.youtubeAccounts
    .map((account) => `${account.accountKey}|${account.label}|${account.avatarUrl || ""}`)
    .join("||");

  if (state.youtubeAccountOptionsKey !== accountOptionsKey) {
    const fragment = document.createDocumentFragment();
    for (const account of state.youtubeAccounts) {
      fragment.append(createYouTubeAccountOption(account));
    }

    elements.youtubeAccountList.replaceChildren(fragment);
    state.youtubeAccountOptionsKey = accountOptionsKey;
  }

  const buttonKey = `${selectedAccount.accountKey}|${selectedAccount.label}|${selectedAccount.avatarUrl || ""}`;
  if (state.youtubeAccountButtonKey !== buttonKey) {
    renderYouTubeAccountButton(selectedAccount);
    state.youtubeAccountButtonKey = buttonKey;
  }

  elements.youtubeAccountButton.disabled =
    state.browserAccountSelectionInFlight ||
    state.youtubeAccountSelectionInFlight;
  state.selectedYouTubeAccountKey = selectedAccount.accountKey;
  rememberSelectedYouTubeAccountForBrowser(browserAccountKey, selectedAccount.accountKey);

  for (const option of elements.youtubeAccountList.querySelectorAll(".account-menu-option")) {
    if (!(option instanceof HTMLButtonElement)) {
      continue;
    }

    const isSelected = option.dataset.accountKey === selectedAccount.accountKey;
    option.classList.toggle("is-selected", isSelected);
    option.disabled = state.browserAccountSelectionInFlight || state.youtubeAccountSelectionInFlight;
    option.setAttribute("aria-selected", String(isSelected));
    option.tabIndex = isSelected ? 0 : -1;
  }

  if (elements.youtubeAccountButton.disabled) {
    setYouTubeAccountMenuOpen(false);
  }
}

function updateYouTubeAccountRefreshStatus(browserAccountKey, isRefreshing) {
  if (!(elements.youtubeAccountRefreshStatus instanceof HTMLElement)) {
    return;
  }

  const shouldShow = browserAccountKey !== "" && isRefreshing;
  elements.youtubeAccountRefreshStatus.classList.toggle("hidden", !shouldShow);
  elements.youtubeAccountRefreshStatus.textContent = shouldShow ? "Refreshing YouTube accounts..." : "";
}

function getCachedYouTubeAccountsForBrowser(browserAccountKey) {
  if (typeof browserAccountKey !== "string" || browserAccountKey === "") {
    return null;
  }

  return state.youtubeAccountCacheByBrowserKey.get(browserAccountKey) || null;
}

function rememberYouTubeAccountsForBrowser(browserAccountKey, accounts, selectedAccountKey) {
  if (typeof browserAccountKey !== "string" || browserAccountKey === "" || !Array.isArray(accounts) || accounts.length === 0) {
    return;
  }

  state.youtubeAccountCacheByBrowserKey.set(browserAccountKey, {
    accounts: accounts.map((account) => ({ ...account })),
    selectedAccountKey: selectedAccountKey || "",
  });
}

function rememberSelectedYouTubeAccountForBrowser(browserAccountKey, selectedAccountKey) {
  if (typeof browserAccountKey !== "string" || browserAccountKey === "" || typeof selectedAccountKey !== "string") {
    return;
  }

  const cachedEntry = getCachedYouTubeAccountsForBrowser(browserAccountKey);
  if (cachedEntry !== null) {
    cachedEntry.selectedAccountKey = selectedAccountKey;
    return;
  }

  state.youtubeAccountCacheByBrowserKey.set(browserAccountKey, {
    accounts: [],
    selectedAccountKey,
  });
}

function renderYouTubeAccountButton(account) {
  elements.youtubeAccountButton.title = account.label || "YouTube account";
  elements.youtubeAccountButton.setAttribute("aria-label", account.label || "YouTube account");

  const content = createYouTubeAccountContent(account);
  content.classList.add("is-button");

  const chevron = document.createElement("span");
  chevron.className = "account-menu-chevron";
  chevron.setAttribute("aria-hidden", "true");
  chevron.textContent = "▾";

  elements.youtubeAccountButton.replaceChildren(content, chevron);
}

function createYouTubeAccountOption(account) {
  const option = document.createElement("button");
  option.type = "button";
  option.className = "account-menu-option";
  option.dataset.accountKey = account.accountKey;
  option.role = "option";
  option.title = account.label || "YouTube account";
  option.append(createYouTubeAccountContent(account));
  return option;
}

function createYouTubeAccountContent(account) {
  const content = document.createElement("span");
  content.className = "account-choice-content";
  content.append(createAccountAvatar(account.avatarUrl, account.displayName || account.handle || account.label));

  const text = document.createElement("span");
  text.className = "account-choice-text";

  const primary = document.createElement("span");
  primary.className = "account-choice-primary";
  primary.textContent = account.displayName || account.handle || account.label || "YouTube account";
  text.append(primary);

  const secondaryText = buildYouTubeAccountSecondaryText(account, primary.textContent);
  if (secondaryText !== "") {
    const secondary = document.createElement("span");
    secondary.className = "account-choice-secondary";
    secondary.textContent = secondaryText;
    text.append(secondary);
  }

  content.append(text);
  return content;
}

function buildYouTubeAccountSecondaryText(account, primaryText) {
  const parts = [];
  if (account.handle && account.handle !== primaryText) {
    parts.push(account.handle);
  }

  if (account.byline) {
    parts.push(account.byline);
  }

  return parts.join(" / ");
}

function createAccountAvatar(avatarUrl, label) {
  const avatar = document.createElement("span");
  avatar.className = "account-avatar is-fallback";

  const image = document.createElement("img");
  image.className = "account-avatar-image";
  image.alt = "";
  image.decoding = "async";
  image.loading = "eager";
  image.referrerPolicy = "no-referrer";

  const fallback = document.createElement("span");
  fallback.className = "account-avatar-fallback";
  fallback.textContent = getAccountInitial(label);

  if (avatarUrl) {
    const showImage = () => {
      avatar.classList.remove("is-fallback");
    };

    const showFallback = () => {
      avatar.classList.add("is-fallback");
      image.removeAttribute("src");
    };

    image.addEventListener("load", showImage, { once: true });
    image.addEventListener("error", showFallback, { once: true });
    image.src = avatarUrl;
    if (image.complete) {
      if (image.naturalWidth > 0) {
        showImage();
      } else {
        showFallback();
      }
    }
  }

  avatar.append(image, fallback);
  return avatar;
}

function getAccountInitial(label) {
  const match = (label || "Y").trim().match(/[A-Za-z0-9]/);
  return match ? match[0].toUpperCase() : "Y";
}

function setSearchOpen(isOpen) {
  elements.searchPanel.classList.toggle("is-open", isOpen);
  elements.searchPanel.setAttribute("aria-hidden", String(!isOpen));
  elements.searchToggle.setAttribute("aria-expanded", String(isOpen));
}

function setMonitorOpen(isOpen) {
  elements.monitorPanel.classList.toggle("hidden", !isOpen);
  elements.monitorPanel.setAttribute("aria-hidden", String(!isOpen));
  elements.monitorToggle.setAttribute("aria-expanded", String(isOpen));
}

function renderActivityFeed(messages) {
  elements.activityFeed.replaceChildren();

  const entries = messages.length > 0 ? [...messages].reverse() : ["Waiting for activity..."];
  for (const message of entries) {
    const item = document.createElement("li");
    item.textContent = message;
    elements.activityFeed.append(item);
  }
}

function updateEmptyState() {
  const hasAnyVideos = state.cards.size > 0;
  const isEmpty = getVisibleVideoCount() === 0;
  elements.emptyState.classList.toggle("hidden", !isEmpty);
  elements.libraryGrid.classList.toggle("hidden", isEmpty);
  if (isEmpty) {
    if (!hasAnyVideos) {
      elements.emptyStateTitle.textContent = "No downloaded videos yet";
      elements.emptyStateText.textContent = "Run a sync from the tray app or from the button above. New videos will appear here automatically.";
      clearPlayer();
      return;
    }

    if (state.showWatchedOnly) {
      elements.emptyStateTitle.textContent = "No watched videos yet";
      elements.emptyStateText.textContent = "Videos you watch past 90%, or videos you hide manually, will appear here.";
      return;
    }

    elements.emptyStateTitle.textContent = "No videos in the main library";
    elements.emptyStateText.textContent = "Everything downloaded here is already marked watched or hidden. Toggle Watched Videos to review them.";
  }
}

async function runCommand(url, payload, successMessage) {
  try {
    const response = await post(url, payload);
    showToast(response.message || successMessage, false);
    await refreshStatus(false);
  } catch (error) {
    showToast(error instanceof Error ? error.message : "The request failed.", true);
  }
}

async function fetchJson(url) {
  let response;
  try {
    response = await fetch(url, {
      cache: "no-store",
      headers: {
        "Accept": "application/json",
      },
    });
  } catch {
    throw new Error(trayUnavailableMessage());
  }

  if (!response.ok) {
    throw new Error(await readErrorMessage(response));
  }

  return await response.json();
}

async function post(url, payload) {
  let response;
  try {
    response = await fetch(url, {
      method: "POST",
      headers: payload ? { "Content-Type": "application/json" } : undefined,
      body: payload ? JSON.stringify(payload) : undefined,
    });
  } catch {
    throw new Error(trayUnavailableMessage());
  }

  const contentType = response.headers.get("Content-Type") || "";
  const data = contentType.includes("application/json") ? await response.json() : null;
  if (!response.ok) {
    throw new Error(data?.message || `The tray app returned ${response.status}.`);
  }

  return data;
}

function showToast(message, isError) {
  window.clearTimeout(state.toastTimer);
  elements.toast.textContent = message;
  elements.toast.classList.toggle("error", isError);
  elements.toast.classList.remove("hidden");
  state.toastTimer = window.setTimeout(() => {
    elements.toast.classList.add("hidden");
  }, 2800);
}

function trayUnavailableMessage() {
  return "Cannot reach the YouTube Sync tray app. If it was restarted, reopen Library from the tray menu or refresh this page.";
}

async function readErrorMessage(response) {
  const contentType = response.headers.get("Content-Type") || "";
  try {
    if (contentType.includes("application/json")) {
      const data = await response.json();
      if (data?.message) {
        return data.message;
      }
    }

    const text = (await response.text()).trim();
    if (text !== "") {
      return text;
    }
  } catch {
    // Fall back to a generic status message below.
  }

  if (response.status >= 500) {
    return `The YouTube Sync tray app returned ${response.status}. Check the tray app and tray-sync.log for details.`;
  }

  return `The YouTube Sync tray app returned ${response.status}.`;
}
