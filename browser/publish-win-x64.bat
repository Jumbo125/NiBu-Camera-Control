@echo off
setlocal
dotnet restore
if errorlevel 1 exit /b 1
dotnet publish -c Release -r win-x64 --self-contained false
