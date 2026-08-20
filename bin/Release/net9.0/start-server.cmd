@echo off
REM Starts the quiz web app on the LAN.
REM Students open http://<this-computer-ip>:5000 in their browser (no internet needed).
cd /d "%~dp0"
if exist QuizWeb.exe (
  QuizWeb.exe --urls http://0.0.0.0:5000
) else (
  echo QuizWeb.exe not found. Publish it first:
  echo   dotnet publish -c Release -o publish
  pause
)
