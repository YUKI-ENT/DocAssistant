@echo off
setlocal
set "workspace=%~dp0.."
dotnet publish "%workspace%\DocAssistant\DocAssistant.csproj" -c Release -p:PublishProfile=Windows-x86 -p:Platform=x86
if errorlevel 1 (
    echo Single-file publish failed.
    exit /b 1
)
set "exe=%workspace%\publish\DocAssistant\DocAssistant.exe"
if not exist "%exe%" (
    echo Published executable was not found.
    exit /b 1
)
echo Published: %exe%
exit /b 0
