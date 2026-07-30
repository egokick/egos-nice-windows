#!/usr/bin/env python3
"""Keep an in-container browser RDP canvas active without host input."""

from __future__ import annotations

import itertools
import json
import time
import urllib.request

import websocket


DEVTOOLS_HTTP = "http://127.0.0.1:9222"
INTERVAL_SECONDS = 55
TARGET_URL_PARTS = (
    "windows365.microsoft.com",
    "rdweb.wvd.microsoft.com",
    "client.wvd.microsoft.com",
)
directions = itertools.cycle((-1, 1))


def get_json(path: str) -> object:
    with urllib.request.urlopen(
        DEVTOOLS_HTTP + path,
        timeout=5,
    ) as response:
        return json.load(response)


def evaluate(socket: websocket.WebSocket, request_id: int, expression: str) -> object:
    socket.send(
        json.dumps(
            {
                "id": request_id,
                "method": "Runtime.evaluate",
                "params": {
                    "expression": expression,
                    "returnByValue": True,
                    "awaitPromise": True,
                },
            }
        )
    )
    while True:
        message = json.loads(socket.recv())
        if message.get("id") != request_id:
            continue
        return (
            message.get("result", {})
            .get("result", {})
            .get("value")
        )


def dispatch_move(
    socket: websocket.WebSocket,
    request_id: int,
    x: float,
    y: float,
) -> None:
    socket.send(
        json.dumps(
            {
                "id": request_id,
                "method": "Input.dispatchMouseEvent",
                "params": {
                    "type": "mouseMoved",
                    "x": x,
                    "y": y,
                    "button": "none",
                    "buttons": 0,
                    "pointerType": "mouse",
                },
            }
        )
    )
    while True:
        message = json.loads(socket.recv())
        if message.get("id") == request_id:
            return


CANVAS_EXPRESSION = r"""
(() => {
  const candidates = Array.from(document.querySelectorAll("canvas"))
    .map(canvas => {
      const rect = canvas.getBoundingClientRect();
      return {
        x: rect.left,
        y: rect.top,
        width: rect.width,
        height: rect.height,
        visible:
          rect.width >= 200 &&
          rect.height >= 150 &&
          rect.bottom > 0 &&
          rect.right > 0 &&
          rect.top < innerHeight &&
          rect.left < innerWidth
      };
    })
    .filter(item => item.visible)
    .sort((a, b) => (b.width * b.height) - (a.width * a.height));
  if (!candidates.length) return null;
  const rect = candidates[0];
  return {
    x: Math.max(1, Math.min(innerWidth - 2, rect.x + rect.width / 2)),
    y: Math.max(1, Math.min(innerHeight - 2, rect.y + rect.height / 2))
  };
})()
"""


def tick() -> bool:
    targets = get_json("/json/list")
    if not isinstance(targets, list):
        return False

    pages = [
        target
        for target in targets
        if target.get("type") == "page"
        and target.get("webSocketDebuggerUrl")
        and any(part in target.get("url", "") for part in TARGET_URL_PARTS)
    ]
    for page in pages:
        socket = websocket.create_connection(
            page["webSocketDebuggerUrl"],
            origin=DEVTOOLS_HTTP,
            timeout=5,
        )
        try:
            point = evaluate(socket, 1, CANVAS_EXPRESSION)
            if not isinstance(point, dict):
                continue
            offset = next(directions)
            dispatch_move(socket, 2, point["x"] + offset, point["y"])
            time.sleep(0.15)
            dispatch_move(socket, 3, point["x"], point["y"])
            print(
                f"Injected page-scoped RDP keepalive at "
                f"{time.strftime('%Y-%m-%d %H:%M:%S')}",
                flush=True,
            )
            return True
        finally:
            socket.close()
    return False


def main() -> None:
    while True:
        try:
            tick()
        except Exception as error:  # bounded retry loop; supervisor stays stable
            print(f"Keepalive retry: {error}", flush=True)
        time.sleep(INTERVAL_SECONDS)


if __name__ == "__main__":
    main()

