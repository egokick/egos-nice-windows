import sqlite3


database = r"C:\ProgramData\NordVPN\settings.db"
connection = sqlite3.connect(database, timeout=30)
try:
    connection.execute("BEGIN IMMEDIATE")
    cursor = connection.execute(
        "UPDATE Settings SET Value = 'False' WHERE Key = 'SplitTunneling:IsEnabled'"
    )
    if cursor.rowcount != 1:
        raise RuntimeError("NordVPN split-tunneling setting was not found exactly once")
    connection.commit()
finally:
    connection.close()
