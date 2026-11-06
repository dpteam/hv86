@echo off
setlocal
rem hv86 - запуск интерфейса. Если exe ещё не собран - собираем.
set "ROOT=%~dp0.."

if not exist "%ROOT%\bin\hv86.exe" (
  call "%~dp0build.cmd" || exit /b 1
)

start "" "%ROOT%\bin\hv86.exe" %*
exit /b 0