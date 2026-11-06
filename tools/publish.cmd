@echo off
setlocal
rem hv86 - публикация: self-contained single-file, x64.
rem .NET вшивается, чтобы рядом с exe не было dll-хвоста.
set "ROOT=%~dp0.."
pushd "%ROOT%"

if not exist "lib\webview2\Microsoft.Web.WebView2.Core.dll" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "tools\fetch-webview2.ps1" || (popd & exit /b 1)
)

if not exist "assets\v86\0.5.473\libv86.js" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "tools\fetch-v86.ps1" || (popd & exit /b 1)
)

dotnet publish hv86.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None ^
  -o publish

set "CODE=%ERRORLEVEL%"
popd
exit /b %CODE%