Diese ZIP enthält nur die C# Dateien zum Ersetzen/Einfügen:

- Program.cs
- MainForm.cs (links: Start/Stop/Restart/Open App/Logs/Erweitert…)
- AdvancedForm.cs (Frames + Status via check_*.bat + Konsole)
- Services/ScriptRunner.cs (stream + capture stdout/stderr)
- Services/StatusChecker.cs
- Services/Models.cs

Wichtig:
- AdvancedForm erwartet diese Scripts (werden per cmd.exe ausgeführt):
  launcher\check_install.bat
  launcher\check_firewall.bat (einzeiliges JSON)
  launcher\check_task.bat

Hinweis:
- Program.cs referenziert Watchdog.RunAsync(...) wie in deinem Projekt.
