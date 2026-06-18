#Requires AutoHotkey v2.0
#SingleInstance Force

; Keep the browser RDP tab focused inside the VM before starting this script.
; Ctrl+Alt+P pauses/resumes. Ctrl+Alt+Q exits.

intervalMs := 240000
stepPx := 1
direction := 1

SetTimer KeepAlive, intervalMs
KeepAlive()
TrayTip "WorkVM keepalive", "Running. Focus the browser RDP tab inside the VM.", 5

KeepAlive() {
    global stepPx, direction

    MouseGetPos &x, &y
    MouseMove x + (stepPx * direction), y, 0
    Sleep 200
    MouseMove x, y, 0

    direction := direction * -1
}

^!p::Pause -1
^!q::ExitApp
