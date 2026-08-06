import os
import sqlite3


DATABASE = r"C:\ProgramData\NordVPN\settings.db"
APPS = (
    ("tailscaled", r"C:\Program Files\Tailscale\tailscaled.exe"),
    ("tailscale-ipn", r"C:\Program Files\Tailscale\tailscale-ipn.exe"),
    ("tailscale", r"C:\Program Files\Tailscale\tailscale.exe"),
    ("Opticon", r"C:\Program Files\Taildesk\Admin\Opticon.exe"),
    ("opticon-cli", r"C:\Program Files\Taildesk\Admin\Cli\opticon.exe"),
    ("ssh", os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "System32", "OpenSSH", "ssh.exe")),
    ("rustdesk", r"C:\Program Files\RustDesk\rustdesk.exe"),
)


connection = sqlite3.connect(DATABASE, timeout=30)
try:
    connection.execute("BEGIN IMMEDIATE")
    required = {
        "SplitTunneling:IsEnabled": "True",
        "SplitTunneling:Mode": "VpnDisabledForApps",
    }
    for key, value in required.items():
        cursor = connection.execute(
            "UPDATE Settings SET Value = ? WHERE Key = ?", (value, key)
        )
        if cursor.rowcount != 1:
            raise RuntimeError(f"NordVPN setting was not found exactly once: {key}")

    connection.execute("DELETE FROM Settings WHERE Key LIKE 'SplitTunneling:Apps:%'")
    for index, (name, path) in enumerate(APPS):
        values = {
            f"SplitTunneling:Apps:{index}:Name": name,
            f"SplitTunneling:Apps:{index}:Path": path,
            f"SplitTunneling:Apps:{index}:StartupArgs": "",
            f"SplitTunneling:Apps:{index}:AppType": "Native",
        }
        connection.executemany(
            "INSERT INTO Settings (Key, Value) VALUES (?, ?)", values.items()
        )
    connection.commit()
finally:
    connection.close()
