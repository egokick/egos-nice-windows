using System.Text.Json;
using System.Text.RegularExpressions;

namespace stayactive.IntegrationTests;

public sealed class ChromeCursorInputStayAwakeStaticSafetyTests
{
    private static readonly string[] ExactEligibleHosts =
    [
        "client.wvd.microsoft.com",
        "rdweb.wvd.microsoft.com",
        "windows.cloud.microsoft",
        "windows365.microsoft.com"
    ];

    [Fact]
    public void Program_UsesExactMenuLabelCheckOnClickAndPersistentToggle()
    {
        var program = ReadRepositoryFile("stayactive", "Program.cs");

        AssertPatternMatches(
            program,
            """
            (?s)_chromeCursorInputStayAwakeMenuItem\s*=\s*new\s+ToolStripMenuItem\(\s*
            "chrome-cursor-input-stay-awake"\s*\)\s*
            \{\s*CheckOnClick\s*=\s*true\s*\};
            """);
        AssertPatternMatches(
            program,
            """
            (?s)_chromeCursorInputStayAwakeMenuItem\.Click\s*\+=\s*\(_,\s*_\)\s*=>\s*
            ToggleChromeCursorInputStayAwake\(\);
            """);

        var toggle = ExtractBraceBlock(
            program,
            "private void ToggleChromeCursorInputStayAwake()");
        Assert.Contains(
            "var enabled = _chromeCursorInputStayAwakeMenuItem.Checked;",
            toggle,
            StringComparison.Ordinal);
        Assert.Contains(
            "UpdateSettings(settings => settings.ChromeCursorInputStayAwakeEnabled = enabled);",
            toggle,
            StringComparison.Ordinal);
        Assert.Contains(
            "_chromeCursorInputStayAwakeService.EnsureRegistered();",
            toggle,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Program_RunsNativeHostBeforeAcquiringSingletonMutex()
    {
        var program = ReadRepositoryFile("stayactive", "Program.cs");
        var main = ExtractBraceBlock(program, "private static void Main(string[] args)");
        var nativeHostIndex = main.IndexOf(
            "ChromeCursorInputNativeMessagingHost.TryRun(args)",
            StringComparison.Ordinal);
        var singletonIndex = main.IndexOf(
            "new Mutex(true, \"StayActive.Singleton\"",
            StringComparison.Ordinal);

        Assert.True(nativeHostIndex >= 0, "The Chrome native host entry point was not found.");
        Assert.True(
            singletonIndex > nativeHostIndex,
            "Chrome native messaging must run before the tray singleton check.");
        AssertPatternMatches(
            main,
            """
            (?s)if\s*\(\s*ChromeCursorInputNativeMessagingHost\.TryRun\(args\)\s*\)
            \s*\{\s*return;\s*\}
            """);
    }

    [Fact]
    public void NativeMessagingRegistration_IsGoogleChromeOnly()
    {
        var service = ReadRepositoryFile(
            "stayactive",
            "ChromeCursorInputStayAwakeService.cs");

        Assert.Contains(
            @"Software\Google\Chrome\NativeMessagingHosts",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "RegisterGoogleChromeNativeMessagingHost",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "OpenGoogleChromeExtensionsPage",
            service,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            @"Software\Microsoft\Edge\NativeMessagingHosts",
            service,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("msedge.exe", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("edge://extensions", service, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extension_IsManifestV3WithMinimalInputPermissions()
    {
        var manifestText = ReadRepositoryFile(
            "stayactive",
            "ChromeCursorInputStayAwakeExtension",
            "manifest.json");
        using var manifest = JsonDocument.Parse(manifestText);
        var root = manifest.RootElement;

        Assert.Equal(3, root.GetProperty("manifest_version").GetInt32());
        Assert.Equal(
            new[] { "alarms", "debugger", "nativeMessaging", "tabs" },
            root.GetProperty("permissions")
                .EnumerateArray()
                .Select(element => element.GetString())
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
        Assert.False(root.TryGetProperty("content_scripts", out _));
        Assert.False(root.TryGetProperty("host_permissions", out _));
        Assert.Equal(
            "background.js",
            root.GetProperty("background").GetProperty("service_worker").GetString());
    }

    [Fact]
    public void Extension_RestrictsTargetsToExactApprovedChromeRemoteHosts()
    {
        var background = ReadRepositoryFile(
            "stayactive",
            "ChromeCursorInputStayAwakeExtension",
            "background.js");
        var hostSet = Regex.Match(
            background,
            @"const\s+ELIGIBLE_HOSTS\s*=\s*new\s+Set\(\s*\[(?<hosts>[\s\S]*?)\]\s*\);");
        Assert.True(hostSet.Success, "ELIGIBLE_HOSTS was not found.");

        var actualHosts = Regex.Matches(hostSet.Groups["hosts"].Value, "\"(?<host>[^\"]+)\"")
            .Select(match => match.Groups["host"].Value)
            .OrderBy(host => host, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExactEligibleHosts, actualHosts);
        Assert.Contains("windows.cloud.microsoft", actualHosts);

        var inPageHostSet = Regex.Match(
            background,
            @"const\s+eligibleHosts\s*=\s*new\s+Set\(\s*\[(?<hosts>[\s\S]*?)\]\s*\);");
        Assert.True(inPageHostSet.Success, "The in-page host recheck was not found.");
        var inPageHosts = Regex.Matches(
                inPageHostSet.Groups["hosts"].Value,
                "\"(?<host>[^\"]+)\"")
            .Select(match => match.Groups["host"].Value)
            .OrderBy(host => host, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExactEligibleHosts, inPageHosts);

        var isEligibleTab = ExtractBraceBlock(background, "function isEligibleTab(tab)");
        Assert.Contains(
            "parsed.protocol === \"https:\"",
            isEligibleTab,
            StringComparison.Ordinal);
        Assert.Contains("parsed.port === \"\"", isEligibleTab, StringComparison.Ordinal);
        Assert.Contains("parsed.username === \"\"", isEligibleTab, StringComparison.Ordinal);
        Assert.Contains("parsed.password === \"\"", isEligibleTab, StringComparison.Ordinal);
        Assert.Contains(
            "ELIGIBLE_HOSTS.has(parsed.hostname)",
            isEligibleTab,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Extension_UsesFreshBoundedOneShotPulseScheduleWithoutDuplicates()
    {
        var background = ReadRepositoryFile(
            "stayactive",
            "ChromeCursorInputStayAwakeExtension",
            "background.js");
        var nextDelay = ExtractBraceBlock(
            background,
            "function nextPulseDelayMs()");
        var clearSchedule = ExtractBraceBlock(
            background,
            "function clearPulseSchedule()");
        var schedule = ExtractBraceBlock(
            background,
            "function scheduleNextPulse()");
        var claim = ExtractBraceBlock(
            background,
            "function claimScheduledPulse(generation)");
        var createAlarms = ExtractBraceBlock(
            background,
            "function createAlarms()");
        var alarmHandler = ExtractBraceBlock(
            background,
            "chrome.alarms.onAlarm.addListener((alarm) =>");
        var connectNative = ExtractBraceBlock(
            background,
            "function connectNative()");

        Assert.Equal(20_000, ReadIntegerConstant(background, "PULSE_DELAY_MIN_MS"));
        Assert.Equal(35_000, ReadIntegerConstant(background, "PULSE_DELAY_MAX_MS"));
        Assert.True(
            ReadIntegerConstant(background, "PULSE_ALARM_MIN_MS") >= 30_000,
            "The durable Chrome alarm fallback must respect Chrome's minimum alarm delay.");

        // Randomness is sampled inside the delay function, and that function is
        // called anew whenever the next cycle is scheduled. The integer range
        // includes both configured endpoints.
        Assert.Contains("Math.random()", nextDelay, StringComparison.Ordinal);
        Assert.Contains(
            "Math.max(0, Math.min(1,",
            nextDelay,
            StringComparison.Ordinal);
        AssertPatternMatches(
            nextDelay,
            """
            (?s)const\s+inclusiveRange\s*=\s*
            PULSE_DELAY_MAX_MS\s*-\s*PULSE_DELAY_MIN_MS\s*\+\s*1;\s*
            return\s*\(\s*
            PULSE_DELAY_MIN_MS\s*\+\s*
            Math\.min\(\s*
            inclusiveRange\s*-\s*1,\s*
            Math\.floor\(randomUnit\s*\*\s*inclusiveRange\)\s*
            \)\s*
            \);
            """);
        Assert.Single(
            Regex.Matches(schedule, @"\bnextPulseDelayMs\(\)").Cast<Match>());
        Assert.DoesNotContain(
            "const PULSE_MINUTES",
            background,
            StringComparison.Ordinal);
        Assert.Contains(
            "const PULSE_ALARM_SESSION = Date.now().toString(36);",
            background,
            StringComparison.Ordinal);

        // setTimeout is the normal 20-35 second path. The named Chrome alarm
        // is a one-shot fallback only; no fixed periodic pulse alarm remains.
        AssertPatternMatches(
            schedule,
            """
            (?s)pulseTimer\s*=\s*setTimeout\(\s*
            \(\)\s*=>\s*\{\s*
            claimScheduledPulse\(generation\);\s*
            \},\s*delayMs\s*\);
            """);
        AssertPatternMatches(
            schedule,
            """
            (?s)chrome\.alarms\.create\(\s*pulseAlarmName,\s*\{\s*
            when:\s*pulseAlarmScheduledTime\s*
            \}\s*\);
            """);
        Assert.DoesNotContain("periodInMinutes", schedule, StringComparison.Ordinal);
        Assert.DoesNotContain("PULSE_ALARM_PREFIX", createAlarms, StringComparison.Ordinal);
        Assert.Single(
            Regex.Matches(background, @"\bperiodInMinutes\s*:").Cast<Match>());

        // Clearing invalidates callbacks from the old generation and cancels
        // both timing mechanisms. Scheduling replaces any extant timeout.
        Assert.Contains(
            "pulseScheduleGeneration += 1;",
            clearSchedule,
            StringComparison.Ordinal);
        Assert.Contains(
            "pulseScheduleActive = false;",
            clearSchedule,
            StringComparison.Ordinal);
        Assert.Contains(
            "pulseScheduleClaimed = false;",
            clearSchedule,
            StringComparison.Ordinal);
        Assert.Contains(
            "const alarmName = pulseAlarmName;",
            clearSchedule,
            StringComparison.Ordinal);
        Assert.Contains("pulseAlarmName = null;", clearSchedule, StringComparison.Ordinal);
        Assert.Contains("clearTimeout(pulseTimer);", clearSchedule, StringComparison.Ordinal);
        Assert.Contains(
            "chrome.alarms.clear(alarmName);",
            clearSchedule,
            StringComparison.Ordinal);
        Assert.Contains("clearTimeout(pulseTimer);", schedule, StringComparison.Ordinal);
        Assert.Contains(
            "pulseScheduleGeneration = generation;",
            schedule,
            StringComparison.Ordinal);
        Assert.Contains(
            "pulseScheduleClaimed = false;",
            schedule,
            StringComparison.Ordinal);
        Assert.Contains(
            "const precedingAlarmName = pulseAlarmName;",
            schedule,
            StringComparison.Ordinal);
        Assert.Contains(
            "`${PULSE_ALARM_PREFIX}-${PULSE_ALARM_SESSION}-${generation}`",
            schedule,
            StringComparison.Ordinal);
        Assert.Contains(
            "chrome.alarms.clear(precedingAlarmName);",
            schedule,
            StringComparison.Ordinal);

        // The timeout and fallback alarm race for one generation. Exactly one
        // can claim it, and the next randomized cycle is established before
        // the serialized pulse animation begins.
        Assert.Contains("!pulseScheduleActive", claim, StringComparison.Ordinal);
        Assert.Contains("pulseScheduleClaimed", claim, StringComparison.Ordinal);
        Assert.Contains(
            "generation !== pulseScheduleGeneration",
            claim,
            StringComparison.Ordinal);
        Assert.Contains(
            "pulseScheduleClaimed = true;",
            claim,
            StringComparison.Ordinal);
        Assert.Contains("clearTimeout(pulseTimer);", claim, StringComparison.Ordinal);
        var rescheduleIndex = claim.IndexOf(
            "scheduleNextPulse();",
            StringComparison.Ordinal);
        var pulseIndex = claim.IndexOf(
            "await runScheduledPulse();",
            StringComparison.Ordinal);
        Assert.True(
            rescheduleIndex >= 0 && pulseIndex > rescheduleIndex,
            "The next randomized cycle must be scheduled before awaiting this pulse.");

        Assert.Contains(
            "const pulseAlarmPrefix = `${PULSE_ALARM_PREFIX}-`;",
            alarmHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "`${pulseAlarmPrefix}${PULSE_ALARM_SESSION}-`",
            alarmHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "alarm.name.startsWith(pulseAlarmPrefix)",
            alarmHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "!alarm.name.startsWith(currentSessionAlarmPrefix)",
            alarmHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "pendingPulseAlarmWake = true;",
            alarmHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "requestNativeState();",
            alarmHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "alarm.name === pulseAlarmName",
            alarmHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "alarm.scheduledTime === pulseAlarmScheduledTime",
            alarmHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "claimScheduledPulse(pulseScheduleGeneration);",
            alarmHandler,
            StringComparison.Ordinal);

        // Enabling still performs the immediate attach/pulse behavior; the
        // randomized timer governs subsequent cycles only.
        var scheduleOnEnableIndex = connectNative.IndexOf(
            "scheduleNextPulse();",
            StringComparison.Ordinal);
        var immediateReconcileIndex = connectNative.IndexOf(
            "await reconcileTarget(!recoveringDurablePulse);",
            StringComparison.Ordinal);
        Assert.True(scheduleOnEnableIndex >= 0);
        Assert.True(
            immediateReconcileIndex > scheduleOnEnableIndex,
            "Enabling must preserve the immediate reconcile-and-pulse path.");
        Assert.Contains(
            "let pendingPulseAlarmWake = false;",
            background,
            StringComparison.Ordinal);
        Assert.Contains(
            "const recoveringDurablePulse = pendingPulseAlarmWake;",
            connectNative,
            StringComparison.Ordinal);
        Assert.Contains(
            "pendingPulseAlarmWake = false;",
            connectNative,
            StringComparison.Ordinal);
        Assert.Contains(
            "await sendCursorPulse(\"scheduled_pulse\");",
            connectNative,
            StringComparison.Ordinal);
        Assert.True(
            Regex.Matches(background, @"\bclearPulseSchedule\(\)").Count >= 4,
            "Disable, disconnect, stale state, and replacement paths must cancel the schedule.");
    }

    [Fact]
    public void Extension_DispatchesVisibleBoundedMouseMovesWithDwellAndReturnsToStart()
    {
        var background = ReadRepositoryFile(
            "stayactive",
            "ChromeCursorInputStayAwakeExtension",
            "background.js");
        var pulse = ExtractBraceBlock(background, "async function sendCursorPulse");
        var validation = ExtractBraceBlock(background, "function validInputPoint(value)");
        var delay = ExtractBraceBlock(background, "function waitForDelay(delayMs)");

        var inputMethods = Regex.Matches(
                background,
                "sendDebuggerCommand\\([^,]+,\\s*\"(?<method>Input\\.[^\"]+)\"")
            .Select(match => match.Groups["method"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(method => method, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "Input.dispatchKeyEvent", "Input.dispatchMouseEvent" },
            inputMethods);
        Assert.Equal(
            3,
            Regex.Matches(pulse, "\"Input\\.dispatchMouseEvent\"").Count);
        Assert.Equal(
            3,
            Regex.Matches(background, "\"Input\\.dispatchMouseEvent\"").Count);

        Assert.Contains("type: \"mouseMoved\"", pulse, StringComparison.Ordinal);
        Assert.Contains("button: \"none\"", pulse, StringComparison.Ordinal);
        Assert.Contains("buttons: 0", pulse, StringComparison.Ordinal);
        Assert.Contains("pointerType: \"mouse\"", pulse, StringComparison.Ordinal);

        var movementDistance = ReadIntegerConstant(
            background,
            "CURSOR_MOVE_DISTANCE_CSS_PX");
        var establishDelay = ReadIntegerConstant(
            background,
            "CURSOR_ESTABLISH_DELAY_MS");
        var visibleHold = ReadIntegerConstant(
            background,
            "CURSOR_VISIBLE_HOLD_MS");
        Assert.InRange(
            movementDistance,
            16,
            64);
        Assert.InRange(establishDelay, 50, 500);
        Assert.Equal(5_000, visibleHold);

        Assert.Contains(
            "movementDistance <= CURSOR_MOVE_DISTANCE_CSS_PX",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "value.x >= value.surfaceLeft",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "value.x <= value.surfaceRight",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "value.y >= value.surfaceTop",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "value.y <= value.surfaceBottom",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "value.movedX >= value.surfaceLeft",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "value.movedX <= value.surfaceRight",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "value.movedY >= value.surfaceTop",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "value.movedY <= value.surfaceBottom",
            validation,
            StringComparison.Ordinal);

        AssertPatternMatches(
            delay,
            """
            return\s+new\s+Promise\(\s*
            \(resolve\)\s*=>\s*setTimeout\(resolve,\s*delayMs\)\s*
            \);
            """);
        var dispatches = Regex.Matches(
                pulse,
                """
                (?x)await\s+sendDebuggerCommand\(
                tabId,\s*"Input\.dispatchMouseEvent",\s*\{\s*
                \.\.\.baseEvent,\s*
                x:\s*(?<x>point\.(?:x|movedX)),\s*
                y:\s*(?<y>point\.(?:y|movedY))\s*
                \}\);
                """)
            .Cast<Match>()
            .ToArray();
        Assert.Equal(3, dispatches.Length);
        Assert.Equal(
            new[] { "point.x", "point.movedX", "point.x" },
            dispatches.Select(match => match.Groups["x"].Value).ToArray());
        Assert.Equal(
            new[] { "point.y", "point.movedY", "point.y" },
            dispatches.Select(match => match.Groups["y"].Value).ToArray());

        var establishDelayIndex = pulse.IndexOf(
            "await waitForDelay(CURSOR_ESTABLISH_DELAY_MS);",
            StringComparison.Ordinal);
        var visibleHoldIndex = pulse.IndexOf(
            "await waitForDelay(CURSOR_VISIBLE_HOLD_MS);",
            StringComparison.Ordinal);
        var returnHoldIndex = pulse.IndexOf(
            "await waitForDelay(CURSOR_RETURN_HOLD_MS);",
            StringComparison.Ordinal);
        Assert.True(dispatches[0].Index < establishDelayIndex);
        Assert.True(establishDelayIndex < dispatches[1].Index);
        Assert.True(dispatches[1].Index < visibleHoldIndex);
        Assert.True(visibleHoldIndex < dispatches[2].Index);
        Assert.True(dispatches[2].Index < returnHoldIndex);

        Assert.Contains(
            "const movementDistance = Math.min(",
            background,
            StringComparison.Ordinal);
        Assert.Contains(
            "${CURSOR_MOVE_DISTANCE_CSS_PX}",
            background,
            StringComparison.Ordinal);
        Assert.Contains(
            "Math.max(horizontalRoom, verticalRoom)",
            background,
            StringComparison.Ordinal);
        Assert.DoesNotContain("mousePressed", background, StringComparison.Ordinal);
        Assert.DoesNotContain("mouseReleased", background, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", background, StringComparison.Ordinal);
        Assert.DoesNotContain("SetCursorPos", background, StringComparison.Ordinal);
    }

    [Fact]
    public void Extension_BackgroundPulseSendsOneLowercaseFKeyCycleAndAlwaysReleases()
    {
        var background = ReadRepositoryFile(
            "stayactive",
            "ChromeCursorInputStayAwakeExtension",
            "background.js");
        var pulse = ExtractBraceBlock(background, "async function sendCursorPulse");
        var keyCycle = ExtractBraceBlock(
            background,
            "async function dispatchPulseKey(tabId)");

        Assert.Contains("const PULSE_KEY = \"f\";", background, StringComparison.Ordinal);
        Assert.Contains(
            "const PULSE_KEY_CODE = \"KeyF\";",
            background,
            StringComparison.Ordinal);
        Assert.Equal(
            70,
            ReadIntegerConstant(background, "PULSE_KEY_WINDOWS_VIRTUAL_CODE"));
        Assert.DoesNotContain("nativeVirtualKeyCode", keyCycle, StringComparison.Ordinal);

        Assert.Equal(
            3,
            Regex.Matches(keyCycle, "\"Input\\.dispatchKeyEvent\"").Count);
        Assert.Single(
            Regex.Matches(keyCycle, "type:\\s*\"keyDown\"").Cast<Match>());
        Assert.Single(
            Regex.Matches(keyCycle, "type:\\s*\"keyUp\"").Cast<Match>());
        Assert.Equal(
            3,
            Regex.Matches(background, "\"Input\\.dispatchKeyEvent\"").Count);
        Assert.Single(
            Regex.Matches(pulse, @"await\s+dispatchPulseKey\(tabId\);")
                .Cast<Match>());

        Assert.Contains("modifiers: 0", keyCycle, StringComparison.Ordinal);
        Assert.Contains("key: PULSE_KEY", keyCycle, StringComparison.Ordinal);
        Assert.Contains("code: PULSE_KEY_CODE", keyCycle, StringComparison.Ordinal);
        Assert.Contains(
            "windowsVirtualKeyCode: PULSE_KEY_WINDOWS_VIRTUAL_CODE",
            keyCycle,
            StringComparison.Ordinal);
        Assert.Contains("autoRepeat: false", keyCycle, StringComparison.Ordinal);
        Assert.Contains("isKeypad: false", keyCycle, StringComparison.Ordinal);
        Assert.Contains("isSystemKey: false", keyCycle, StringComparison.Ordinal);

        // One logical keyUp event lives in finally, so a failed or partially
        // completed keyDown still attempts its release before pulse cleanup.
        // Only a failed release triggers one retry of that same keyUp event.
        AssertPatternMatches(
            keyCycle,
            """
            (?s)const\s+keyUpEvent\s*=\s*\{\s*
            \.\.\.baseKeyEvent,\s*
            type:\s*"keyUp"\s*
            \};
            """);
        var outerTryIndex = keyCycle.IndexOf("try {", StringComparison.Ordinal);
        var keyDownIndex = keyCycle.IndexOf("type: \"keyDown\"", StringComparison.Ordinal);
        var finallyIndex = keyCycle.IndexOf("} finally {", StringComparison.Ordinal);
        const string keyUpDispatch =
            "sendDebuggerCommand(tabId, \"Input.dispatchKeyEvent\", keyUpEvent)";
        var firstKeyUpIndex = keyCycle.IndexOf(keyUpDispatch, StringComparison.Ordinal);
        var retryCatchIndex = keyCycle.IndexOf(
            "catch (keyUpError)",
            StringComparison.Ordinal);
        var secondKeyUpIndex = keyCycle.IndexOf(
            keyUpDispatch,
            firstKeyUpIndex + 1,
            StringComparison.Ordinal);
        var rethrowIndex = keyCycle.IndexOf("throw keyUpError;", StringComparison.Ordinal);
        Assert.True(outerTryIndex >= 0 && keyDownIndex > outerTryIndex);
        Assert.True(finallyIndex > keyDownIndex);
        Assert.True(firstKeyUpIndex > finallyIndex);
        Assert.True(retryCatchIndex > firstKeyUpIndex);
        Assert.True(secondKeyUpIndex > retryCatchIndex);
        Assert.True(rethrowIndex > secondKeyUpIndex);

        // The moved mouse event is followed by another freshly resolved
        // foreground check. The key cycle occurs only in its background arm.
        AssertPatternMatches(
            pulse,
            """
            (?s)await\s+sendDebuggerCommand\(\s*
            tabId,\s*"Input\.dispatchMouseEvent",\s*\{\s*
            \.\.\.baseEvent,\s*
            x:\s*point\.movedX,\s*
            y:\s*point\.movedY\s*
            \}\);\s*
            const\s+keyInputState\s*=\s*await\s+getLiveInputState\(tabId\);\s*
            if\s*\(\s*!keyInputState\.available\s*\)\s*\{\s*return;\s*\}\s*
            if\s*\(\s*keyInputState\.foreground\s*\)\s*
            \{\s*foregroundTransitioned\s*=\s*true;\s*\}
            \s*else\s*\{\s*
            await\s+dispatchPulseKey\(tabId\);\s*
            \}
            """);

        var keyCycleIndex = pulse.IndexOf(
            "await dispatchPulseKey(tabId);",
            StringComparison.Ordinal);
        var visibleHoldIndex = pulse.IndexOf(
            "await waitForDelay(CURSOR_VISIBLE_HOLD_MS);",
            StringComparison.Ordinal);
        Assert.True(
            keyCycleIndex >= 0 && visibleHoldIndex > keyCycleIndex,
            "The complete key cycle must finish before the five-second dwell.");
    }

    [Fact]
    public void Extension_RevalidatesLiveTargetBeforeEveryInputPhase()
    {
        var background = ReadRepositoryFile(
            "stayactive",
            "ChromeCursorInputStayAwakeExtension",
            "background.js");
        var liveState = ExtractBraceBlock(
            background,
            "async function getLiveInputState(tabId)");
        var pulse = ExtractBraceBlock(background, "async function sendCursorPulse");

        Assert.Equal(
            3,
            Regex.Matches(liveState, @"!isFeatureEnabled\(\)").Count);
        Assert.Equal(
            3,
            Regex.Matches(liveState, @"attachedTabId\s*!==\s*tabId").Count);
        Assert.Equal(
            3,
            Regex.Matches(liveState, @"navigatingTabs\.has\(tabId\)").Count);
        var firstFeatureCheck = liveState.IndexOf(
            "!isFeatureEnabled()",
            StringComparison.Ordinal);
        var tabLookup = liveState.IndexOf(
            "await getTab(tabId)",
            StringComparison.Ordinal);
        var secondFeatureCheck = liveState.IndexOf(
            "!isFeatureEnabled()",
            firstFeatureCheck + 1,
            StringComparison.Ordinal);
        var windowLookup = liveState.IndexOf(
            "await getWindow(currentTab.windowId)",
            StringComparison.Ordinal);
        var thirdFeatureCheck = liveState.IndexOf(
            "!isFeatureEnabled()",
            secondFeatureCheck + 1,
            StringComparison.Ordinal);
        Assert.True(firstFeatureCheck >= 0 && tabLookup > firstFeatureCheck);
        Assert.True(
            secondFeatureCheck > tabLookup,
            "Enabled, attachment, and navigation state must be rechecked after getTab.");
        Assert.True(windowLookup > secondFeatureCheck);
        Assert.True(
            thirdFeatureCheck > windowLookup,
            "Enabled, attachment, and navigation state must be rechecked after getWindow.");
        AssertPatternMatches(
            liveState,
            """
            (?s)try\s*\{\s*
            currentTab\s*=\s*await\s+getTab\(tabId\);\s*
            \}\s*catch\s*\{\s*
            return\s*\{\s*available:\s*false,\s*foreground:\s*true\s*\};\s*
            \}
            """);
        Assert.Contains("currentTab.id !== tabId", liveState, StringComparison.Ordinal);
        Assert.Contains(
            "currentTab.status !== \"complete\"",
            liveState,
            StringComparison.Ordinal);
        Assert.Contains("!isEligibleTab(currentTab)", liveState, StringComparison.Ordinal);
        Assert.Contains("if (!currentTab.active)", liveState, StringComparison.Ordinal);
        Assert.Contains(
            "return { available: true, foreground: false };",
            liveState,
            StringComparison.Ordinal);
        Assert.Contains(
            "typeof currentTab.windowId !== \"number\"",
            liveState,
            StringComparison.Ordinal);
        Assert.Contains(
            "await getWindow(currentTab.windowId)",
            liveState,
            StringComparison.Ordinal);
        Assert.Contains(
            "foreground: Boolean(window.focused)",
            liveState,
            StringComparison.Ordinal);
        Assert.True(
            Regex.Matches(
                liveState,
                @"return\s*\{\s*available:\s*false,\s*foreground:\s*true\s*\};").Count >= 4,
            "Every missing, stale, navigating, or lookup-error state must fail closed.");

        Assert.Equal(
            6,
            Regex.Matches(pulse, @"await\s+getLiveInputState\(tabId\)").Count);
        Assert.DoesNotContain("selection.tab.active", pulse, StringComparison.Ordinal);

        var initialStateIndex = pulse.IndexOf(
            "const initialInputState = await getLiveInputState(tabId);",
            StringComparison.Ordinal);
        var movedStateIndex = pulse.IndexOf(
            "const movedInputState = await getLiveInputState(tabId);",
            StringComparison.Ordinal);
        var keyStateIndex = pulse.IndexOf(
            "const keyInputState = await getLiveInputState(tabId);",
            StringComparison.Ordinal);
        var keyIndex = pulse.IndexOf(
            "await dispatchPulseKey(tabId);",
            StringComparison.Ordinal);
        var visibleHoldIndex = pulse.IndexOf(
            "await waitForDelay(CURSOR_VISIBLE_HOLD_MS);",
            StringComparison.Ordinal);
        var returnStateIndex = pulse.IndexOf(
            "const returnInputState = await getLiveInputState(tabId);",
            StringComparison.Ordinal);
        Assert.True(initialStateIndex >= 0);
        Assert.True(movedStateIndex > initialStateIndex);
        Assert.True(keyStateIndex > movedStateIndex);
        Assert.True(keyIndex > keyStateIndex);
        Assert.True(visibleHoldIndex > keyIndex);
        Assert.True(
            returnStateIndex > visibleHoldIndex,
            "The target must be refreshed again after the five-second dwell.");

        foreach (var stateName in new[]
                 {
                     "initialInputState",
                     "movedInputState",
                     "keyInputState",
                     "returnInputState"
                 })
        {
            Assert.Contains(
                $"if (!{stateName}.available)",
                pulse,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Extension_UsesBoundedProtocolMarkerWithFallbackAndIndependentCleanup()
    {
        var background = ReadRepositoryFile(
            "stayactive",
            "ChromeCursorInputStayAwakeExtension",
            "background.js");
        var initializeMarker = ExtractBraceBlock(
            background,
            "async function initializeCursorMarker(tabId)");
        var markerQuad = ExtractBraceBlock(
            background,
            "function markerQuadAtPoint(point)");
        var markerRect = ExtractBraceBlock(
            background,
            "function markerRectAtPoint(point)");
        var showMarker = ExtractBraceBlock(
            background,
            "async function showCursorMarker(tabId, point, markerDomains)");
        var hideMarker = ExtractBraceBlock(
            background,
            "async function hideCursorMarker(tabId)");
        var animateMarker = ExtractBraceBlock(
            background,
            "async function animateCursorMarker(tabId, point)");
        var pulse = ExtractBraceBlock(background, "async function sendCursorPulse");
        var detach = ExtractBraceBlock(background, "async function detachCurrentTab(detail)");

        Assert.InRange(
            ReadIntegerConstant(background, "CURSOR_MARKER_RADIUS_CSS_PX"),
            8,
            32);
        Assert.InRange(
            ReadIntegerConstant(background, "CURSOR_RETURN_HOLD_MS"),
            150,
            500);
        Assert.Contains(
            "const CURSOR_MARKER_COLOR = { r: 255, g: 32, b: 96, a: 0.9 };",
            background,
            StringComparison.Ordinal);
        Assert.Contains(
            "const CURSOR_MARKER_OUTLINE_COLOR = { r: 255, g: 255, b: 255, a: 1 };",
            background,
            StringComparison.Ordinal);

        // Overlay.highlightQuad is a compositor overlay rather than a DOM
        // element, so it stays above canvas/iframe content and cannot intercept
        // pointer input. Keep the implementation free of a page-injected marker.
        Assert.DoesNotContain("document.createElement", background, StringComparison.Ordinal);
        Assert.DoesNotContain("appendChild", background, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacent", background, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", background, StringComparison.Ordinal);
        Assert.DoesNotContain("outerHTML", background, StringComparison.Ordinal);
        Assert.DoesNotContain("Runtime.callFunctionOn", background, StringComparison.Ordinal);
        Assert.DoesNotContain("DOM.set", background, StringComparison.Ordinal);
        Assert.Single(
            Regex.Matches(background, "\"DOM\\.enable\"").Cast<Match>());
        Assert.Single(
            Regex.Matches(background, "\"Runtime\\.evaluate\"").Cast<Match>());
        Assert.Contains(
            "expression: FIND_INPUT_SURFACE_EXPRESSION",
            pulse,
            StringComparison.Ordinal);

        var domEnableIndex = initializeMarker.IndexOf(
            "\"DOM.enable\"",
            StringComparison.Ordinal);
        var overlayEnableIndex = initializeMarker.IndexOf(
            "\"Overlay.enable\"",
            StringComparison.Ordinal);
        Assert.True(
            domEnableIndex >= 0 && overlayEnableIndex > domEnableIndex,
            "The DOM protocol agent must be initialized before Overlay.enable.");
        Assert.Single(
            Regex.Matches(animateMarker, "initializeCursorMarker\\(").Cast<Match>());
        Assert.Single(
            Regex.Matches(pulse, "initializeCursorMarker\\(").Cast<Match>());

        Assert.Contains("\"Overlay.highlightQuad\"", showMarker, StringComparison.Ordinal);
        Assert.Contains("quad: markerQuadAtPoint(point)", showMarker, StringComparison.Ordinal);
        Assert.Contains("color: CURSOR_MARKER_COLOR", showMarker, StringComparison.Ordinal);
        Assert.Contains(
            "outlineColor: CURSOR_MARKER_OUTLINE_COLOR",
            showMarker,
            StringComparison.Ordinal);
        Assert.Contains("\"DOM.highlightRect\"", showMarker, StringComparison.Ordinal);
        Assert.Contains("...markerRectAtPoint(point)", showMarker, StringComparison.Ordinal);

        var overlayHighlightIndex = showMarker.IndexOf(
            "\"Overlay.highlightQuad\"",
            StringComparison.Ordinal);
        var rectangleFallbackIndex = showMarker.IndexOf(
            "\"DOM.highlightRect\"",
            StringComparison.Ordinal);
        Assert.True(
            overlayHighlightIndex >= 0 && rectangleFallbackIndex > overlayHighlightIndex,
            "Overlay.highlightQuad must be attempted before DOM.highlightRect.");

        // Every diamond vertex is clamped to the evaluated viewport. This
        // matters for tiny surfaces and input points close to a viewport edge.
        Assert.Contains(
            "const maxX = Math.max(0, point.viewportWidth - 1);",
            markerQuad,
            StringComparison.Ordinal);
        Assert.Contains(
            "const maxY = Math.max(0, point.viewportHeight - 1);",
            markerQuad,
            StringComparison.Ordinal);
        Assert.Contains(
            "const clampX = (value) => Math.max(0, Math.min(maxX, value));",
            markerQuad,
            StringComparison.Ordinal);
        Assert.Contains(
            "const clampY = (value) => Math.max(0, Math.min(maxY, value));",
            markerQuad,
            StringComparison.Ordinal);
        AssertPatternMatches(
            markerQuad,
            """
            (?s)return\s*\[\s*
            clampX\(point\.x\),\s*clampY\(point\.y\s*-\s*radius\),\s*
            clampX\(point\.x\s*\+\s*radius\),\s*clampY\(point\.y\),\s*
            clampX\(point\.x\),\s*clampY\(point\.y\s*\+\s*radius\),\s*
            clampX\(point\.x\s*-\s*radius\),\s*clampY\(point\.y\)\s*
            \];
            """);

        // The rectangle fallback uses bounded integer protocol coordinates.
        Assert.Contains(
            "Math.floor(point.viewportWidth - 1)",
            markerRect,
            StringComparison.Ordinal);
        Assert.Contains(
            "Math.floor(point.viewportHeight - 1)",
            markerRect,
            StringComparison.Ordinal);
        Assert.Contains("Math.floor(point.x - radius)", markerRect, StringComparison.Ordinal);
        Assert.Contains("Math.floor(point.y - radius)", markerRect, StringComparison.Ordinal);
        Assert.Contains("Math.ceil(point.x + radius)", markerRect, StringComparison.Ordinal);
        Assert.Contains("Math.ceil(point.y + radius)", markerRect, StringComparison.Ordinal);
        Assert.Contains("width: Math.max(1, right - left)", markerRect, StringComparison.Ordinal);
        Assert.Contains("height: Math.max(1, bottom - top)", markerRect, StringComparison.Ordinal);

        // Each cleanup command has its own failure boundary. A failure in one
        // protocol domain must never prevent the remaining cleanup attempts.
        Assert.Equal(4, Regex.Matches(hideMarker, @"\btry\s*\{").Count);
        Assert.Equal(4, Regex.Matches(hideMarker, @"\bcatch\s*\{").Count);
        AssertPatternMatches(
            hideMarker,
            """
            (?s)try\s*\{.*?"Overlay\.hideHighlight".*?\}\s*catch\s*\{.*?\}
            \s*try\s*\{.*?"DOM\.hideHighlight".*?\}\s*catch\s*\{.*?\}
            \s*try\s*\{.*?"Overlay\.disable".*?\}\s*catch\s*\{.*?\}
            \s*try\s*\{.*?"DOM\.disable".*?\}\s*catch\s*\{
            """);

        // The marker follows base -> moved -> base for both visual-only and
        // background-input sequences and is always hidden in finally.
        Assert.Equal(
            3,
            Regex.Matches(animateMarker, "await\\s+showCursorMarker\\(").Count);
        Assert.Equal(
            3,
            Regex.Matches(pulse, "await\\s+showCursorMarker\\(").Count);
        Assert.Contains("let markerShown = true;", animateMarker, StringComparison.Ordinal);
        Assert.Contains("let markerShown = true;", pulse, StringComparison.Ordinal);
        Assert.Equal(
            3,
            Regex.Matches(
                animateMarker,
                @"\)\)\s*&&\s*markerShown").Count);
        Assert.Equal(
            3,
            Regex.Matches(
                pulse,
                @"\)\)\s*&&\s*markerShown").Count);
        Assert.Contains("x: point.movedX", animateMarker, StringComparison.Ordinal);
        Assert.Contains("y: point.movedY", animateMarker, StringComparison.Ordinal);
        Assert.Contains(
            "await waitForDelay(CURSOR_ESTABLISH_DELAY_MS);",
            animateMarker,
            StringComparison.Ordinal);
        Assert.Contains(
            "await waitForDelay(CURSOR_VISIBLE_HOLD_MS);",
            animateMarker,
            StringComparison.Ordinal);
        Assert.Contains(
            "await waitForDelay(CURSOR_RETURN_HOLD_MS);",
            animateMarker,
            StringComparison.Ordinal);
        AssertPatternMatches(
            animateMarker,
            """
            (?s)\}\s*finally\s*\{\s*
            await\s+hideCursorMarker\(tabId\);\s*
            \}
            """);

        var hideBeforeDetachIndex = detach.IndexOf(
            "await hideCursorMarker(tabId);",
            StringComparison.Ordinal);
        var debuggerDetachIndex = detach.IndexOf(
            "await detachDebugger(tabId);",
            StringComparison.Ordinal);
        Assert.True(
            hideBeforeDetachIndex >= 0 && debuggerDetachIndex > hideBeforeDetachIndex,
            "The compositor marker must be hidden before detaching the debugger.");
    }

    [Fact]
    public void Extension_ForegroundRunsVisualPreviewWithoutInputAndReportsTruthfully()
    {
        var background = ReadRepositoryFile(
            "stayactive",
            "ChromeCursorInputStayAwakeExtension",
            "background.js");
        var detailAllowlist = Regex.Match(
            background,
            @"const\s+STATUS_DETAIL_VALUES\s*=\s*new\s+Set\(\s*\[(?<details>[\s\S]*?)\]\s*\);");
        Assert.True(detailAllowlist.Success, "STATUS_DETAIL_VALUES was not found.");
        foreach (var detail in new[]
                 {
                     "target_foreground_visualized",
                     "target_foreground_marker_unavailable",
                     "visual_marker_shown",
                     "visual_marker_unavailable"
                 })
        {
            Assert.Contains(
                $"\"{detail}\"",
                detailAllowlist.Groups["details"].Value,
                StringComparison.Ordinal);
        }

        var pulse = ExtractBraceBlock(background, "async function sendCursorPulse");
        var animateMarker = ExtractBraceBlock(
            background,
            "async function animateCursorMarker(tabId, point)");
        var foregroundBranch = ExtractBraceBlock(
            pulse,
            "if (foregroundBeforeEvaluation || stateAfterEvaluation.foreground)");
        var firstForegroundCheck = pulse.IndexOf(
            "const stateBeforeEvaluation = await getLiveInputState(tabId);",
            StringComparison.Ordinal);
        var evaluation = pulse.IndexOf("\"Runtime.evaluate\"", StringComparison.Ordinal);
        var secondForegroundCheck = pulse.IndexOf(
            "const stateAfterEvaluation = await getLiveInputState(tabId);",
            StringComparison.Ordinal);
        var firstInput = pulse.IndexOf("\"Input.dispatchMouseEvent\"", StringComparison.Ordinal);

        Assert.True(firstForegroundCheck >= 0);
        Assert.True(evaluation > firstForegroundCheck);
        Assert.True(secondForegroundCheck > evaluation);
        Assert.True(firstInput > secondForegroundCheck);
        Assert.Contains(
            "if (!stateBeforeEvaluation.available)",
            pulse,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!stateAfterEvaluation.available)",
            pulse,
            StringComparison.Ordinal);

        Assert.Contains(
            "const markerShown = await animateCursorMarker(tabId, point);",
            foregroundBranch,
            StringComparison.Ordinal);
        Assert.Contains(
            "reportStatus(",
            foregroundBranch,
            StringComparison.Ordinal);
        Assert.Contains("\"pulse_skipped\"", foregroundBranch, StringComparison.Ordinal);
        Assert.Contains(
            "\"target_foreground_visualized\"",
            foregroundBranch,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"target_foreground_marker_unavailable\"",
            foregroundBranch,
            StringComparison.Ordinal);
        Assert.Contains("return;", foregroundBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.", foregroundBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.", animateMarker, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "dispatchPulseKey",
            foregroundBranch,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "dispatchPulseKey",
            animateMarker,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"pulsed\"", foregroundBranch, StringComparison.Ordinal);
        Assert.Equal(
            3,
            Regex.Matches(animateMarker, "await\\s+showCursorMarker\\(").Count);

        var foregroundBranchIndex = pulse.IndexOf(
            foregroundBranch,
            StringComparison.Ordinal);
        Assert.True(
            firstInput > foregroundBranchIndex + foregroundBranch.Length,
            "All input dispatches must remain after the foreground visual-only branch.");
        Assert.True(
            Regex.Matches(pulse, @"await\s+getLiveInputState\(tabId\)").Count == 6,
            "Foreground state must be refreshed before evaluation, before each input phase, and after the dwell.");

        Assert.Contains(
            "markerShown ? \"visual_marker_shown\" : \"visual_marker_unavailable\"",
            pulse,
            StringComparison.Ordinal);
        Assert.Single(
            Regex.Matches(
                    pulse,
                    @"reportStatus\(\s*""pulsed""")
                .Cast<Match>());
    }

    [Fact]
    public void Extension_MultipleEligibleTargetsFailClosed()
    {
        var background = ReadRepositoryFile(
            "stayactive",
            "ChromeCursorInputStayAwakeExtension",
            "background.js");
        var selection = ExtractBraceBlock(
            background,
            "async function findSingleEligibleTab()");
        Assert.Contains(
            "if (eligibleTabs.length !== 1)",
            selection,
            StringComparison.Ordinal);
        Assert.Contains(
            "return { tab: null, detail: \"multiple_eligible_tabs\" };",
            selection,
            StringComparison.Ordinal);

        var reconcile = ExtractBraceBlock(background, "async function reconcileTarget(pulseAfterAttach)");
        AssertPatternMatches(
            reconcile,
            """
            (?s)if\s*\(!selection\.tab\)\s*\{\s*
            await\s+detachCurrentTab\(selection\.detail\);\s*
            reportStatus\("waiting",\s*selection\.detail\);\s*
            return;
            """);

        var pulse = ExtractBraceBlock(background, "async function sendCursorPulse");
        AssertPatternMatches(
            pulse,
            """
            (?s)if\s*\(!selection\.tab\s*\|\|\s*selection\.tab\.id\s*!==\s*tabId\)\s*\{\s*
            await\s+detachCurrentTab\(
            """);
    }

    private static string ReadRepositoryFile(params string[] relativePathParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                new[] { current.FullName }
                    .Concat(relativePathParts)
                    .ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativePathParts)} from {AppContext.BaseDirectory}.");
    }

    private static int ReadIntegerConstant(string source, string name)
    {
        var match = Regex.Match(
            source,
            $@"const\s+{Regex.Escape(name)}\s*=\s*(?<value>\d[\d_]*)\s*;");
        Assert.True(match.Success, $"Integer constant was not found: {name}");
        return int.Parse(match.Groups["value"].Value.Replace("_", string.Empty));
    }

    private static void AssertPatternMatches(string source, string pattern)
    {
        Assert.Matches(
            new Regex(
                pattern,
                RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace),
            source);
    }

    private static string ExtractBraceBlock(string source, string declaration)
    {
        var declarationIndex = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(declarationIndex >= 0, $"Declaration was not found: {declaration}");

        var openingBraceIndex = source.IndexOf('{', declarationIndex);
        Assert.True(openingBraceIndex >= 0, $"Opening brace was not found: {declaration}");

        var depth = 0;
        for (var index = openingBraceIndex; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source[declarationIndex..(index + 1)];
                    }
                    break;
            }
        }

        throw new InvalidOperationException($"Closing brace was not found: {declaration}");
    }
}
