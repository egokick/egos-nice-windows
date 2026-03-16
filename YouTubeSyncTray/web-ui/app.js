const state = {
  cards: new Map(),
  currentVideoId: null,
  currentVideoTitle: "",
  libraryVersion: -1,
  lastVideoRefreshAt: 0,
  offlineNotified: false,
  refreshTimer: null,
  searchTerm: "",
  selectedIds: new Set(),
  toastTimer: null,
  videos: new Map(),
};

const elements = {
  activityFeed: document.getElementById("activityFeed"),
  clearSelectionButton: document.getElementById("clearSelectionButton"),
  emptyState: document.getElementById("emptyState"),
  libraryGrid: document.getElementById("libraryGrid"),
  monitorClose: document.getElementById("monitorClose"),
  monitorPanel: document.getElementById("monitorPanel"),
  monitorPill: document.getElementById("monitorPill"),
  monitorToggle: document.getElementById("monitorToggle"),
  playerPanel: document.getElementById("playerPanel"),
  playerPlaceholder: document.getElementById("playerPlaceholder"),
  playerTitle: document.getElementById("playerTitle"),
  refreshSummary: document.getElementById("refreshSummary"),
  removeSelectedButton: document.getElementById("removeSelectedButton"),
  searchInput: document.getElementById("searchInput"),
  searchPanel: document.getElementById("searchPanel"),
  searchToggle: document.getElementById("searchToggle"),
  selectVisibleButton: document.getElementById("selectVisibleButton"),
  selectionSummary: document.getElementById("selectionSummary"),
  settingsButton: document.getElementById("settingsButton"),
  statusText: document.getElementById("statusText"),
  syncButton: document.getElementById("syncButton"),
  toast: document.getElementById("toast"),
  videoPlayer: document.getElementById("videoPlayer"),
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
  elements.syncButton.addEventListener("click", async () => {
    await runCommand("/api/sync", null, "Sync requested.");
  });

  elements.settingsButton.addEventListener("click", async () => {
    await runCommand("/api/settings/open", null, "Settings opened.");
  });

  elements.selectVisibleButton.addEventListener("click", () => {
    for (const [videoId, card] of state.cards) {
      if (card.classList.contains("is-hidden")) {
        continue;
      }

      state.selectedIds.add(videoId);
      card.querySelector(".video-selector").checked = true;
      card.classList.add("selected");
    }

    updateSelectionUi();
  });

  elements.clearSelectionButton.addEventListener("click", () => {
    state.selectedIds.clear();
    for (const card of state.cards.values()) {
      card.querySelector(".video-selector").checked = false;
      card.classList.remove("selected");
    }

    updateSelectionUi();
  });

  elements.removeSelectedButton.addEventListener("click", async () => {
    const ids = [...state.selectedIds];
    if (ids.length === 0) {
      showToast("Select one or more videos first.", true);
      return;
    }

    const confirmed = window.confirm(
      `Remove ${ids.length} selected video(s) from Watch Later? Local files stay on disk.`,
    );
    if (!confirmed) {
      return;
    }

    await runCommand("/api/remove", { videoIds: ids }, `Queued removal for ${ids.length} video(s).`);
  });

  elements.searchInput.addEventListener("input", () => {
    state.searchTerm = elements.searchInput.value.trim().toLowerCase();
    applyFilters();
    updateSelectionUi();
  });

  elements.searchToggle.addEventListener("click", () => {
    const isOpen = elements.searchPanel.classList.contains("is-open");
    if (isOpen && elements.searchInput.value.trim() !== "") {
      elements.searchInput.value = "";
      state.searchTerm = "";
      applyFilters();
      updateSelectionUi();
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

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      if (!elements.monitorPanel.classList.contains("hidden")) {
        setMonitorOpen(false);
      }

      if (elements.searchPanel.classList.contains("is-open") && elements.searchInput.value.trim() === "") {
        setSearchOpen(false);
      }
    }
  });
}

async function refreshStatus(forceVideoRefresh) {
  try {
    const status = await fetchJson("/api/status");
    state.offlineNotified = false;
    updateStatus(status);

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
    elements.monitorPill.dataset.state = "offline";
    elements.monitorPill.textContent = "Offline";
    elements.statusText.textContent = message;
    setMonitorOpen(true);
    if (!state.offlineNotified) {
      showToast(message, true);
      state.offlineNotified = true;
    }
  }
}

async function refreshVideos() {
  const videos = await fetchJson("/api/videos");
  state.lastVideoRefreshAt = Date.now();
  reconcileVideos(videos);
  applyFilters();
  updateSelectionUi();
  updateEmptyState(videos.length === 0);
  elements.refreshSummary.textContent = `Updated ${new Date().toLocaleTimeString([], { hour: "numeric", minute: "2-digit", second: "2-digit" })}`;
}

function reconcileVideos(videos) {
  state.videos = new Map(videos.map((video) => [video.videoId, video]));
  const nextIds = new Set(videos.map((video) => video.videoId));

  for (const [videoId, card] of state.cards) {
    if (nextIds.has(videoId)) {
      continue;
    }

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
      <div class="thumb-overlay">
        <span class="index-pill"></span>
        <input class="video-selector" type="checkbox" aria-label="Select video">
      </div>
    </div>
    <div class="card-body">
      <h2 class="video-title"></h2>
      <p class="video-uploader hidden"></p>
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
    elements.videoPlayer.dataset.videoId = video.videoId;
    elements.videoPlayer.src = video.streamUrl;
    elements.videoPlayer.load();
  }

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
  state.currentVideoTitle = "";
  elements.playerTitle.textContent = "Select a video";
  elements.playerPlaceholder.classList.remove("hidden");
  elements.videoPlayer.classList.add("hidden");
  elements.videoPlayer.pause();
  elements.videoPlayer.removeAttribute("src");
  elements.videoPlayer.load();
  delete elements.videoPlayer.dataset.videoId;
}

function applyFilters() {
  let visibleCount = 0;
  for (const card of state.cards.values()) {
    const matches = !state.searchTerm || card.dataset.search.includes(state.searchTerm);
    card.classList.toggle("is-hidden", !matches);
    if (matches) {
      visibleCount += 1;
    }
  }

  if (state.searchTerm && visibleCount === 0 && state.cards.size > 0) {
    elements.refreshSummary.textContent = "No matches for the current search.";
  }
}

function updateSelectionUi() {
  const selectedCount = state.selectedIds.size;
  const visibleCount = [...state.cards.values()].filter((card) => !card.classList.contains("is-hidden")).length;
  const totalCount = state.cards.size;

  elements.selectionSummary.textContent =
    selectedCount === 0
      ? `${visibleCount} visible of ${totalCount} downloaded videos`
      : `${selectedCount} selected across ${visibleCount} visible videos`;

  elements.removeSelectedButton.disabled = selectedCount === 0;
}

function updateStatus(status) {
  elements.monitorPill.dataset.state = status.isBusy ? "busy" : "ready";
  elements.monitorPill.textContent = status.isBusy ? "Busy" : "Ready";
  elements.statusText.textContent = status.status;

  elements.syncButton.disabled = status.isBusy;
  elements.settingsButton.disabled = status.isBusy;
  elements.selectVisibleButton.disabled = status.isBusy;
  elements.clearSelectionButton.disabled = status.isBusy && state.selectedIds.size === 0;
  elements.removeSelectedButton.disabled = status.isBusy || state.selectedIds.size === 0;
  renderActivityFeed(status.recentMessages || []);
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

function updateEmptyState(isEmpty) {
  elements.emptyState.classList.toggle("hidden", !isEmpty);
  elements.libraryGrid.classList.toggle("hidden", isEmpty);
  if (isEmpty) {
    clearPlayer();
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
