@echo off
rem Builds (C#) and launches Rimworld Remade. Extra arguments go to the game, e.g.  Play.bat --windowed
powershell -ExecutionPolicy Bypass -File "%~dp0tools\run.ps1" %*
