@echo off
setlocal

set "ROOT=%~dp0"
set "PROJECT=%ROOT%src\OllamaCloudProxy\OllamaCloudProxy.csproj"
set "OUT=%ROOT%artifacts"
set "RID=win-x64"

echo Publishing self-contained single-file binary (%RID%) to %OUT% ...

dotnet publish "%PROJECT%" ^
  -c Release ^
  -r %RID% ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o "%OUT%"

if errorlevel 1 (
  echo Publish failed.
  exit /b 1
)

echo.
echo Done: %OUT%\ollama-cloud-proxy.exe
endlocal
