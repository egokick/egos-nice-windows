#!/usr/bin/env python3
"""Prove that the running Chrome process can see hybrid-passkey Bluetooth."""

from __future__ import annotations

import json
import sys
import time
import urllib.request

import websocket


DEVTOOLS_HTTP = "http://127.0.0.1:9222"
TEST_URL = "http://localhost:8000/"


def get_json(path: str, method: str = "GET") -> object:
    request = urllib.request.Request(DEVTOOLS_HTTP + path, method=method)
    with urllib.request.urlopen(request, timeout=5) as response:
        return json.load(response)


def send_command(
    socket: websocket.WebSocket,
    request_id: int,
    method: str,
    params: dict[str, object],
) -> dict[str, object]:
    socket.send(
        json.dumps(
            {
                "id": request_id,
                "method": method,
                "params": params,
            }
        )
    )
    while True:
        response = json.loads(socket.recv())
        if response.get("id") != request_id:
            continue
        if response.get("error"):
            raise RuntimeError(
                f"Chrome DevTools {method} failed: {response['error']}"
            )
        result = response.get("result", {})
        return result if isinstance(result, dict) else {}


def create_background_test_page() -> tuple[
    websocket.WebSocket,
    str,
    dict[str, object],
]:
    version = get_json("/json/version")
    if (
        not isinstance(version, dict)
        or not version.get("webSocketDebuggerUrl")
    ):
        raise RuntimeError("Chrome returned no browser DevTools endpoint.")

    browser_socket = websocket.create_connection(
        str(version["webSocketDebuggerUrl"]),
        origin=DEVTOOLS_HTTP,
        timeout=5,
    )
    target_id = ""
    try:
        created = send_command(
            browser_socket,
            1,
            "Target.createTarget",
            {"url": TEST_URL, "background": True},
        )
        target_id = str(created.get("targetId", ""))
        if not target_id:
            raise RuntimeError("Chrome did not create a background test target.")

        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            targets = get_json("/json/list")
            if isinstance(targets, list):
                for target in targets:
                    if (
                        isinstance(target, dict)
                        and target.get("id") == target_id
                        and target.get("webSocketDebuggerUrl")
                        and str(target.get("url", "")).startswith(TEST_URL)
                    ):
                        return browser_socket, target_id, target
            time.sleep(0.2)
        raise RuntimeError("Chrome background test target was not inspectable.")
    except Exception:
        if target_id:
            try:
                send_command(
                    browser_socket,
                    2,
                    "Target.closeTarget",
                    {"targetId": target_id},
                )
            except Exception:
                pass
        browser_socket.close()
        raise


def evaluate_capabilities(target: dict[str, object]) -> dict[str, object]:
    socket = websocket.create_connection(
        str(target["webSocketDebuggerUrl"]),
        origin=DEVTOOLS_HTTP,
        timeout=5,
    )
    try:
        socket.send(
            json.dumps(
                {
                    "id": 1,
                    "method": "Runtime.evaluate",
                    "params": {
                        "expression": r"""
(async () => {
  if (!window.PublicKeyCredential ||
      typeof PublicKeyCredential.getClientCapabilities !== "function") {
    return { error: "getClientCapabilities is unavailable" };
  }
  const capabilities =
    await PublicKeyCredential.getClientCapabilities();
  let webBluetoothAvailability = null;
  if (navigator.bluetooth &&
      typeof navigator.bluetooth.getAvailability === "function") {
    webBluetoothAvailability =
      await navigator.bluetooth.getAvailability();
  }
  return {
    origin: location.origin,
    secureContext: isSecureContext,
    hybridTransport: capabilities.hybridTransport === true,
    webBluetoothAvailability
  };
})()
""",
                        "awaitPromise": True,
                        "returnByValue": True,
                    },
                }
            )
        )
        while True:
            response = json.loads(socket.recv())
            if response.get("id") != 1:
                continue
            if response.get("result", {}).get("exceptionDetails"):
                raise RuntimeError("Chrome capability evaluation threw an exception.")
            value = (
                response.get("result", {})
                .get("result", {})
                .get("value")
            )
            if not isinstance(value, dict):
                raise RuntimeError("Chrome returned no capability object.")
            return value
    finally:
        socket.close()


def main() -> int:
    timeout_seconds = int(sys.argv[1]) if len(sys.argv) > 1 else 25
    deadline = time.monotonic() + timeout_seconds
    last_error = "Chrome capability check did not run."

    while time.monotonic() < deadline:
        browser_socket = None
        target_id = ""
        try:
            # A temporary background target makes the origin deterministic
            # without changing the user's active work tab.
            browser_socket, target_id, target = create_background_test_page()
            result = evaluate_capabilities(target)
            if (
                result.get("origin") != "http://localhost:8000"
                or result.get("secureContext") is not True
            ):
                last_error = (
                    "Chrome background test origin is not ready: "
                    + json.dumps(result, sort_keys=True)
                )
            elif result.get("hybridTransport") is not True:
                last_error = (
                    "Chrome reports hybridTransport=false: "
                    + json.dumps(result, sort_keys=True)
                )
            elif result.get("webBluetoothAvailability") is False:
                last_error = (
                    "Chrome Web Bluetooth reports the adapter unavailable: "
                    + json.dumps(result, sort_keys=True)
                )
            else:
                print(
                    "STAYACTIVE_CHROME_HYBRID_READY "
                    + json.dumps(result, sort_keys=True)
                )
                return 0
        except Exception as error:
            last_error = str(error)
        finally:
            if browser_socket is not None:
                if target_id:
                    try:
                        send_command(
                            browser_socket,
                            2,
                            "Target.closeTarget",
                            {"targetId": target_id},
                        )
                    except Exception:
                        pass
                browser_socket.close()
        time.sleep(1)

    print(last_error, file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
