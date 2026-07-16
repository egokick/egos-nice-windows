const PKCE_STORAGE_KEY = "stayactive.remotehub.admin.pkce.v1";
const PKCE_MAX_AGE_MS = 10 * 60 * 1000;
const CAPABILITIES = ["ExitNode", "ScreenView", "SendFile", "RequestFile"];

const appState = {
  config: null,
  discovery: null,
  accessToken: null,
  accessTokenExpiresAt: 0,
  inventory: []
};

const elements = {
  status: document.querySelector("#status"),
  sessionState: document.querySelector("#session-state"),
  signIn: document.querySelector("#sign-in"),
  signOut: document.querySelector("#sign-out"),
  adminPanel: document.querySelector("#admin-panel"),
  refresh: document.querySelector("#refresh"),
  inventoryRows: document.querySelector("#inventory-rows"),
  auditRows: document.querySelector("#audit-rows"),
  form: document.querySelector("#inventory-form"),
  nodeId: document.querySelector("#node-id"),
  expectedVersion: document.querySelector("#expected-version"),
  ownerOptIn: document.querySelector("#owner-opt-in"),
  ownerName: document.querySelector("#owner-name"),
  locationOptIn: document.querySelector("#location-opt-in"),
  coarseLocation: document.querySelector("#coarse-location"),
  meshCentralNodeId: document.querySelector("#meshcentral-node-id"),
  verified: document.querySelector("#verified"),
  newMapping: document.querySelector("#new-mapping"),
  save: document.querySelector("#save")
};

void bootstrap();

async function bootstrap() {
  wireEvents();
  try {
    appState.config = await loadPublicConfiguration();
    validateBrowserOrigin(appState.config);
    await handleAuthorizationCallback();
    renderAuthenticationState();
    if (hasUsableAccessToken()) {
      await refreshData();
    } else {
      showStatus("Sign in with the configured self-hosted OIDC provider to manage inventory.", "info");
    }
  } catch (error) {
    clearAccessToken();
    renderAuthenticationState();
    showStatus(toSafeMessage(error), "error");
  }
}

function wireEvents() {
  elements.signIn.addEventListener("click", () => void beginSignIn());
  elements.signOut.addEventListener("click", () => {
    clearAccessToken();
    resetForm();
    renderAuthenticationState();
    showStatus("Local browser session cleared. No provider logout request was made.", "info");
  });
  elements.refresh.addEventListener("click", () => void refreshData());
  elements.form.addEventListener("submit", (event) => {
    event.preventDefault();
    void saveInventoryMapping();
  });
  elements.newMapping.addEventListener("click", resetForm);
  elements.ownerOptIn.addEventListener("change", syncOptInInputs);
  elements.locationOptIn.addEventListener("change", syncOptInInputs);
}

async function loadPublicConfiguration() {
  const response = await fetch("/admin/config.json", {
    cache: "no-store",
    credentials: "same-origin",
    headers: { "Accept": "application/json" }
  });
  if (!response.ok) {
    throw new Error("RemoteHub Admin configuration is unavailable.");
  }

  const configuration = await response.json();
  if (!configuration
    || typeof configuration.authority !== "string"
    || typeof configuration.clientId !== "string"
    || typeof configuration.redirectUri !== "string"
    || !Array.isArray(configuration.scopes)) {
    throw new Error("RemoteHub Admin configuration is malformed.");
  }

  return configuration;
}

function validateBrowserOrigin(configuration) {
  const redirectUri = new URL(configuration.redirectUri);
  if (redirectUri.origin !== window.location.origin || redirectUri.pathname !== "/admin/") {
    throw new Error("This Admin page must be loaded from its configured public origin and /admin/ redirect path.");
  }
}

async function beginSignIn() {
  try {
    requireWebCrypto();
    elements.signIn.disabled = true;
    showStatus("Preparing a PKCE sign-in request…", "info");
    const discovery = await getDiscovery();
    assertPkceDiscovery(discovery);

    const verifier = randomBase64Url(64);
    const state = randomBase64Url(32);
    const challenge = await sha256Base64Url(verifier);
    sessionStorage.setItem(PKCE_STORAGE_KEY, JSON.stringify({ verifier, state, createdAt: Date.now() }));

    const authorizationUrl = new URL(discovery.authorization_endpoint);
    authorizationUrl.search = new URLSearchParams({
      response_type: "code",
      client_id: appState.config.clientId,
      redirect_uri: appState.config.redirectUri,
      scope: appState.config.scopes.join(" "),
      state,
      code_challenge: challenge,
      code_challenge_method: "S256"
    }).toString();
    window.location.assign(authorizationUrl.toString());
  } catch (error) {
    elements.signIn.disabled = false;
    showStatus(toSafeMessage(error), "error");
  }
}

async function handleAuthorizationCallback() {
  const callback = new URLSearchParams(window.location.search);
  const providerError = callback.get("error");
  if (providerError) {
    sessionStorage.removeItem(PKCE_STORAGE_KEY);
    removeAuthorizationQuery();
    throw new Error("OIDC sign-in was not completed by the provider.");
  }

  const authorizationCode = callback.get("code");
  if (!authorizationCode) {
    return;
  }

  const returnedState = callback.get("state");
  const pending = readPendingPkce();
  sessionStorage.removeItem(PKCE_STORAGE_KEY);
  if (!pending || returnedState !== pending.state || Date.now() - pending.createdAt > PKCE_MAX_AGE_MS) {
    removeAuthorizationQuery();
    throw new Error("OIDC sign-in state could not be verified. Start sign-in again.");
  }

  const discovery = await getDiscovery();
  assertPkceDiscovery(discovery);
  const response = await fetch(discovery.token_endpoint, {
    method: "POST",
    cache: "no-store",
    credentials: "omit",
    referrerPolicy: "no-referrer",
    headers: { "Content-Type": "application/x-www-form-urlencoded", "Accept": "application/json" },
    body: new URLSearchParams({
      grant_type: "authorization_code",
      code: authorizationCode,
      redirect_uri: appState.config.redirectUri,
      client_id: appState.config.clientId,
      code_verifier: pending.verifier
    })
  });
  removeAuthorizationQuery();
  if (!response.ok) {
    throw new Error("OIDC token exchange failed. Confirm the public client, exact redirect URI, and PKCE policy.");
  }

  const token = await response.json();
  if (!token || typeof token.access_token !== "string" || token.access_token.length === 0
    || (typeof token.token_type === "string" && token.token_type.toLowerCase() !== "bearer")) {
    throw new Error("OIDC provider did not return a usable bearer access token.");
  }

  const lifetimeSeconds = Number.isFinite(Number(token.expires_in))
    ? Math.max(30, Math.min(Number(token.expires_in), 24 * 60 * 60))
    : 5 * 60;
  appState.accessToken = token.access_token;
  appState.accessTokenExpiresAt = Date.now() + lifetimeSeconds * 1000;
  showStatus("Signed in. Access token remains only in this page's memory and will not be refreshed or persisted.", "success");
}

async function getDiscovery() {
  if (appState.discovery) {
    return appState.discovery;
  }

  const authority = new URL(appState.config.authority);
  const issuerBase = authority.href.endsWith("/") ? authority.href : `${authority.href}/`;
  const discoveryUrl = new URL(".well-known/openid-configuration", issuerBase);
  const response = await fetch(discoveryUrl, {
    cache: "no-store",
    credentials: "omit",
    referrerPolicy: "no-referrer",
    headers: { "Accept": "application/json" }
  });
  if (!response.ok) {
    throw new Error("Unable to load the configured OIDC discovery document.");
  }

  const discovery = await response.json();
  if (!discovery
    || typeof discovery.issuer !== "string"
    || typeof discovery.authorization_endpoint !== "string"
    || typeof discovery.token_endpoint !== "string") {
    throw new Error("Configured OIDC discovery document is incomplete.");
  }
  if (normalizeIssuer(discovery.issuer) !== normalizeIssuer(appState.config.authority)) {
    throw new Error("OIDC discovery issuer does not match the configured issuer.");
  }
  requireHttpsUrl(discovery.authorization_endpoint, "OIDC authorization endpoint");
  requireHttpsUrl(discovery.token_endpoint, "OIDC token endpoint");
  appState.discovery = discovery;
  return discovery;
}

function assertPkceDiscovery(discovery) {
  if (!Array.isArray(discovery.code_challenge_methods_supported)
    || !discovery.code_challenge_methods_supported.includes("S256")) {
    throw new Error("Configured OIDC provider does not advertise required S256 PKCE support.");
  }
  if (!Array.isArray(discovery.token_endpoint_auth_methods_supported)
    || !discovery.token_endpoint_auth_methods_supported.includes("none")) {
    throw new Error("Configured OIDC provider does not permit public-client token exchange without a client secret.");
  }
}

async function refreshData() {
  try {
    setBusy(true);
    const [inventoryResponse, auditResponse] = await Promise.all([
      apiFetch("/api/v1/admin/inventory"),
      apiFetch("/api/v1/admin/audit?take=100")
    ]);
    if (!inventoryResponse.ok || !auditResponse.ok) {
      await handleApiFailure(inventoryResponse.ok ? auditResponse : inventoryResponse);
      return;
    }

    const [inventory, audit] = await Promise.all([inventoryResponse.json(), auditResponse.json()]);
    appState.inventory = Array.isArray(inventory.devices) ? inventory.devices : [];
    renderInventory(appState.inventory);
    renderAudit(Array.isArray(audit.events) ? audit.events : []);
    showStatus("Inventory and audit history refreshed.", "success");
  } catch (error) {
    showStatus(toSafeMessage(error), "error");
  } finally {
    setBusy(false);
  }
}

async function saveInventoryMapping() {
  try {
    setBusy(true);
    const mapping = collectInventoryForm();
    const response = await apiFetch(`/api/v1/admin/inventory/${encodeURIComponent(mapping.nodeId)}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(mapping.body)
    });
    if (response.status === 409) {
      await refreshData();
      throw new Error("This mapping changed elsewhere. The latest version is loaded; review it before saving.");
    }
    if (!response.ok) {
      await handleApiFailure(response);
      return;
    }

    const saved = await response.json();
    populateForm(saved);
    await refreshData();
    showStatus(`Inventory mapping for ${saved.headscaleNodeId} was saved and audited.`, "success");
  } catch (error) {
    showStatus(toSafeMessage(error), "error");
  } finally {
    setBusy(false);
  }
}

function collectInventoryForm() {
  const nodeId = elements.nodeId.value.trim();
  const expectedVersion = Number(elements.expectedVersion.value);
  const ownerDisplayNameOptIn = elements.ownerOptIn.checked;
  const coarseLocationOptIn = elements.locationOptIn.checked;
  const ownerDisplayName = ownerDisplayNameOptIn ? elements.ownerName.value.trim() : null;
  const coarseLocation = coarseLocationOptIn ? elements.coarseLocation.value.trim() : null;
  const allowedCapabilities = checkedCapabilities();
  const meshCentralNodeId = elements.meshCentralNodeId.value.trim() || null;

  if (!nodeId || !Number.isSafeInteger(expectedVersion) || expectedVersion < 0) {
    throw new Error("Provide a valid Headscale node ID and non-negative expected version.");
  }
  if (ownerDisplayNameOptIn && !ownerDisplayName) {
    throw new Error("An opted-in owner display name is required.");
  }
  if (coarseLocationOptIn && !coarseLocation) {
    throw new Error("An opted-in coarse location is required.");
  }
  if (allowedCapabilities.some((capability) => capability !== "ExitNode") && !meshCentralNodeId) {
    throw new Error("Screen and file capabilities require a MeshCentral node ID.");
  }

  return {
    nodeId,
    body: {
      expectedVersion,
      ownerDisplayNameOptIn,
      ownerDisplayName,
      coarseLocationOptIn,
      coarseLocation,
      meshCentralNodeId,
      verified: elements.verified.checked,
      allowedCapabilities
    }
  };
}

async function apiFetch(path, options = {}) {
  if (!hasUsableAccessToken()) {
    clearAccessToken();
    renderAuthenticationState();
    throw new Error("Your access token is unavailable or expired. Sign in again.");
  }

  const headers = new Headers(options.headers || {});
  headers.set("Authorization", `Bearer ${appState.accessToken}`);
  headers.set("Accept", "application/json");
  return fetch(path, {
    ...options,
    cache: "no-store",
    credentials: "same-origin",
    referrerPolicy: "no-referrer",
    headers
  });
}

async function handleApiFailure(response) {
  if (response.status === 401 || response.status === 403) {
    clearAccessToken();
    renderAuthenticationState();
    throw new Error("Your OIDC token is not authorized for this operation. Sign in with an inventory-admin account.");
  }
  throw new Error(`RemoteHub rejected the request (HTTP ${response.status}).`);
}

function renderInventory(devices) {
  elements.inventoryRows.replaceChildren();
  if (devices.length === 0) {
    appendEmptyRow(elements.inventoryRows, 6, "No inventory mappings are available.");
    return;
  }

  for (const device of devices) {
    const row = document.createElement("tr");
    appendCell(row, device.headscaleNodeId || "—");
    appendCell(row, device.ownerDisplayName || "Not shared");
    appendCell(row, device.coarseLocation || "Not shared");
    appendCell(row, device.verified ? "Yes" : "Pending");
    appendCell(row, String(device.version ?? "—"));
    const actionCell = document.createElement("td");
    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "row-action secondary";
    edit.textContent = "Edit";
    edit.addEventListener("click", () => populateForm(device));
    actionCell.append(edit);
    row.append(actionCell);
    elements.inventoryRows.append(row);
  }
}

function renderAudit(events) {
  elements.auditRows.replaceChildren();
  if (events.length === 0) {
    appendEmptyRow(elements.auditRows, 5, "No inventory audit events are available.");
    return;
  }

  for (const event of events) {
    const row = document.createElement("tr");
    appendCell(row, formatUtc(event.occurredAtUtc));
    appendCell(row, event.eventType || "—");
    appendCell(row, event.headscaleNodeId || "—");
    appendCell(row, event.actorSubject || "—");
    appendCell(row, String(event.version ?? "—"));
    elements.auditRows.append(row);
  }
}

function populateForm(device) {
  elements.nodeId.value = device.headscaleNodeId || "";
  elements.expectedVersion.value = String(device.version ?? 0);
  elements.ownerOptIn.checked = device.ownerDisplayNameOptIn === true;
  elements.ownerName.value = device.ownerDisplayName || "";
  elements.locationOptIn.checked = device.coarseLocationOptIn === true;
  elements.coarseLocation.value = device.coarseLocation || "";
  elements.meshCentralNodeId.value = device.meshCentralNodeId || "";
  elements.verified.checked = device.verified === true;
  const allowed = new Set(Array.isArray(device.allowedCapabilities) ? device.allowedCapabilities : []);
  document.querySelectorAll('input[name="capability"]').forEach((input) => {
    input.checked = allowed.has(input.value);
  });
  syncOptInInputs();
  elements.nodeId.focus();
}

function resetForm() {
  elements.form.reset();
  elements.expectedVersion.value = "0";
  syncOptInInputs();
  elements.nodeId.focus();
}

function syncOptInInputs() {
  elements.ownerName.disabled = !elements.ownerOptIn.checked;
  elements.coarseLocation.disabled = !elements.locationOptIn.checked;
  if (!elements.ownerOptIn.checked) {
    elements.ownerName.value = "";
  }
  if (!elements.locationOptIn.checked) {
    elements.coarseLocation.value = "";
  }
}

function checkedCapabilities() {
  return [...document.querySelectorAll('input[name="capability"]:checked')]
    .map((input) => input.value)
    .filter((value) => CAPABILITIES.includes(value));
}

function renderAuthenticationState() {
  const signedIn = hasUsableAccessToken();
  elements.signIn.hidden = signedIn;
  elements.signOut.hidden = !signedIn;
  elements.adminPanel.hidden = !signedIn;
  elements.sessionState.textContent = signedIn
    ? "OIDC access token is held only in memory for this page."
    : "Not signed in.";
}

function hasUsableAccessToken() {
  return typeof appState.accessToken === "string"
    && appState.accessToken.length > 0
    && Date.now() < appState.accessTokenExpiresAt;
}

function clearAccessToken() {
  appState.accessToken = null;
  appState.accessTokenExpiresAt = 0;
  appState.inventory = [];
}

function readPendingPkce() {
  try {
    const raw = sessionStorage.getItem(PKCE_STORAGE_KEY);
    const pending = raw ? JSON.parse(raw) : null;
    if (!pending || typeof pending.verifier !== "string" || typeof pending.state !== "string"
      || !Number.isFinite(pending.createdAt)) {
      return null;
    }
    return pending;
  } catch {
    return null;
  }
}

function removeAuthorizationQuery() {
  history.replaceState({}, document.title, window.location.pathname);
}

function requireWebCrypto() {
  if (!window.isSecureContext || !window.crypto || !window.crypto.subtle) {
    throw new Error("This Admin page requires a secure browser context for S256 PKCE.");
  }
}

function randomBase64Url(byteLength) {
  const bytes = new Uint8Array(byteLength);
  window.crypto.getRandomValues(bytes);
  return bytesToBase64Url(bytes);
}

async function sha256Base64Url(value) {
  const digest = await window.crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return bytesToBase64Url(new Uint8Array(digest));
}

function bytesToBase64Url(bytes) {
  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}

function requireHttpsUrl(value, label) {
  let url;
  try {
    url = new URL(value);
  } catch {
    throw new Error(`${label} is not a valid absolute URL.`);
  }
  if (url.protocol !== "https:") {
    throw new Error(`${label} must use HTTPS.`);
  }
}

function normalizeIssuer(value) {
  const url = new URL(value);
  return url.href.endsWith("/") ? url.href.slice(0, -1) : url.href;
}

function appendCell(row, value) {
  const cell = document.createElement("td");
  cell.textContent = value;
  row.append(cell);
}

function appendEmptyRow(tableBody, columnCount, message) {
  const row = document.createElement("tr");
  const cell = document.createElement("td");
  cell.colSpan = columnCount;
  cell.textContent = message;
  row.append(cell);
  tableBody.append(row);
}

function formatUtc(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "—" : date.toISOString().replace("T", " ").replace(".000Z", "Z");
}

function setBusy(busy) {
  elements.refresh.disabled = busy;
  elements.save.disabled = busy;
  elements.newMapping.disabled = busy;
}

function showStatus(message, kind) {
  elements.status.textContent = message;
  elements.status.dataset.kind = kind;
}

function toSafeMessage(error) {
  return error instanceof Error && error.message
    ? error.message
    : "An unexpected Admin page error occurred.";
}
