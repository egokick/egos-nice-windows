"use strict";

const NATIVE_HOST_NAME = "com.stayactive.chrome_cursor_input";
const DEBUGGER_PROTOCOL_VERSION = "1.3";
const STATE_POLL_ALARM = "stayactive-state-poll";
const PULSE_ALARM_PREFIX = "stayactive-cursor-pulse";
const PULSE_ALARM_SESSION = Date.now().toString(36);
const STATE_POLL_MINUTES = 0.5;
const PULSE_DELAY_MIN_MS = 20_000;
const PULSE_DELAY_MAX_MS = 35_000;
const PULSE_ALARM_MIN_MS = 30_000;
const LIVE_STATE_POLL_MS = 5_000;
const STATE_STALE_AFTER_MS = 90_000;
const ATTACH_RETRY_DELAY_MS = 5 * 60_000;
const NATIVE_RECONNECT_MIN_MS = 5_000;
const NATIVE_RECONNECT_MAX_MS = 60_000;
const CURSOR_MOVE_DISTANCE_CSS_PX = 64;
const CURSOR_ESTABLISH_DELAY_MS = 150;
const CURSOR_VISIBLE_HOLD_MS = 5_000;
const CURSOR_RETURN_HOLD_MS = 250;
const CURSOR_MARKER_RADIUS_CSS_PX = 14;
const CURSOR_MARKER_COLOR = { r: 255, g: 32, b: 96, a: 0.9 };
const CURSOR_MARKER_OUTLINE_COLOR = { r: 255, g: 255, b: 255, a: 1 };
const PULSE_KEY = "f";
const PULSE_KEY_CODE = "KeyF";
const PULSE_KEY_WINDOWS_VIRTUAL_CODE = 70;

const ELIGIBLE_HOSTS = new Set([
  "windows.cloud.microsoft",
  "windows365.microsoft.com",
  "rdweb.wvd.microsoft.com",
  "client.wvd.microsoft.com"
]);

const STATUS_VALUES = new Set([
  "disabled",
  "waiting",
  "attached",
  "pulse_skipped",
  "pulsed",
  "detached",
  "error"
]);

const STATUS_DETAIL_VALUES = new Set([
  "native_state_disabled",
  "no_eligible_tab",
  "multiple_eligible_tabs",
  "tab_navigating",
  "target_foreground",
  "target_foreground_visualized",
  "target_foreground_marker_unavailable",
  "visual_marker_shown",
  "visual_marker_unavailable",
  "user_cancelled",
  "debugger_replaced",
  "debugger_detached",
  "native_host_unavailable",
  "state_timeout",
  "target_changed",
  "navigation",
  "tab_closed",
  "attach_failed",
  "evaluation_failed",
  "dispatch_failed",
  "scheduled_pulse",
  "background_pulse",
  "invalid_state_message"
]);

const EXTENSION_VERSION = chrome.runtime.getManifest().version;

let nativePort = null;
let nativeReconnectTimer = null;
let liveStatePollTimer = null;
let nativeReconnectDelayMs = NATIVE_RECONNECT_MIN_MS;
let nativeStateTrusted = false;
let nativeEnabled = false;
let lastNativeStateAt = 0;
let pulseTimer = null;
let pulseScheduleGeneration = 0;
let pulseScheduleActive = false;
let pulseScheduleClaimed = false;
let pulseAlarmName = null;
let pulseAlarmScheduledTime = 0;
let pendingPulseAlarmWake = false;

let attachedTabId = null;
let attachingTabId = null;
let attachRetryAfter = 0;
let userReattachBlocked = false;
let expectedDetachReasons = new Map();
let navigatingTabs = new Set();
let lastReportedStatus = "";
let workQueue = Promise.resolve();

const FIND_INPUT_SURFACE_EXPRESSION = `(() => {
  const eligibleHosts = new Set([
    "windows.cloud.microsoft",
    "windows365.microsoft.com",
    "rdweb.wvd.microsoft.com",
    "client.wvd.microsoft.com"
  ]);
  const currentUrl = new URL(location.href);
  const exactEligibleHost =
    currentUrl.protocol === "https:" &&
    currentUrl.port === "" &&
    currentUrl.username === "" &&
    currentUrl.password === "" &&
    eligibleHosts.has(currentUrl.hostname);

  if (!exactEligibleHost) {
    return { eligible: false };
  }

  const viewportWidth = Math.max(0, window.innerWidth || document.documentElement?.clientWidth || 0);
  const viewportHeight = Math.max(0, window.innerHeight || document.documentElement?.clientHeight || 0);
  const roots = [document];
  const visitedRoots = new Set();
  let best = null;

  const consider = (element) => {
    let style;
    let rect;
    try {
      style = window.getComputedStyle(element);
      rect = element.getBoundingClientRect();
    } catch {
      return;
    }

    if (
      style.display === "none" ||
      style.visibility === "hidden" ||
      style.visibility === "collapse" ||
      Number.parseFloat(style.opacity || "1") <= 0 ||
      rect.width < 2 ||
      rect.height < 2
    ) {
      return;
    }

    const left = Math.max(0, rect.left);
    const top = Math.max(0, rect.top);
    const right = Math.min(viewportWidth, rect.right);
    const bottom = Math.min(viewportHeight, rect.bottom);
    const width = Math.max(0, right - left);
    const height = Math.max(0, bottom - top);
    const area = width * height;

    if (width < 2 || height < 2 || area <= 0) {
      return;
    }

    if (!best || area > best.area) {
      best = { left, top, right, bottom, area };
    }
  };

  while (roots.length > 0) {
    const root = roots.pop();
    if (!root || visitedRoots.has(root)) {
      continue;
    }
    visitedRoots.add(root);

    let surfaces = [];
    let descendants = [];
    try {
      surfaces = root.querySelectorAll("canvas, iframe, [role='application']");
      descendants = root.querySelectorAll("*");
    } catch {
      continue;
    }

    for (const surface of surfaces) {
      consider(surface);
    }

    for (const element of descendants) {
      if (element.shadowRoot && element.shadowRoot.mode === "open") {
        roots.push(element.shadowRoot);
      }
    }
  }

  const fallback = {
    left: 0,
    top: 0,
    right: viewportWidth,
    bottom: viewportHeight
  };
  const target = best || fallback;
  const lastViewportX = Math.max(0, viewportWidth - 1);
  const lastViewportY = Math.max(0, viewportHeight - 1);
  const surfaceLeft = Math.max(0, Math.min(lastViewportX, Math.ceil(target.left)));
  const surfaceTop = Math.max(0, Math.min(lastViewportY, Math.ceil(target.top)));
  const surfaceRight = Math.max(
    surfaceLeft,
    Math.min(lastViewportX, Math.floor(target.right - 0.001))
  );
  const surfaceBottom = Math.max(
    surfaceTop,
    Math.min(lastViewportY, Math.floor(target.bottom - 0.001))
  );
  const horizontalRoom = surfaceRight - surfaceLeft;
  const verticalRoom = surfaceBottom - surfaceTop;
  const movementDistance = Math.min(
    ${CURSOR_MOVE_DISTANCE_CSS_PX},
    Math.max(horizontalRoom, verticalRoom)
  );

  let x = Math.floor((surfaceLeft + surfaceRight) / 2);
  let y = Math.floor((surfaceTop + surfaceBottom) / 2);
  let movedX = x;
  let movedY = y;

  if (horizontalRoom >= verticalRoom && movementDistance > 0) {
    x = Math.floor((surfaceLeft + surfaceRight - movementDistance) / 2);
    movedX = x + movementDistance;
  } else if (movementDistance > 0) {
    y = Math.floor((surfaceTop + surfaceBottom - movementDistance) / 2);
    movedY = y + movementDistance;
  }

  return {
    eligible: true,
    x,
    y,
    movedX,
    movedY,
    surfaceLeft,
    surfaceTop,
    surfaceRight,
    surfaceBottom,
    viewportWidth,
    viewportHeight
  };
})()`;

function enqueue(work) {
  const run = workQueue.then(work, work);
  workQueue = run.catch(() => {});
  return run;
}

function runtimeError() {
  const error = chrome.runtime.lastError;
  return error ? new Error(error.message || String(error)) : null;
}

function queryTabs(queryInfo) {
  return new Promise((resolve, reject) => {
    chrome.tabs.query(queryInfo, (tabs) => {
      const error = runtimeError();
      if (error) {
        reject(error);
      } else {
        resolve(tabs);
      }
    });
  });
}

function getTab(tabId) {
  return new Promise((resolve, reject) => {
    chrome.tabs.get(tabId, (tab) => {
      const error = runtimeError();
      if (error) {
        reject(error);
      } else {
        resolve(tab);
      }
    });
  });
}

function getWindow(windowId) {
  return new Promise((resolve, reject) => {
    chrome.windows.get(windowId, (window) => {
      const error = runtimeError();
      if (error) {
        reject(error);
      } else {
        resolve(window);
      }
    });
  });
}

function attachDebugger(tabId) {
  return new Promise((resolve, reject) => {
    chrome.debugger.attach({ tabId }, DEBUGGER_PROTOCOL_VERSION, () => {
      const error = runtimeError();
      if (error) {
        reject(error);
      } else {
        resolve();
      }
    });
  });
}

function detachDebugger(tabId) {
  return new Promise((resolve, reject) => {
    chrome.debugger.detach({ tabId }, () => {
      const error = runtimeError();
      if (error) {
        reject(error);
      } else {
        resolve();
      }
    });
  });
}

function sendDebuggerCommand(tabId, method, commandParams = {}) {
  return new Promise((resolve, reject) => {
    chrome.debugger.sendCommand({ tabId }, method, commandParams, (result) => {
      const error = runtimeError();
      if (error) {
        reject(error);
      } else {
        resolve(result);
      }
    });
  });
}

function isEligibleTab(tab) {
  if (!tab || typeof tab.url !== "string") {
    return false;
  }

  try {
    const parsed = new URL(tab.url);
    return (
      parsed.protocol === "https:" &&
      parsed.port === "" &&
      parsed.username === "" &&
      parsed.password === "" &&
      ELIGIBLE_HOSTS.has(parsed.hostname)
    );
  } catch {
    return false;
  }
}

function isFeatureEnabled() {
  return nativeStateTrusted && nativeEnabled;
}

function sendNative(message) {
  if (!nativePort) {
    return false;
  }

  try {
    nativePort.postMessage(message);
    return true;
  } catch {
    return false;
  }
}

function requestNativeState() {
  if (!nativePort) {
    connectNative();
    return;
  }

  sendNative({
    type: "getState",
    extensionVersion: EXTENSION_VERSION
  });
}

function reportStatus(status, detail) {
  if (!STATUS_VALUES.has(status) || !STATUS_DETAIL_VALUES.has(detail)) {
    return;
  }

  const statusKey = `${status}:${detail}`;
  if (status !== "pulsed" && statusKey === lastReportedStatus) {
    return;
  }

  if (sendNative({
    type: "status",
    status,
    detail,
    extensionVersion: EXTENSION_VERSION
  })) {
    lastReportedStatus = statusKey;
  }
}

function scheduleNativeReconnect() {
  if (nativeReconnectTimer !== null) {
    return;
  }

  nativeReconnectTimer = setTimeout(() => {
    nativeReconnectTimer = null;
    connectNative();
  }, nativeReconnectDelayMs);
  nativeReconnectDelayMs = Math.min(
    NATIVE_RECONNECT_MAX_MS,
    nativeReconnectDelayMs * 2
  );
}

function nextPulseDelayMs() {
  const randomUnit = Math.max(0, Math.min(1, Number(Math.random()) || 0));
  const inclusiveRange = PULSE_DELAY_MAX_MS - PULSE_DELAY_MIN_MS + 1;
  return (
    PULSE_DELAY_MIN_MS +
    Math.min(inclusiveRange - 1, Math.floor(randomUnit * inclusiveRange))
  );
}

function clearPulseSchedule() {
  const alarmName = pulseAlarmName;
  pulseScheduleGeneration += 1;
  pulseScheduleActive = false;
  pulseScheduleClaimed = false;
  pulseAlarmName = null;
  pulseAlarmScheduledTime = 0;

  if (pulseTimer !== null) {
    clearTimeout(pulseTimer);
    pulseTimer = null;
  }

  if (alarmName !== null) {
    // Every generation has a distinct alarm name, so an asynchronous clear can
    // never remove a newly scheduled fallback after a rapid off/on toggle.
    chrome.alarms.clear(alarmName);
  }
}

function scheduleNextPulse() {
  if (!isFeatureEnabled()) {
    clearPulseSchedule();
    return;
  }

  if (pulseTimer !== null) {
    clearTimeout(pulseTimer);
    pulseTimer = null;
  }

  const generation = pulseScheduleGeneration + 1;
  const precedingAlarmName = pulseAlarmName;
  pulseScheduleGeneration = generation;
  pulseScheduleActive = true;
  pulseScheduleClaimed = false;
  pulseAlarmName =
    `${PULSE_ALARM_PREFIX}-${PULSE_ALARM_SESSION}-${generation}`;

  const delayMs = nextPulseDelayMs();
  pulseAlarmScheduledTime =
    Date.now() + Math.max(delayMs, PULSE_ALARM_MIN_MS);

  pulseTimer = setTimeout(() => {
    claimScheduledPulse(generation);
  }, delayMs);

  // Chrome 120+ can defer packaged-extension alarms below 30 seconds. The
  // in-worker timer provides the requested 20-35 second cadence while the
  // native messaging/debugger connection keeps this worker alive. This
  // one-shot alarm is a durable fallback and is always due within 30-35
  // seconds. Per-generation names make delayed alarm events and asynchronous
  // cleanup unable to claim or cancel a newer cycle.
  chrome.alarms.create(pulseAlarmName, {
    when: pulseAlarmScheduledTime
  });

  if (precedingAlarmName !== null) {
    chrome.alarms.clear(precedingAlarmName);
  }
}

function claimScheduledPulse(generation) {
  if (
    !isFeatureEnabled() ||
    !pulseScheduleActive ||
    pulseScheduleClaimed ||
    generation !== pulseScheduleGeneration
  ) {
    return;
  }

  pulseScheduleClaimed = true;
  if (pulseTimer !== null) {
    clearTimeout(pulseTimer);
    pulseTimer = null;
  }

  enqueue(async () => {
    if (
      !isFeatureEnabled() ||
      !pulseScheduleActive ||
      !pulseScheduleClaimed ||
      generation !== pulseScheduleGeneration
    ) {
      return;
    }

    // Establish the next start time before awaiting the marker animation.
    // workQueue serializes all pulses, so input can never overlap.
    scheduleNextPulse();
    await runScheduledPulse();
  });
}

function startLiveStatePolling() {
  if (liveStatePollTimer !== null) {
    return;
  }

  // A native messaging port keeps a Manifest V3 worker alive in supported
  // Chrome versions. The alarm remains as a durable fallback, while this short
  // poll makes a StayActive menu toggle take effect promptly.
  liveStatePollTimer = setInterval(requestNativeState, LIVE_STATE_POLL_MS);
}

function stopLiveStatePolling() {
  if (liveStatePollTimer === null) {
    return;
  }

  clearInterval(liveStatePollTimer);
  liveStatePollTimer = null;
}

function connectNative() {
  if (nativePort) {
    return;
  }

  let port;
  try {
    port = chrome.runtime.connectNative(NATIVE_HOST_NAME);
  } catch {
    nativeStateTrusted = false;
    clearPulseSchedule();
    enqueue(() => detachCurrentTab("native_host_unavailable"));
    scheduleNativeReconnect();
    return;
  }

  nativePort = port;
  startLiveStatePolling();

  port.onMessage.addListener((message) => {
    if (
      !message ||
      message.type !== "state" ||
      typeof message.enabled !== "boolean"
    ) {
      reportStatus("error", "invalid_state_message");
      return;
    }

    const wasEnabled = nativeEnabled;
    nativeEnabled = message.enabled;
    nativeStateTrusted = true;
    lastNativeStateAt = Date.now();
    nativeReconnectDelayMs = NATIVE_RECONNECT_MIN_MS;

    if (!nativeEnabled) {
      pendingPulseAlarmWake = false;
      userReattachBlocked = false;
      attachRetryAfter = 0;
      clearPulseSchedule();
      enqueue(async () => {
        await detachCurrentTab("native_state_disabled");
        reportStatus("disabled", "native_state_disabled");
      });
      return;
    }

    if (!wasEnabled) {
      userReattachBlocked = false;
      attachRetryAfter = 0;
    }

    if (!wasEnabled || !pulseScheduleActive) {
      clearPulseSchedule();
      scheduleNextPulse();
    }

    const recoveringDurablePulse = pendingPulseAlarmWake;
    pendingPulseAlarmWake = false;
    enqueue(async () => {
      await reconcileTarget(!recoveringDurablePulse);
      if (recoveringDurablePulse) {
        await sendCursorPulse("scheduled_pulse");
      }
    });
  });

  port.onDisconnect.addListener(() => {
    if (nativePort !== port) {
      return;
    }

    // Reading lastError prevents an unchecked runtime error in Chrome. Its text
    // is intentionally never forwarded to the native application.
    void chrome.runtime.lastError;
    nativePort = null;
    stopLiveStatePolling();
    nativeStateTrusted = false;
    clearPulseSchedule();
    lastReportedStatus = "";
    enqueue(() => detachCurrentTab("native_host_unavailable"));
    scheduleNativeReconnect();
  });

  requestNativeState();
}

async function detachCurrentTab(detail) {
  const tabId = attachedTabId;
  if (tabId === null) {
    return;
  }

  expectedDetachReasons.set(tabId, detail);
  try {
    await hideCursorMarker(tabId);
    await detachDebugger(tabId);
  } catch {
    // The target may already be gone or detached. Clearing our local state is
    // still the fail-closed result.
    expectedDetachReasons.delete(tabId);
  } finally {
    if (attachedTabId === tabId) {
      attachedTabId = null;
    }
    reportStatus("detached", detail);
  }
}

async function findSingleEligibleTab() {
  const tabs = await queryTabs({});
  const eligibleTabs = tabs.filter(isEligibleTab);

  if (eligibleTabs.length === 0) {
    return { tab: null, detail: "no_eligible_tab" };
  }

  if (eligibleTabs.length !== 1) {
    return { tab: null, detail: "multiple_eligible_tabs" };
  }

  const tab = eligibleTabs[0];
  if (tab.status !== "complete" || navigatingTabs.has(tab.id)) {
    return { tab: null, detail: "tab_navigating" };
  }

  return { tab, detail: null };
}

async function reconcileTarget(pulseAfterAttach) {
  if (!isFeatureEnabled()) {
    await detachCurrentTab(
      nativeEnabled ? "native_host_unavailable" : "native_state_disabled"
    );
    return;
  }

  if (userReattachBlocked) {
    reportStatus("waiting", "user_cancelled");
    return;
  }

  if (Date.now() < attachRetryAfter) {
    reportStatus("waiting", "attach_failed");
    return;
  }

  let selection;
  try {
    selection = await findSingleEligibleTab();
  } catch {
    await detachCurrentTab("target_changed");
    reportStatus("error", "attach_failed");
    return;
  }

  if (!selection.tab) {
    await detachCurrentTab(selection.detail);
    reportStatus("waiting", selection.detail);
    return;
  }

  const targetTab = selection.tab;
  if (attachedTabId === targetTab.id) {
    return;
  }

  if (attachedTabId !== null) {
    await detachCurrentTab("target_changed");
  }

  if (!isFeatureEnabled() || userReattachBlocked) {
    return;
  }

  attachingTabId = targetTab.id;
  try {
    await attachDebugger(targetTab.id);
  } catch {
    attachRetryAfter = Date.now() + ATTACH_RETRY_DELAY_MS;
    reportStatus("error", "attach_failed");
    return;
  } finally {
    attachingTabId = null;
  }

  attachedTabId = targetTab.id;
  reportStatus("attached", "background_pulse");

  if (!isFeatureEnabled() || userReattachBlocked) {
    await detachCurrentTab("native_state_disabled");
    return;
  }

  if (pulseAfterAttach) {
    await sendCursorPulse("background_pulse");
  }
}

async function getLiveInputState(tabId) {
  if (
    !isFeatureEnabled() ||
    attachedTabId !== tabId ||
    navigatingTabs.has(tabId)
  ) {
    return { available: false, foreground: true };
  }

  let currentTab;
  try {
    currentTab = await getTab(tabId);
  } catch {
    return { available: false, foreground: true };
  }

  if (
    !isFeatureEnabled() ||
    attachedTabId !== tabId ||
    !currentTab ||
    currentTab.id !== tabId ||
    currentTab.status !== "complete" ||
    !isEligibleTab(currentTab) ||
    navigatingTabs.has(tabId)
  ) {
    return { available: false, foreground: true };
  }

  if (!currentTab.active) {
    return { available: true, foreground: false };
  }

  if (typeof currentTab.windowId !== "number") {
    return { available: false, foreground: true };
  }

  try {
    const window = await getWindow(currentTab.windowId);
    if (
      !isFeatureEnabled() ||
      attachedTabId !== tabId ||
      navigatingTabs.has(tabId)
    ) {
      return { available: false, foreground: true };
    }
    return {
      available: true,
      foreground: Boolean(window.focused)
    };
  } catch {
    return { available: false, foreground: true };
  }
}

function validInputPoint(value) {
  if (
    !value ||
    value.eligible !== true ||
    !Number.isFinite(value.x) ||
    !Number.isFinite(value.y) ||
    !Number.isFinite(value.movedX) ||
    !Number.isFinite(value.movedY) ||
    !Number.isFinite(value.surfaceLeft) ||
    !Number.isFinite(value.surfaceTop) ||
    !Number.isFinite(value.surfaceRight) ||
    !Number.isFinite(value.surfaceBottom) ||
    !Number.isFinite(value.viewportWidth) ||
    !Number.isFinite(value.viewportHeight) ||
    value.viewportWidth <= 0 ||
    value.viewportHeight <= 0
  ) {
    return false;
  }

  const pointsAreInsideSurface =
    value.surfaceLeft >= 0 &&
    value.surfaceTop >= 0 &&
    value.surfaceRight < value.viewportWidth &&
    value.surfaceBottom < value.viewportHeight &&
    value.surfaceLeft <= value.surfaceRight &&
    value.surfaceTop <= value.surfaceBottom &&
    value.x >= value.surfaceLeft &&
    value.x <= value.surfaceRight &&
    value.y >= value.surfaceTop &&
    value.y <= value.surfaceBottom &&
    value.movedX >= value.surfaceLeft &&
    value.movedX <= value.surfaceRight &&
    value.movedY >= value.surfaceTop &&
    value.movedY <= value.surfaceBottom;

  const movementDistance = Math.hypot(
    value.movedX - value.x,
    value.movedY - value.y
  );

  return (
    pointsAreInsideSurface &&
    movementDistance > 0 &&
    movementDistance <= CURSOR_MOVE_DISTANCE_CSS_PX
  );
}

function waitForDelay(delayMs) {
  return new Promise((resolve) => setTimeout(resolve, delayMs));
}

async function dispatchPulseKey(tabId) {
  const baseKeyEvent = {
    modifiers: 0,
    key: PULSE_KEY,
    code: PULSE_KEY_CODE,
    windowsVirtualKeyCode: PULSE_KEY_WINDOWS_VIRTUAL_CODE,
    location: 0,
    autoRepeat: false,
    isKeypad: false,
    isSystemKey: false
  };
  const keyUpEvent = {
    ...baseKeyEvent,
    type: "keyUp"
  };

  try {
    await sendDebuggerCommand(tabId, "Input.dispatchKeyEvent", {
      ...baseKeyEvent,
      type: "keyDown",
      text: PULSE_KEY,
      unmodifiedText: PULSE_KEY
    });
  } finally {
    // Always release the key, including when Chrome reports a failed key-down,
    // and retry one failed release so a transient protocol error is less
    // likely to leave the remote key held.
    try {
      await sendDebuggerCommand(tabId, "Input.dispatchKeyEvent", keyUpEvent);
    } catch (keyUpError) {
      try {
        await sendDebuggerCommand(tabId, "Input.dispatchKeyEvent", keyUpEvent);
      } catch {
        // Surface the first release failure after the best-effort retry.
      }
      throw keyUpError;
    }
  }
}

function markerQuadAtPoint(point) {
  const radius = CURSOR_MARKER_RADIUS_CSS_PX;
  const maxX = Math.max(0, point.viewportWidth - 1);
  const maxY = Math.max(0, point.viewportHeight - 1);
  const clampX = (value) => Math.max(0, Math.min(maxX, value));
  const clampY = (value) => Math.max(0, Math.min(maxY, value));
  return [
    clampX(point.x),
    clampY(point.y - radius),
    clampX(point.x + radius),
    clampY(point.y),
    clampX(point.x),
    clampY(point.y + radius),
    clampX(point.x - radius),
    clampY(point.y)
  ];
}

function markerRectAtPoint(point) {
  const radius = CURSOR_MARKER_RADIUS_CSS_PX;
  const maxX = Math.max(0, Math.floor(point.viewportWidth - 1));
  const maxY = Math.max(0, Math.floor(point.viewportHeight - 1));
  const left = Math.max(0, Math.min(maxX, Math.floor(point.x - radius)));
  const top = Math.max(0, Math.min(maxY, Math.floor(point.y - radius)));
  const right = Math.max(left, Math.min(maxX, Math.ceil(point.x + radius)));
  const bottom = Math.max(top, Math.min(maxY, Math.ceil(point.y + radius)));
  return {
    x: left,
    y: top,
    width: Math.max(1, right - left),
    height: Math.max(1, bottom - top)
  };
}

async function initializeCursorMarker(tabId) {
  try {
    // Overlay commands require the DOM agent to be initialized first in some
    // Chrome builds. Enabling the protocol domain does not inject or modify
    // page DOM and the compositor highlight cannot receive input.
    await sendDebuggerCommand(tabId, "DOM.enable");
  } catch {
    return { domEnabled: false, overlayEnabled: false };
  }

  try {
    await sendDebuggerCommand(tabId, "Overlay.enable");
    return { domEnabled: true, overlayEnabled: true };
  } catch {
    // DOM.highlightRect remains available as a fallback without Overlay.
    return { domEnabled: true, overlayEnabled: false };
  }
}

async function showCursorMarker(tabId, point, markerDomains) {
  if (!markerDomains.domEnabled) {
    return false;
  }

  if (markerDomains.overlayEnabled) {
    try {
      await sendDebuggerCommand(tabId, "Overlay.highlightQuad", {
        quad: markerQuadAtPoint(point),
        color: CURSOR_MARKER_COLOR,
        outlineColor: CURSOR_MARKER_OUTLINE_COLOR
      });
      return true;
    } catch {
      // Older Chrome builds can reject Overlay.highlightQuad even after the
      // domains are enabled. Fall back to the legacy DOM-domain rectangle.
    }
  }

  try {
    await sendDebuggerCommand(tabId, "DOM.highlightRect", {
      ...markerRectAtPoint(point),
      color: CURSOR_MARKER_COLOR,
      outlineColor: CURSOR_MARKER_OUTLINE_COLOR
    });
    return true;
  } catch {
    return false;
  }
}

async function hideCursorMarker(tabId) {
  try {
    await sendDebuggerCommand(tabId, "Overlay.hideHighlight");
  } catch {
    // Best-effort cleanup continues through both protocol domains.
  }

  try {
    await sendDebuggerCommand(tabId, "DOM.hideHighlight");
  } catch {
    // Best-effort cleanup continues through both protocol domains.
  }

  try {
    await sendDebuggerCommand(tabId, "Overlay.disable");
  } catch {
    // Best-effort cleanup continues with disabling the DOM domain.
  }

  try {
    await sendDebuggerCommand(tabId, "DOM.disable");
  } catch {
    // Detaching the debugger is the final cleanup fallback.
  }
}

async function animateCursorMarker(tabId, point) {
  let markerShown = true;
  let markerDomains = { domEnabled: false, overlayEnabled: false };

  try {
    markerDomains = await initializeCursorMarker(tabId);
    markerShown =
      (await showCursorMarker(
        tabId,
        {
          x: point.x,
          y: point.y,
          viewportWidth: point.viewportWidth,
          viewportHeight: point.viewportHeight
        },
        markerDomains
      )) && markerShown;
    await waitForDelay(CURSOR_ESTABLISH_DELAY_MS);
    markerShown =
      (await showCursorMarker(
        tabId,
        {
          x: point.movedX,
          y: point.movedY,
          viewportWidth: point.viewportWidth,
          viewportHeight: point.viewportHeight
        },
        markerDomains
      )) && markerShown;
    await waitForDelay(CURSOR_VISIBLE_HOLD_MS);
    markerShown =
      (await showCursorMarker(
        tabId,
        {
          x: point.x,
          y: point.y,
          viewportWidth: point.viewportWidth,
          viewportHeight: point.viewportHeight
        },
        markerDomains
      )) && markerShown;
    await waitForDelay(CURSOR_RETURN_HOLD_MS);
  } finally {
    await hideCursorMarker(tabId);
  }

  return markerShown;
}

async function sendCursorPulse(detail) {
  void detail;
  const tabId = attachedTabId;
  if (!isFeatureEnabled() || tabId === null) {
    return;
  }

  let selection;
  try {
    selection = await findSingleEligibleTab();
  } catch {
    await detachCurrentTab("target_changed");
    return;
  }

  if (!selection.tab || selection.tab.id !== tabId) {
    await detachCurrentTab(selection.detail || "target_changed");
    reportStatus("waiting", selection.detail || "target_changed");
    return;
  }

  const stateBeforeEvaluation = await getLiveInputState(tabId);
  if (!stateBeforeEvaluation.available) {
    return;
  }
  const foregroundBeforeEvaluation = stateBeforeEvaluation.foreground;

  let evaluation;
  try {
    evaluation = await sendDebuggerCommand(tabId, "Runtime.evaluate", {
      expression: FIND_INPUT_SURFACE_EXPRESSION,
      returnByValue: true,
      silent: true,
      userGesture: false
    });
  } catch {
    reportStatus("error", "evaluation_failed");
    return;
  }

  if (
    !evaluation ||
    evaluation.exceptionDetails ||
    !evaluation.result ||
    !validInputPoint(evaluation.result.value)
  ) {
    reportStatus("error", "evaluation_failed");
    return;
  }

  const point = evaluation.result.value;

  const stateAfterEvaluation = await getLiveInputState(tabId);
  if (!stateAfterEvaluation.available) {
    return;
  }

  if (foregroundBeforeEvaluation || stateAfterEvaluation.foreground) {
    const markerShown = await animateCursorMarker(tabId, point);
    reportStatus(
      "pulse_skipped",
      markerShown
        ? "target_foreground_visualized"
        : "target_foreground_marker_unavailable"
    );
    return;
  }

  let markerShown = true;
  let foregroundTransitioned = false;
  let markerDomains = { domEnabled: false, overlayEnabled: false };
  try {
    markerDomains = await initializeCursorMarker(tabId);
    const baseEvent = {
      type: "mouseMoved",
      button: "none",
      buttons: 0,
      pointerType: "mouse"
    };

    // Establish a bounded point, pause briefly, move far enough for the remote
    // cursor to be visible, send one complete lowercase "f" keypress, hold the
    // marker there, then return. A compositor-level diamond follows the same
    // three positions without touching the page DOM or accepting any input.
    markerShown =
      (await showCursorMarker(
        tabId,
        {
          x: point.x,
          y: point.y,
          viewportWidth: point.viewportWidth,
          viewportHeight: point.viewportHeight
        },
        markerDomains
      )) && markerShown;
    const initialInputState = await getLiveInputState(tabId);
    if (!initialInputState.available) {
      return;
    }
    if (initialInputState.foreground) {
      foregroundTransitioned = true;
    } else {
      await sendDebuggerCommand(tabId, "Input.dispatchMouseEvent", {
        ...baseEvent,
        x: point.x,
        y: point.y
      });
    }
    await waitForDelay(CURSOR_ESTABLISH_DELAY_MS);
    markerShown =
      (await showCursorMarker(
        tabId,
        {
          x: point.movedX,
          y: point.movedY,
          viewportWidth: point.viewportWidth,
          viewportHeight: point.viewportHeight
        },
        markerDomains
      )) && markerShown;
    const movedInputState = await getLiveInputState(tabId);
    if (!movedInputState.available) {
      return;
    }
    if (foregroundTransitioned || movedInputState.foreground) {
      foregroundTransitioned = true;
    } else {
      await sendDebuggerCommand(tabId, "Input.dispatchMouseEvent", {
        ...baseEvent,
        x: point.movedX,
        y: point.movedY
      });
      const keyInputState = await getLiveInputState(tabId);
      if (!keyInputState.available) {
        return;
      }
      if (keyInputState.foreground) {
        foregroundTransitioned = true;
      } else {
        await dispatchPulseKey(tabId);
      }
    }
    await waitForDelay(CURSOR_VISIBLE_HOLD_MS);
    markerShown =
      (await showCursorMarker(
        tabId,
        {
          x: point.x,
          y: point.y,
          viewportWidth: point.viewportWidth,
          viewportHeight: point.viewportHeight
        },
        markerDomains
      )) && markerShown;
    const returnInputState = await getLiveInputState(tabId);
    if (!returnInputState.available) {
      return;
    }
    if (foregroundTransitioned || returnInputState.foreground) {
      foregroundTransitioned = true;
    } else {
      await sendDebuggerCommand(tabId, "Input.dispatchMouseEvent", {
        ...baseEvent,
        x: point.x,
        y: point.y
      });
    }
    await waitForDelay(CURSOR_RETURN_HOLD_MS);
  } catch {
    reportStatus("error", "dispatch_failed");
    return;
  } finally {
    await hideCursorMarker(tabId);
  }

  if (foregroundTransitioned) {
    reportStatus(
      "pulse_skipped",
      markerShown
        ? "target_foreground_visualized"
        : "target_foreground_marker_unavailable"
    );
    return;
  }

  reportStatus(
    "pulsed",
    markerShown ? "visual_marker_shown" : "visual_marker_unavailable"
  );
}

async function runScheduledPulse() {
  if (!isFeatureEnabled()) {
    return;
  }

  await reconcileTarget(false);
  await sendCursorPulse("scheduled_pulse");
}

function createAlarms() {
  chrome.alarms.create(STATE_POLL_ALARM, {
    delayInMinutes: STATE_POLL_MINUTES,
    periodInMinutes: STATE_POLL_MINUTES
  });
}

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === STATE_POLL_ALARM) {
    if (
      nativeStateTrusted &&
      lastNativeStateAt > 0 &&
      Date.now() - lastNativeStateAt > STATE_STALE_AFTER_MS
    ) {
      nativeStateTrusted = false;
      clearPulseSchedule();
      enqueue(async () => {
        await detachCurrentTab("state_timeout");
        reportStatus("error", "state_timeout");
      });
    }

    requestNativeState();
    return;
  }

  const pulseAlarmPrefix = `${PULSE_ALARM_PREFIX}-`;
  const currentSessionAlarmPrefix =
    `${pulseAlarmPrefix}${PULSE_ALARM_SESSION}-`;
  if (
    typeof alarm.name === "string" &&
    alarm.name.startsWith(pulseAlarmPrefix) &&
    !nativeStateTrusted
  ) {
    // An alarm can be the event that starts a fresh service worker. Preserve
    // that durable wake until the native host confirms whether the feature is
    // still enabled; the confirmed path then performs exactly one pulse. A
    // non-current generation from this same worker session is only a delayed
    // event from a retired schedule and must remain ignored.
    if (!alarm.name.startsWith(currentSessionAlarmPrefix)) {
      pendingPulseAlarmWake = true;
      requestNativeState();
    }
    return;
  }

  if (alarm.name === pulseAlarmName) {
    if (
      pulseScheduleActive &&
      Number.isFinite(alarm.scheduledTime) &&
      alarm.scheduledTime === pulseAlarmScheduledTime
    ) {
      claimScheduledPulse(pulseScheduleGeneration);
    }
  }
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  const startedLoading = changeInfo.status === "loading";
  const changedUrl = typeof changeInfo.url === "string";

  if (startedLoading) {
    navigatingTabs.add(tabId);
  }

  if (startedLoading || changedUrl) {
    if (tabId === attachedTabId) {
      enqueue(() => detachCurrentTab("navigation"));
    }
  }

  // History API / fragment navigations can update the URL without producing a
  // later "complete" event. The URL in this event is already the new URL, so a
  // fresh exact-host selection can run immediately after detaching.
  if (changedUrl && !startedLoading) {
    navigatingTabs.delete(tabId);
    if (isFeatureEnabled()) {
      enqueue(() => reconcileTarget(true));
    }
  }

  if (changeInfo.status === "complete") {
    navigatingTabs.delete(tabId);
    if (isFeatureEnabled()) {
      enqueue(() => reconcileTarget(true));
    }
  }
});

chrome.tabs.onRemoved.addListener((tabId) => {
  navigatingTabs.delete(tabId);
  if (tabId === attachedTabId || tabId === attachingTabId) {
    attachedTabId = null;
    attachingTabId = null;
    reportStatus("detached", "tab_closed");
    if (isFeatureEnabled()) {
      enqueue(() => reconcileTarget(true));
    }
  }
});

chrome.debugger.onDetach.addListener((source, reason) => {
  const tabId = source.tabId;
  if (typeof tabId !== "number") {
    return;
  }

  const expectedReason = expectedDetachReasons.get(tabId) || null;
  expectedDetachReasons.delete(tabId);

  enqueue(async () => {
    if (attachedTabId === tabId) {
      attachedTabId = null;
    }
    if (attachingTabId === tabId) {
      attachingTabId = null;
    }

    if (expectedReason) {
      return;
    }

    if (reason === "canceled_by_user") {
      userReattachBlocked = true;
      reportStatus("detached", "user_cancelled");
      return;
    }

    if (reason === "replaced_with_devtools") {
      userReattachBlocked = true;
      reportStatus("detached", "debugger_replaced");
      return;
    }

    attachRetryAfter = Date.now() + ATTACH_RETRY_DELAY_MS;
    reportStatus("detached", "debugger_detached");
  });
});

chrome.runtime.onInstalled.addListener(() => {
  createAlarms();
  connectNative();
});

chrome.runtime.onStartup.addListener(() => {
  createAlarms();
  connectNative();
});

createAlarms();
connectNative();
