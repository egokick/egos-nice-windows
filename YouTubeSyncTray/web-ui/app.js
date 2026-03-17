const state = {
  browserAccountButtonKey: "",
  browserAccounts: [],
  browserAccountOptionsKey: "",
  browserAccountSelectionInFlight: false,
  cards: new Map(),
  currentVideoId: null,
  currentVideoTitle: "",
  libraryVersion: -1,
  lastVideoRefreshAt: 0,
  offlineNotified: false,
  refreshTimer: null,
  searchMatcher: null,
  searchTerm: "",
  configuredDownloadCount: null,
  selectedBrowserAccountKey: "",
  selectedIds: new Set(),
  syncButtonAnimationFrame: 0,
  syncButtonAnimationTimer: null,
  toastTimer: null,
  watchLaterTotalCount: null,
  youtubeAccounts: [],
  youtubeAccountButtonKey: "",
  youtubeAccountOptionsKey: "",
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
  selectionSummary: document.getElementById("selectionSummary"),
  settingsButton: document.getElementById("settingsButton"),
  statusText: document.getElementById("statusText"),
  syncButton: document.getElementById("syncButton"),
  toast: document.getElementById("toast"),
  videoPlayer: document.getElementById("videoPlayer"),
  youtubeAccountButton: document.getElementById("youtubeAccountButton"),
  youtubeAccountList: document.getElementById("youtubeAccountList"),
  youtubeAccountMenu: document.getElementById("youtubeAccountMenu"),
  youtubeAccountPicker: document.getElementById("youtubeAccountPicker"),
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

  elements.settingsButton.addEventListener("click", async () => {
    await runCommand("/api/settings/open", null, "Settings opened.");
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
    state.searchMatcher = buildSearchMatcher(state.searchTerm);
    applyFilters();
    updateSelectionUi();
  });

  elements.searchToggle.addEventListener("click", () => {
    const isOpen = elements.searchPanel.classList.contains("is-open");
    if (isOpen && elements.searchInput.value.trim() !== "") {
      elements.searchInput.value = "";
      state.searchTerm = "";
      state.searchMatcher = null;
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
      if (!elements.browserAccountList.classList.contains("hidden")) {
        setBrowserAccountMenuOpen(false);
      }

      if (!elements.youtubeAccountList.classList.contains("hidden")) {
        setYouTubeAccountMenuOpen(false);
      }

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
      </div>
      <input class="video-selector" type="checkbox" aria-label="Select video">
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
    const matches = !state.searchMatcher || state.searchMatcher(card.dataset.search);
    card.classList.toggle("is-hidden", !matches);
    if (matches) {
      visibleCount += 1;
    }
  }

  if (state.searchTerm && visibleCount === 0 && state.cards.size > 0) {
    elements.refreshSummary.textContent = "No matches for the current search.";
  }
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
  const visibleCount = [...state.cards.values()].filter((card) => !card.classList.contains("is-hidden")).length;
  const downloadedCount = state.cards.size;

  elements.selectionSummary.textContent =
    selectedCount === 0
      ? buildLibrarySummary(downloadedCount)
      : `${selectedCount} selected across ${visibleCount} visible videos`;

  elements.removeSelectedButton.disabled = selectedCount === 0;
}

function buildLibrarySummary(downloadedCount) {
  const configuredDownloadCount = Number.isInteger(state.configuredDownloadCount)
    ? Math.max(state.configuredDownloadCount, 0)
    : null;
  const watchLaterTotalCount = Number.isInteger(state.watchLaterTotalCount)
    ? Math.max(state.watchLaterTotalCount, 0)
    : null;

  if (configuredDownloadCount !== null && watchLaterTotalCount !== null) {
    return `${downloadedCount} downloaded on disk, ${configuredDownloadCount} configured to download, ${watchLaterTotalCount} total in playlist`;
  }

  if (configuredDownloadCount !== null) {
    return `${downloadedCount} downloaded on disk, ${configuredDownloadCount} configured to download`;
  }

  if (watchLaterTotalCount !== null) {
    return `${downloadedCount} downloaded on disk, ${watchLaterTotalCount} total in playlist`;
  }

  return `${downloadedCount} downloaded videos`;
}

function updateStatus(status) {
  state.configuredDownloadCount = Number.isInteger(status.downloadCount)
    ? status.downloadCount
    : null;
  state.watchLaterTotalCount = Number.isInteger(status.watchLaterTotalCount)
    ? status.watchLaterTotalCount
    : null;
  elements.monitorPill.dataset.state = status.isBusy ? "busy" : "ready";
  elements.monitorPill.textContent = status.isBusy ? "Busy" : "Ready";
  elements.statusText.textContent = status.status;
  renderBrowserAccountPicker(
    Array.isArray(status.availableBrowserAccounts) ? status.availableBrowserAccounts : [],
    status.selectedBrowserAccountKey || "",
  );
  renderYouTubeAccountPicker(
    Array.isArray(status.availableYouTubeAccounts) ? status.availableYouTubeAccounts : [],
    status.selectedYouTubeAccountKey || "",
  );

  elements.syncButton.disabled = status.isBusy;
  updateSyncButtonState(status.isBusy);
  elements.settingsButton.disabled = false;
  elements.clearSelectionButton.disabled = status.isBusy && state.selectedIds.size === 0;
  elements.removeSelectedButton.disabled = state.selectedIds.size === 0;
  elements.removeSelectedButton.textContent = status.isBusy ? "Queue Remove Selected" : "Remove Selected";
  updateSelectionUi();
  renderActivityFeed(status.recentMessages || []);
}

function updateSyncButtonState(isBusy) {
  if (!isBusy) {
    if (state.syncButtonAnimationTimer !== null) {
      window.clearInterval(state.syncButtonAnimationTimer);
      state.syncButtonAnimationTimer = null;
    }

    state.syncButtonAnimationFrame = 0;
    elements.syncButton.textContent = "Sync Now";
    return;
  }

  const frames = ["Syncing.", "Syncing..", "Syncing..."];
  elements.syncButton.textContent = frames[state.syncButtonAnimationFrame % frames.length];

  if (state.syncButtonAnimationTimer !== null) {
    return;
  }

  state.syncButtonAnimationTimer = window.setInterval(() => {
    state.syncButtonAnimationFrame = (state.syncButtonAnimationFrame + 1) % frames.length;
    elements.syncButton.textContent = frames[state.syncButtonAnimationFrame];
  }, 500);
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

function renderYouTubeAccountPicker(accounts, selectedAccountKey) {
  state.youtubeAccounts = Array.isArray(accounts) ? accounts : [];
  if (state.youtubeAccounts.length === 0) {
    state.youtubeAccountButtonKey = "";
    state.youtubeAccountOptionsKey = "";
    state.selectedYouTubeAccountKey = "";
    elements.youtubeAccountPicker.classList.add("hidden");
    elements.youtubeAccountButton.replaceChildren();
    elements.youtubeAccountButton.disabled = true;
    elements.youtubeAccountList.replaceChildren();
    setYouTubeAccountMenuOpen(false);
    return;
  }

  elements.youtubeAccountPicker.classList.remove("hidden");
  const selectedKey = selectedAccountKey || state.youtubeAccounts[0].accountKey;
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
    state.youtubeAccountSelectionInFlight ||
    state.youtubeAccounts.length === 1;
  state.selectedYouTubeAccountKey = selectedAccount.accountKey;

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

function renderYouTubeAccountButton(account) {
  elements.youtubeAccountButton.title = account.label || "YouTube account";

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
