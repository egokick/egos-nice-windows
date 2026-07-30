"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const backgroundSource = fs.readFileSync(
  path.join(__dirname, "background.js"),
  "utf8"
);

class MockEvent {
  constructor() {
    this.listeners = [];
  }

  addListener(listener) {
    this.listeners.push(listener);
  }

  emit(...args) {
    for (const listener of this.listeners) {
      listener(...args);
    }
  }
}

class FakeTimers {
  constructor() {
    this.now = 0;
    this.nextId = 1;
    this.tasks = new Map();
  }

  setTimeout(callback, delayMs = 0) {
    const id = this.nextId;
    this.nextId += 1;
    this.tasks.set(id, {
      callback,
      dueAt: this.now + Math.max(0, Number(delayMs) || 0)
    });
    return id;
  }

  clearTimeout(id) {
    this.tasks.delete(id);
  }

  advanceBy(delayMs) {
    const targetTime = this.now + delayMs;

    while (true) {
      let nextId = null;
      let nextTask = null;
      for (const [id, task] of this.tasks) {
        if (
          task.dueAt <= targetTime &&
          (!nextTask || task.dueAt < nextTask.dueAt)
        ) {
          nextId = id;
          nextTask = task;
        }
      }

      if (!nextTask) {
        break;
      }

      this.tasks.delete(nextId);
      this.now = nextTask.dueAt;
      nextTask.callback();
    }

    this.now = targetTime;
  }
}

function createHarness(
  initialTabs,
  windowFocused = false,
  evaluationValue = {
    eligible: true,
    x: 384,
    y: 300,
    movedX: 448,
    movedY: 300,
    surfaceLeft: 0,
    surfaceTop: 0,
    surfaceRight: 799,
    surfaceBottom: 599,
    viewportWidth: 800,
    viewportHeight: 600
  },
  failingDebuggerMethods = new Set(),
  randomValues = [0.5],
  deferAlarmClears = false,
  beforeLookupCallback = null
) {
  const timers = new FakeTimers();
  let randomIndex = 0;
  const debuggerMethodCallCounts = new Map();
  const state = {
    tabs: initialTabs.map((tab) => ({ ...tab })),
    windowFocused,
    nativeMessages: [],
    nativeConnectCalls: [],
    attachCalls: [],
    detachCalls: [],
    debuggerCommands: [],
    alarmCreates: [],
    alarmClears: [],
    activeAlarms: new Map(),
    pendingAlarmClears: []
  };

  state.completePendingAlarmClears = () => {
    for (const complete of state.pendingAlarmClears.splice(0)) {
      complete();
    }
  };

  const events = {
    alarm: new MockEvent(),
    tabUpdated: new MockEvent(),
    tabRemoved: new MockEvent(),
    debuggerDetach: new MockEvent(),
    installed: new MockEvent(),
    startup: new MockEvent(),
    nativeMessage: new MockEvent(),
    nativeDisconnect: new MockEvent()
  };

  const nativePort = {
    onMessage: events.nativeMessage,
    onDisconnect: events.nativeDisconnect,
    postMessage(message) {
      state.nativeMessages.push(structuredClone(message));
    }
  };

  const chrome = {
    alarms: {
      create(name, alarmInfo) {
        const copy = structuredClone(alarmInfo);
        state.alarmCreates.push({
          name,
          alarmInfo: copy,
          atMs: timers.now
        });
        state.activeAlarms.set(name, copy);
      },
      clear(name, callback) {
        state.alarmClears.push({ name, atMs: timers.now });
        const complete = () => {
          const existed = state.activeAlarms.delete(name);
          if (callback) {
            callback(existed);
          }
        };
        if (deferAlarmClears) {
          state.pendingAlarmClears.push(complete);
        } else {
          complete();
        }
      },
      onAlarm: events.alarm
    },
    debugger: {
      attach(source, version, callback) {
        state.attachCalls.push({ source: { ...source }, version });
        callback();
      },
      detach(source, callback) {
        state.detachCalls.push({ source: { ...source } });
        events.debuggerDetach.emit(source, "target_closed");
        callback();
      },
      sendCommand(source, method, params, callback) {
        const methodCallNumber =
          (debuggerMethodCallCounts.get(method) || 0) + 1;
        debuggerMethodCallCounts.set(method, methodCallNumber);
        state.debuggerCommands.push({
          source: { ...source },
          method,
          params: structuredClone(params),
          atMs: timers.now
        });

        const configuredFailures = failingDebuggerMethods.get?.(method);
        const shouldFail =
          failingDebuggerMethods instanceof Map
            ? configuredFailures?.has?.(methodCallNumber) === true
            : failingDebuggerMethods.has?.(method) === true;
        if (shouldFail) {
          chrome.runtime.lastError = {
            message: `simulated ${method} failure`
          };
          callback();
          chrome.runtime.lastError = null;
          return;
        }

        if (method === "Runtime.evaluate") {
          callback({
            result: {
              value: structuredClone(evaluationValue)
            }
          });
        } else {
          callback({});
        }
      },
      onDetach: events.debuggerDetach
    },
    runtime: {
      lastError: null,
      getManifest() {
        return { version: "1.5.0" };
      },
      connectNative(name) {
        state.nativeConnectCalls.push({ name, atMs: timers.now });
        assert.equal(name, "com.stayactive.chrome_cursor_input");
        return nativePort;
      },
      onInstalled: events.installed,
      onStartup: events.startup
    },
    tabs: {
      query(_queryInfo, callback) {
        callback(state.tabs.map((tab) => ({ ...tab })));
      },
      get(tabId, callback) {
        beforeLookupCallback?.({
          kind: "tab",
          id: tabId,
          events,
          state
        });
        callback({ ...state.tabs.find((tab) => tab.id === tabId) });
      },
      onUpdated: events.tabUpdated,
      onRemoved: events.tabRemoved
    },
    windows: {
      get(windowId, callback) {
        beforeLookupCallback?.({
          kind: "window",
          id: windowId,
          events,
          state
        });
        callback({ id: windowId, focused: state.windowFocused });
      }
    }
  };

  class HarnessDate extends Date {
    constructor(...args) {
      super(...(args.length > 0 ? args : [timers.now]));
    }

    static now() {
      return timers.now;
    }
  }

  const harnessMath = Object.create(Math);
  harnessMath.random = () => {
    const value =
      randomValues[Math.min(randomIndex, Math.max(0, randomValues.length - 1))] ??
      0.5;
    randomIndex += 1;
    return value;
  };

  const context = vm.createContext({
    chrome,
    clearInterval() {},
    clearTimeout: timers.clearTimeout.bind(timers),
    console,
    Date: HarnessDate,
    Math: harnessMath,
    setInterval() {
      return 1;
    },
    setTimeout: timers.setTimeout.bind(timers),
    structuredClone,
    URL
  });
  vm.runInContext(backgroundSource, context, { filename: "background.js" });

  return { events, state, timers };
}

async function flushWorkQueue() {
  for (let index = 0; index < 8; index += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
}

async function advanceTimers(timers, delayMs) {
  timers.advanceBy(delayMs);
  await flushWorkQueue();
}

async function completeVisiblePulse(timers) {
  await advanceTimers(timers, 150);
  await advanceTimers(timers, 5_000);
  await advanceTimers(timers, 250);
}

function enable(events) {
  events.nativeMessage.emit({ type: "state", enabled: true });
}

function statusMessages(state) {
  return state.nativeMessages.filter((message) => message.type === "status");
}

function pulseAlarmCreates(state) {
  return state.alarmCreates.filter(({ name }) =>
    name.startsWith("stayactive-cursor-pulse-")
  );
}

function evaluationTimes(state) {
  return state.debuggerCommands
    .filter(({ method }) => method === "Runtime.evaluate")
    .map(({ atMs }) => atMs);
}

test("every scheduled pulse chooses a fresh inclusive 20-35 second start delay", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 60,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Set(),
    [0, 0.5, 1]
  );

  enable(events);
  await flushWorkQueue();
  await completeVisiblePulse(timers);

  await advanceTimers(timers, 14_600);
  assert.deepEqual(evaluationTimes(state), [0, 20_000]);
  await completeVisiblePulse(timers);

  await advanceTimers(timers, 22_100);
  assert.deepEqual(evaluationTimes(state), [0, 20_000, 47_500]);
  await completeVisiblePulse(timers);

  await advanceTimers(timers, 29_600);
  assert.deepEqual(evaluationTimes(state), [0, 20_000, 47_500, 82_500]);

  const pulseAlarms = pulseAlarmCreates(state);
  assert.deepEqual(
    pulseAlarms.slice(0, 4).map(({ atMs, alarmInfo }) => [
      atMs,
      alarmInfo.when
    ]),
    [
      [0, 30_000],
      [20_000, 50_000],
      [47_500, 82_500],
      [82_500, 117_500]
    ]
  );
  assert.ok(
    pulseAlarms.every(
      ({ alarmInfo }) =>
        Object.hasOwn(alarmInfo, "when") &&
        !Object.hasOwn(alarmInfo, "periodInMinutes")
    )
  );
  assert.equal(new Set(pulseAlarms.map(({ name }) => name)).size, pulseAlarms.length);
});

test("timer claim ignores the delayed fallback from its completed generation", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 61,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Set(),
    [0, 0.5]
  );

  enable(events);
  await flushWorkQueue();
  const firstAlarm = pulseAlarmCreates(state)[0];
  await completeVisiblePulse(timers);
  await advanceTimers(timers, 14_600);
  await completeVisiblePulse(timers);
  assert.deepEqual(evaluationTimes(state), [0, 20_000]);

  await advanceTimers(timers, 4_600);
  events.alarm.emit({
    name: firstAlarm.name,
    scheduledTime: firstAlarm.alarmInfo.when
  });
  await flushWorkQueue();

  assert.deepEqual(evaluationTimes(state), [0, 20_000]);
});

test("durable alarm replaces a missing worker timer and cannot duplicate its pulse", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 62,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Set(),
    [0, 0.5]
  );

  enable(events);
  await flushWorkQueue();
  const fallbackAlarm = pulseAlarmCreates(state)[0];
  await completeVisiblePulse(timers);

  // Simulate loss of the in-worker timeout while Chrome retains its alarm.
  timers.tasks.clear();
  timers.now = fallbackAlarm.alarmInfo.when;
  events.alarm.emit({
    name: fallbackAlarm.name,
    scheduledTime: fallbackAlarm.alarmInfo.when
  });
  await flushWorkQueue();
  assert.deepEqual(evaluationTimes(state), [0, 30_000]);

  events.alarm.emit({
    name: fallbackAlarm.name,
    scheduledTime: fallbackAlarm.alarmInfo.when
  });
  await flushWorkQueue();
  assert.deepEqual(evaluationTimes(state), [0, 30_000]);
});

test("alarm-first claim cancels its still-live timeout without a duplicate", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 65,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Set(),
    [1, 0.5]
  );

  enable(events);
  await flushWorkQueue();
  const fallbackAlarm = pulseAlarmCreates(state)[0];
  await completeVisiblePulse(timers);

  // Deliver the durable event first at the shared 35-second deadline. The
  // original setTimeout remains live until claimScheduledPulse cancels it.
  timers.now = fallbackAlarm.alarmInfo.when;
  events.alarm.emit({
    name: fallbackAlarm.name,
    scheduledTime: fallbackAlarm.alarmInfo.when
  });
  await flushWorkQueue();
  assert.deepEqual(evaluationTimes(state), [0, 35_000]);

  await completeVisiblePulse(timers);
  await advanceTimers(timers, 1);
  assert.deepEqual(evaluationTimes(state), [0, 35_000]);
});

test("delayed alarm cleanup cannot erase a rapid re-enable generation", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 63,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Set(),
    [0, 1],
    true
  );

  enable(events);
  await flushWorkQueue();
  await completeVisiblePulse(timers);
  const firstAlarm = pulseAlarmCreates(state)[0];

  events.nativeMessage.emit({ type: "state", enabled: false });
  await flushWorkQueue();
  enable(events);
  await flushWorkQueue();
  const secondAlarm = pulseAlarmCreates(state)[1];
  assert.notEqual(firstAlarm.name, secondAlarm.name);

  state.completePendingAlarmClears();
  assert.equal(state.activeAlarms.has(firstAlarm.name), false);
  assert.equal(state.activeAlarms.has(secondAlarm.name), true);

  await completeVisiblePulse(timers);
  await advanceTimers(timers, 29_600);
  assert.deepEqual(evaluationTimes(state), [0, 5_400, 40_400]);
});

test("a prior-worker alarm wake waits for trusted native state and pulses once", async () => {
  const { events, state } = createHarness([
    {
      id: 64,
      active: false,
      status: "complete",
      url: "https://windows.cloud.microsoft/",
      windowId: 1
    }
  ]);

  events.alarm.emit({
    name: "stayactive-cursor-pulse-prior-worker-9",
    scheduledTime: 25_000
  });
  await flushWorkQueue();
  assert.deepEqual(evaluationTimes(state), []);

  enable(events);
  await flushWorkQueue();
  assert.deepEqual(evaluationTimes(state), [0]);

  // Repeated trusted state polls do not reset cadence or replay the wake.
  const scheduledAlarmCount = pulseAlarmCreates(state).length;
  enable(events);
  await flushWorkQueue();
  assert.deepEqual(evaluationTimes(state), [0]);
  assert.equal(pulseAlarmCreates(state).length, scheduledAlarmCount);
});

test("retired same-session alarm stays stale while native state is untrusted", async () => {
  const { events, state, timers } = createHarness([
    {
      id: 66,
      active: false,
      status: "complete",
      url: "https://windows.cloud.microsoft/",
      windowId: 1
    }
  ]);

  enable(events);
  await flushWorkQueue();
  const retiredAlarm = pulseAlarmCreates(state)[0];
  await completeVisiblePulse(timers);

  events.nativeDisconnect.emit();
  const connectCountAfterDisconnect = state.nativeConnectCalls.length;
  events.alarm.emit({
    name: retiredAlarm.name,
    scheduledTime: retiredAlarm.alarmInfo.when
  });
  await flushWorkQueue();

  assert.equal(state.nativeConnectCalls.length, connectCountAfterDisconnect);

  enable(events);
  await flushWorkQueue();
  assert.deepEqual(evaluationTimes(state), [0, 5_400]);
});

test("one background eligible tab gets a visible movement, dwell, and return", async () => {
  const { events, state, timers } = createHarness([
    {
      id: 7,
      active: true,
      status: "complete",
      url: "https://windows.cloud.microsoft/",
      windowId: 2
    }
  ]);

  enable(events);
  await flushWorkQueue();

  assert.deepEqual(state.attachCalls, [
    { source: { tabId: 7 }, version: "1.3" }
  ]);
  const dispatches = state.debuggerCommands.filter(
    (command) => command.method === "Input.dispatchMouseEvent"
  );
  assert.equal(dispatches.length, 1);

  await advanceTimers(timers, 149);
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Input.dispatchMouseEvent"
    ).length,
    1
  );

  await advanceTimers(timers, 1);
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Input.dispatchMouseEvent"
    ).length,
    2
  );
  const keyDispatches = state.debuggerCommands.filter(
    (command) => command.method === "Input.dispatchKeyEvent"
  );
  assert.deepEqual(
    keyDispatches.map(({ params, atMs }) => ({
      params,
      atMs
    })),
    [
      {
        params: {
          modifiers: 0,
          key: "f",
          code: "KeyF",
          windowsVirtualKeyCode: 70,
          location: 0,
          autoRepeat: false,
          isKeypad: false,
          isSystemKey: false,
          type: "keyDown",
          text: "f",
          unmodifiedText: "f"
        },
        atMs: 150
      },
      {
        params: {
          modifiers: 0,
          key: "f",
          code: "KeyF",
          windowsVirtualKeyCode: 70,
          location: 0,
          autoRepeat: false,
          isKeypad: false,
          isSystemKey: false,
          type: "keyUp"
        },
        atMs: 150
      }
    ]
  );

  await advanceTimers(timers, 4_999);
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Input.dispatchMouseEvent"
    ).length,
    2
  );

  await advanceTimers(timers, 1);
  const completedDispatches = state.debuggerCommands.filter(
    (command) => command.method === "Input.dispatchMouseEvent"
  );
  assert.equal(completedDispatches.length, 3);
  assert.deepEqual(
    completedDispatches.map(({ params }) => [params.type, params.x, params.y]),
    [
      ["mouseMoved", 384, 300],
      ["mouseMoved", 448, 300],
      ["mouseMoved", 384, 300]
    ]
  );
  assert.equal(
    Math.hypot(
      completedDispatches[1].params.x - completedDispatches[0].params.x,
      completedDispatches[1].params.y - completedDispatches[0].params.y
    ),
    64
  );
  assert.equal(completedDispatches[1].atMs - completedDispatches[0].atMs, 150);
  assert.equal(completedDispatches[2].atMs - completedDispatches[1].atMs, 5_000);
  assert.ok(completedDispatches.every(({ params }) => params.buttons === 0));
  assert.ok(completedDispatches.every(({ params }) => params.button === "none"));

  const markerCommands = state.debuggerCommands.filter(
    (command) => command.method === "Overlay.highlightQuad"
  );
  assert.equal(markerCommands.length, 3);
  assert.deepEqual(
    markerCommands.map(({ params }) => params.quad),
    [
      [384, 286, 398, 300, 384, 314, 370, 300],
      [448, 286, 462, 300, 448, 314, 434, 300],
      [384, 286, 398, 300, 384, 314, 370, 300]
    ]
  );
  assert.ok(
    markerCommands.every(
      ({ params }) =>
        params.color.r === 255 &&
        params.color.g === 32 &&
        params.color.b === 96 &&
        params.color.a === 0.9 &&
        params.outlineColor.r === 255 &&
        params.outlineColor.g === 255 &&
        params.outlineColor.b === 255 &&
        params.outlineColor.a === 1
    )
  );

  await advanceTimers(timers, 250);

  const domainSetup = state.debuggerCommands.filter(
    (command) => command.method === "DOM.enable" || command.method === "Overlay.enable"
  );
  assert.deepEqual(
    domainSetup.map(({ method }) => method),
    ["DOM.enable", "Overlay.enable"]
  );
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Overlay.hideHighlight"
    ).length,
    1
  );
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Overlay.disable"
    ).length,
    1
  );
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "DOM.hideHighlight"
    ).length,
    1
  );
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "DOM.disable"
    ).length,
    1
  );
  assert.equal(keyDispatches.length, 2);

  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "attached" && detail === "background_pulse"
    )
  );
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "pulsed" && detail === "visual_marker_shown"
    )
  );

  for (const message of statusMessages(state)) {
    assert.deepEqual(
      Object.keys(message).sort(),
      ["detail", "extensionVersion", "status", "type"]
    );
    assert.doesNotMatch(JSON.stringify(message), /https?:|tabId|windowId|title/i);
  }
});

test("key-up is attempted when lowercase f key-down reports an error", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 67,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Map([["Input.dispatchKeyEvent", new Set([1])]])
  );

  enable(events);
  await flushWorkQueue();
  await advanceTimers(timers, 150);

  const keyDispatches = state.debuggerCommands.filter(
    ({ method }) => method === "Input.dispatchKeyEvent"
  );
  assert.deepEqual(
    keyDispatches.map(({ params }) => [
      params.type,
      params.key,
      params.code,
      params.text,
      params.unmodifiedText
    ]),
    [
      ["keyDown", "f", "KeyF", "f", "f"],
      ["keyUp", "f", "KeyF", undefined, undefined]
    ]
  );
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "error" && detail === "dispatch_failed"
    )
  );
});

test("one failed f key-up is retried before reporting dispatch failure", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 69,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Map([["Input.dispatchKeyEvent", new Set([2])]])
  );

  enable(events);
  await flushWorkQueue();
  await advanceTimers(timers, 150);

  const keyDispatches = state.debuggerCommands.filter(
    ({ method }) => method === "Input.dispatchKeyEvent"
  );
  assert.deepEqual(
    keyDispatches.map(({ params }) => params.type),
    ["keyDown", "keyUp", "keyUp"]
  );
  assert.deepEqual(keyDispatches[2].params, keyDispatches[1].params);
  for (const method of [
    "Overlay.hideHighlight",
    "DOM.hideHighlight",
    "Overlay.disable",
    "DOM.disable"
  ]) {
    assert.equal(
      state.debuggerCommands.filter((command) => command.method === method).length,
      1,
      method
    );
  }
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "error" && detail === "dispatch_failed"
    )
  );
});

test("a small surface uses its bounded available movement and returns", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 11,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    {
      eligible: true,
      x: 2,
      y: 7,
      movedX: 18,
      movedY: 7,
      surfaceLeft: 2,
      surfaceTop: 5,
      surfaceRight: 18,
      surfaceBottom: 9,
      viewportWidth: 20,
      viewportHeight: 12
    }
  );

  enable(events);
  await flushWorkQueue();
  await completeVisiblePulse(timers);

  const dispatches = state.debuggerCommands.filter(
    (command) => command.method === "Input.dispatchMouseEvent"
  );
  assert.deepEqual(
    dispatches.map(({ params }) => [params.x, params.y]),
    [
      [2, 7],
      [18, 7],
      [2, 7]
    ]
  );
  assert.ok(
    dispatches.every(
      ({ params }) =>
        params.x >= 2 &&
        params.x <= 18 &&
        params.y >= 5 &&
        params.y <= 9
    )
  );
  assert.equal(dispatches[1].params.x - dispatches[0].params.x, 16);

  const markerCommands = state.debuggerCommands.filter(
    (command) => command.method === "Overlay.highlightQuad"
  );
  assert.deepEqual(
    markerCommands.map(({ params }) => params.quad),
    [
      [2, 0, 16, 7, 2, 11, 0, 7],
      [18, 0, 19, 7, 18, 11, 4, 7],
      [2, 0, 16, 7, 2, 11, 0, 7]
    ]
  );
  assert.ok(
    markerCommands.every(({ params }) =>
      params.quad.every(
        (coordinate, index) =>
          coordinate >= 0 && coordinate <= (index % 2 === 0 ? 19 : 11)
      )
    )
  );
});

test("quad failure uses a bounded integer DOM rectangle fallback", async () => {
  const evaluationValue = {
    eligible: true,
    x: 384,
    y: 300,
    movedX: 448,
    movedY: 300,
    surfaceLeft: 0,
    surfaceTop: 0,
    surfaceRight: 799,
    surfaceBottom: 599,
    viewportWidth: 800,
    viewportHeight: 600
  };
  const { events, state, timers } = createHarness(
    [
      {
        id: 12,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    evaluationValue,
    new Set(["Overlay.highlightQuad"])
  );

  enable(events);
  await flushWorkQueue();
  await completeVisiblePulse(timers);

  const dispatches = state.debuggerCommands.filter(
    (command) => command.method === "Input.dispatchMouseEvent"
  );
  assert.deepEqual(
    dispatches.map(({ params }) => [params.type, params.x, params.y]),
    [
      ["mouseMoved", 384, 300],
      ["mouseMoved", 448, 300],
      ["mouseMoved", 384, 300]
    ]
  );
  const fallbackMarkers = state.debuggerCommands.filter(
    (command) => command.method === "DOM.highlightRect"
  );
  assert.deepEqual(
    fallbackMarkers.map(({ params }) => [
      params.x,
      params.y,
      params.width,
      params.height
    ]),
    [
      [370, 286, 28, 28],
      [434, 286, 28, 28],
      [370, 286, 28, 28]
    ]
  );
  assert.ok(
    fallbackMarkers.every(({ params }) =>
      [params.x, params.y, params.width, params.height].every(Number.isInteger)
    )
  );
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Overlay.disable"
    ).length,
    1
  );
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "attached" && detail === "background_pulse"
    )
  );
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "pulsed" && detail === "visual_marker_shown"
    )
  );
});

test("Overlay enable failure still uses the DOM rectangle fallback", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 40,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Set(["Overlay.enable"])
  );

  enable(events);
  await flushWorkQueue();
  await completeVisiblePulse(timers);

  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Overlay.highlightQuad"
    ).length,
    0
  );
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "DOM.highlightRect"
    ).length,
    3
  );
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "pulsed" && detail === "visual_marker_shown"
    )
  );
});

test("DOM agent initialization failure reports marker unavailable but preserves input", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 41,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Set(["DOM.enable"])
  );

  enable(events);
  await flushWorkQueue();
  await completeVisiblePulse(timers);

  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Input.dispatchMouseEvent"
    ).length,
    3
  );
  assert.equal(
    state.debuggerCommands.filter(
      (command) =>
        command.method === "Overlay.highlightQuad" ||
        command.method === "DOM.highlightRect"
    ).length,
    0
  );
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "pulsed" && detail === "visual_marker_unavailable"
    )
  );
});

test("one failed marker frame makes the full animation unavailable", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 42,
        active: true,
        status: "complete",
        url: "https://windows365.microsoft.com/",
        windowId: 1
      }
    ],
    true,
    undefined,
    new Map([
      ["Overlay.highlightQuad", new Set([2])],
      ["DOM.highlightRect", new Set([1])]
    ])
  );

  enable(events);
  await flushWorkQueue();
  await completeVisiblePulse(timers);

  assert.equal(
    state.debuggerCommands.filter((command) => command.method.startsWith("Input."))
      .length,
    0
  );
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "pulse_skipped" &&
        detail === "target_foreground_marker_unavailable"
    )
  );
});

test("cleanup attempts every command even when one cleanup command fails", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 43,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Set(["Overlay.hideHighlight"])
  );

  enable(events);
  await flushWorkQueue();
  await completeVisiblePulse(timers);

  for (const method of [
    "Overlay.hideHighlight",
    "DOM.hideHighlight",
    "Overlay.disable",
    "DOM.disable"
  ]) {
    assert.equal(
      state.debuggerCommands.filter((command) => command.method === method).length,
      1,
      method
    );
  }
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "pulsed" && detail === "visual_marker_shown"
    )
  );
});

test("total marker failure reports unavailable without blocking background movement", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 13,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Set(["Overlay.highlightQuad", "DOM.highlightRect"])
  );

  enable(events);
  await flushWorkQueue();
  await completeVisiblePulse(timers);

  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Input.dispatchMouseEvent"
    ).length,
    3
  );
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "pulsed" && detail === "visual_marker_unavailable"
    )
  );
  for (const method of [
    "Overlay.hideHighlight",
    "DOM.hideHighlight",
    "Overlay.disable",
    "DOM.disable"
  ]) {
    assert.equal(
      state.debuggerCommands.filter((command) => command.method === method).length,
      1
    );
  }
});

test("foreground eligible tab gets visual-only movement and no input", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 8,
        active: true,
        status: "complete",
        url: "https://windows365.microsoft.com/",
        windowId: 3
      }
    ],
    true
  );

  enable(events);
  await flushWorkQueue();

  assert.equal(state.attachCalls.length, 1);
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Input.dispatchMouseEvent"
    ).length,
    0
  );
  await completeVisiblePulse(timers);
  assert.deepEqual(
    state.debuggerCommands
      .filter((command) => command.method === "Overlay.highlightQuad")
      .map(({ params }) => params.quad),
    [
      [384, 286, 398, 300, 384, 314, 370, 300],
      [448, 286, 462, 300, 448, 314, 434, 300],
      [384, 286, 398, 300, 384, 314, 370, 300]
    ]
  );
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method.startsWith("Input.")
    ).length,
    0
  );
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "pulse_skipped" &&
        detail === "target_foreground_visualized"
    )
  );
});

test("focus transition completes the marker animation but sends no further input", async () => {
  const { events, state, timers } = createHarness([
    {
      id: 44,
      active: true,
      status: "complete",
      url: "https://windows365.microsoft.com/",
      windowId: 5
    }
  ]);

  enable(events);
  await flushWorkQueue();
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Input.dispatchMouseEvent"
    ).length,
    1
  );

  state.windowFocused = true;
  await completeVisiblePulse(timers);

  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Input.dispatchMouseEvent"
    ).length,
    1
  );
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Input.dispatchKeyEvent"
    ).length,
    0
  );
  assert.equal(
    state.debuggerCommands.filter(
      (command) => command.method === "Overlay.highlightQuad"
    ).length,
    3
  );
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "pulse_skipped" &&
        detail === "target_foreground_visualized"
    )
  );
});

test("activating an RDP tab in focused Chrome stops key and later mouse input", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 68,
        active: false,
        status: "complete",
        url: "https://windows365.microsoft.com/",
        windowId: 6
      }
    ],
    true
  );

  enable(events);
  await flushWorkQueue();
  assert.equal(
    state.debuggerCommands.filter(
      ({ method }) => method === "Input.dispatchMouseEvent"
    ).length,
    1
  );

  state.tabs[0].active = true;
  await completeVisiblePulse(timers);

  assert.equal(
    state.debuggerCommands.filter(
      ({ method }) => method === "Input.dispatchMouseEvent"
    ).length,
    1
  );
  assert.equal(
    state.debuggerCommands.filter(
      ({ method }) => method === "Input.dispatchKeyEvent"
    ).length,
    0
  );
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "pulse_skipped" &&
        detail === "target_foreground_visualized"
    )
  );
});

test("navigation during the five-second hold sends no further input", async () => {
  const { events, state, timers } = createHarness([
    {
      id: 70,
      active: false,
      status: "complete",
      url: "https://windows.cloud.microsoft/",
      windowId: 1
    }
  ]);

  enable(events);
  await flushWorkQueue();
  await advanceTimers(timers, 150);

  const inputCountBeforeNavigation = state.debuggerCommands.filter(
    ({ method }) => method.startsWith("Input.")
  ).length;
  assert.equal(inputCountBeforeNavigation, 4);

  state.tabs[0].status = "loading";
  state.tabs[0].url = "https://example.com/";
  events.tabUpdated.emit(70, {
    status: "loading",
    url: "https://example.com/"
  });
  await flushWorkQueue();
  await advanceTimers(timers, 5_000);

  assert.equal(
    state.debuggerCommands.filter(({ method }) => method.startsWith("Input."))
      .length,
    inputCountBeforeNavigation
  );
  assert.equal(state.detachCalls.length, 1);
});

test("disabling during the five-second hold sends no further input", async () => {
  const { events, state, timers } = createHarness([
    {
      id: 71,
      active: false,
      status: "complete",
      url: "https://windows.cloud.microsoft/",
      windowId: 1
    }
  ]);

  enable(events);
  await flushWorkQueue();
  await advanceTimers(timers, 150);

  const inputCountBeforeDisable = state.debuggerCommands.filter(
    ({ method }) => method.startsWith("Input.")
  ).length;
  assert.equal(inputCountBeforeDisable, 4);

  events.nativeMessage.emit({ type: "state", enabled: false });
  await flushWorkQueue();
  await advanceTimers(timers, 5_000);

  assert.equal(
    state.debuggerCommands.filter(({ method }) => method.startsWith("Input."))
      .length,
    inputCountBeforeDisable
  );
  assert.equal(state.detachCalls.length, 1);
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "disabled" && detail === "native_state_disabled"
    )
  );
});

test("disable interleaved with live tab lookup fails closed before input", async () => {
  let disabled = false;
  const { events, state } = createHarness(
    [
      {
        id: 72,
        active: false,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Set(),
    [0.5],
    false,
    ({ kind, events: lookupEvents }) => {
      if (!disabled && kind === "tab") {
        disabled = true;
        lookupEvents.nativeMessage.emit({ type: "state", enabled: false });
      }
    }
  );

  enable(events);
  await flushWorkQueue();

  assert.equal(
    state.debuggerCommands.filter(({ method }) => method.startsWith("Input."))
      .length,
    0
  );
  assert.equal(
    state.debuggerCommands.filter(({ method }) => method === "Runtime.evaluate")
      .length,
    0
  );
  assert.equal(state.detachCalls.length, 1);
});

test("disable interleaved with focused-window lookup fails closed before input", async () => {
  let disabled = false;
  const { events, state } = createHarness(
    [
      {
        id: 73,
        active: true,
        status: "complete",
        url: "https://windows.cloud.microsoft/",
        windowId: 1
      }
    ],
    false,
    undefined,
    new Set(),
    [0.5],
    false,
    ({ kind, events: lookupEvents }) => {
      if (!disabled && kind === "window") {
        disabled = true;
        lookupEvents.nativeMessage.emit({ type: "state", enabled: false });
      }
    }
  );

  enable(events);
  await flushWorkQueue();

  assert.equal(
    state.debuggerCommands.filter(({ method }) => method.startsWith("Input."))
      .length,
    0
  );
  assert.equal(
    state.debuggerCommands.filter(({ method }) => method === "Runtime.evaluate")
      .length,
    0
  );
  assert.equal(state.detachCalls.length, 1);
});

test("foreground total marker failure reports unavailable and still sends no input", async () => {
  const { events, state, timers } = createHarness(
    [
      {
        id: 14,
        active: true,
        status: "complete",
        url: "https://windows365.microsoft.com/",
        windowId: 4
      }
    ],
    true,
    undefined,
    new Set(["Overlay.highlightQuad", "DOM.highlightRect"])
  );

  enable(events);
  await flushWorkQueue();
  await completeVisiblePulse(timers);

  assert.equal(
    state.debuggerCommands.filter((command) => command.method.startsWith("Input."))
      .length,
    0
  );
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "pulse_skipped" &&
        detail === "target_foreground_marker_unavailable"
    )
  );
});

test("only each exact supported HTTPS host is accepted", async () => {
  const exactHosts = [
    "windows.cloud.microsoft",
    "windows365.microsoft.com",
    "rdweb.wvd.microsoft.com",
    "client.wvd.microsoft.com"
  ];

  for (const [index, host] of exactHosts.entries()) {
    const { events, state } = createHarness([
      {
        id: 20 + index,
        active: false,
        status: "complete",
        url: `https://${host}/`,
        windowId: 1
      }
    ]);

    enable(events);
    await flushWorkQueue();
    assert.equal(state.attachCalls.length, 1, host);
  }
});

test("multiple eligible tabs fail closed", async () => {
  const { events, state } = createHarness([
    {
      id: 1,
      active: false,
      status: "complete",
      url: "https://rdweb.wvd.microsoft.com/",
      windowId: 1
    },
    {
      id: 2,
      active: false,
      status: "complete",
      url: "https://client.wvd.microsoft.com/",
      windowId: 1
    }
  ]);

  enable(events);
  await flushWorkQueue();

  assert.equal(state.attachCalls.length, 0);
  assert.equal(state.debuggerCommands.length, 0);
  assert.ok(
    statusMessages(state).some(
      ({ status, detail }) =>
        status === "waiting" && detail === "multiple_eligible_tabs"
    )
  );
});

test("lookalike host is not eligible", async () => {
  const ineligibleUrls = [
    "https://sub.windows.cloud.microsoft/",
    "https://windows.cloud.microsoft.example/",
    "http://windows.cloud.microsoft/",
    "https://windows.cloud.microsoft:444/"
  ];

  for (const [index, url] of ineligibleUrls.entries()) {
    const { events, state } = createHarness([
      {
        id: 30 + index,
        active: false,
        status: "complete",
        url,
        windowId: 1
      }
    ]);

    enable(events);
    await flushWorkQueue();
    assert.equal(state.attachCalls.length, 0, url);
    assert.ok(
      statusMessages(state).some(
        ({ status, detail }) =>
          status === "waiting" && detail === "no_eligible_tab"
      ),
      url
    );
  }
});

test("disabling while attached cleans both marker domains before detach", async () => {
  const { events, state, timers } = createHarness([
    {
      id: 45,
      active: false,
      status: "complete",
      url: "https://client.wvd.microsoft.com/",
      windowId: 1
    }
  ]);

  enable(events);
  await flushWorkQueue();
  await completeVisiblePulse(timers);

  events.nativeMessage.emit({ type: "state", enabled: false });
  await flushWorkQueue();

  assert.equal(state.detachCalls.length, 1);
  for (const method of [
    "Overlay.hideHighlight",
    "DOM.hideHighlight",
    "Overlay.disable",
    "DOM.disable"
  ]) {
    assert.equal(
      state.debuggerCommands.filter((command) => command.method === method).length,
      2,
      method
    );
  }
});

test("disable detaches and user cancellation blocks reattach until toggled", async () => {
  const { events, state, timers } = createHarness([
    {
      id: 10,
      active: false,
      status: "complete",
      url: "https://client.wvd.microsoft.com/",
      windowId: 1
    }
  ]);

  enable(events);
  await flushWorkQueue();
  assert.equal(state.attachCalls.length, 1);
  await completeVisiblePulse(timers);

  events.debuggerDetach.emit({ tabId: 10 }, "canceled_by_user");
  await flushWorkQueue();
  enable(events);
  await flushWorkQueue();
  assert.equal(state.attachCalls.length, 1);

  events.nativeMessage.emit({ type: "state", enabled: false });
  await flushWorkQueue();
  enable(events);
  await flushWorkQueue();
  assert.equal(state.attachCalls.length, 2);
  await completeVisiblePulse(timers);
});
