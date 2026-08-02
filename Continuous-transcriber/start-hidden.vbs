Option Explicit

Dim shell, fso, appDirectory, command, argument
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
appDirectory = fso.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = appDirectory

command = Quote(appDirectory & "\start-hidden.bat")
For Each argument In WScript.Arguments
    command = command & " " & Quote(CStr(argument))
Next

shell.Run command, 0, False

Function Quote(value)
    Quote = """" & Replace(value, """", """""") & """"
End Function
