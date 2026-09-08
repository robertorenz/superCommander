@echo off
setlocal
rem ---------------------------------------------------------------------------
rem  Builds SuperCommander and refreshes run\SuperCommander.exe.
rem
rem  Produces the self-contained single file, so the result runs on a machine
rem  with no .NET installed. Pass "fd" for the small framework-dependent build
rem  instead, which needs the .NET 9 Desktop Runtime.
rem ---------------------------------------------------------------------------

set "ROOT=%~dp0"
set "PROJECT=%ROOT%src\SuperCommander\SuperCommander.csproj"
set "STAGE=%ROOT%artifacts\run"
set "TARGET=%ROOT%run"

if /i "%~1"=="fd" (
    set "SELFCONTAINED=false"
    set "EXTRA="
    echo Building framework-dependent ^(needs .NET 9 Desktop Runtime^)...
) else (
    set "SELFCONTAINED=true"
    set "EXTRA=-p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true"
    echo Building self-contained ^(no .NET needed^)...
)

taskkill /IM SuperCommander.exe /F >nul 2>&1

dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained %SELFCONTAINED% ^
    -p:PublishSingleFile=true -p:DebugType=none %EXTRA% -o "%STAGE%" --nologo
if errorlevel 1 (
    echo.
    echo Build failed.
    exit /b 1
)

if not exist "%TARGET%" mkdir "%TARGET%"
copy /y "%STAGE%\SuperCommander.exe" "%TARGET%\SuperCommander.exe" >nul
if errorlevel 1 (
    echo Could not copy into run\ - is SuperCommander still running?
    exit /b 1
)

echo.
echo Ready: %TARGET%\SuperCommander.exe
for %%F in ("%TARGET%\SuperCommander.exe") do echo Size:  %%~zF bytes
endlocal
