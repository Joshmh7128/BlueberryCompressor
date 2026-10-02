@echo off
rem Builds a ready-to-run AutoCompressor.exe into the "publish" folder.
rem Needs the .NET 9 SDK. The result needs the .NET 9 Desktop Runtime and ffmpeg to run.
setlocal
cd /d "%~dp0"
dotnet publish src\AutoCompressor.Desktop -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o publish -nologo
if errorlevel 1 exit /b 1
echo.
echo Built: %~dp0publish\AutoCompressor.exe
