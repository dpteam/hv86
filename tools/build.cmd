@echo off
setlocal
rem hv86 - сборка (debug). Требуется .NET 9 SDK, WebView2 SDK в lib\webview2
rem и ассеты v86 в assets\v86 (tools\fetch-webview2.ps1, tools\fetch-v86.ps1).
set "ROOT=%~dp0.."
pushd "%ROOT%"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [ERROR] dotnet not found in PATH
  popd
  exit /b 1
)

if not exist "lib\webview2\Microsoft.Web.WebView2.Core.dll" (
  echo [1/2] fetching WebView2 SDK ...
  powershell -NoProfile -ExecutionPolicy Bypass -File "tools\fetch-webview2.ps1" || (popd & exit /b 1)
)

if not exist "assets\v86\0.5.473\libv86.js" (
  echo [2/2] fetching v86 ...
  powershell -NoProfile -ExecutionPolicy Bypass -File "tools\fetch-v86.ps1" || (popd & exit /b 1)
)

rem headless-движок опционален: без него ВМ работают только в сессии пользователя
if exist "engine\package.json" (
  if not exist "engine\node_modules\v86" (
    where node >nul 2>nul && (
      echo [3/3] installing headless engine ...
      powershell -NoProfile -ExecutionPolicy Bypass -File "tools\fetch-engine.ps1"
    )
  )
)

dotnet build hv86.csproj -c Debug -v minimal
set "CODE=%ERRORLEVEL%"

popd
exit /b %CODE%