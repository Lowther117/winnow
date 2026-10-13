@echo off
rem Build Winnow.exe - one self-contained file that runs on any Windows 11 PC
rem with nothing installed.
rem
rem   build-exe.bat           64-bit Intel/AMD build (or ARM64 on an ARM PC)
rem   build-exe.bat x64       force the Intel/AMD build
rem   build-exe.bat arm64     force the ARM64 build (Snapdragon PCs)
rem
rem Output: dist\Winnow.exe. Copy it anywhere and double-click it. It asks for
rem administrator rights once at start-up (UAC) - debloating needs them - and
rem keeps its settings, change journal and logs beside the exe.
rem
rem What this script installs: the .NET 10 SDK, into a .dotnet folder inside
rem this repo, only if the PC does not already have one. Nothing is installed
rem system-wide and no admin rights are needed to build. The SDK's own
rem telemetry is switched off for the build (DOTNET_CLI_TELEMETRY_OPTOUT).
rem
rem The noisy output of dotnet goes to build-win-log.txt so this window stays
rem readable; if anything fails, the tail of that log is shown here. The
rem finished exe runs its self-test before this script says it worked.
rem
rem dist\ is never wiped, so a rebuild keeps winnow-settings.json and
rem winnow-journal.json (your undo history) beside the new exe.

setlocal
cd /d "%~dp0"
set "HERE=%~dp0"
set "LOG=%HERE%build-win-log.txt"
set "DIST=%HERE%dist"
set "EXE=%DIST%\Winnow.exe"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_GENERATE_ASPNET_CERTIFICATE=false"

set "RID=win-x64"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "RID=win-arm64"
if /i "%~1"=="x64" set "RID=win-x64"
if /i "%~1"=="arm64" set "RID=win-arm64"

echo Winnow build> "%LOG%"
echo %DATE% %TIME%>> "%LOG%"
echo target %RID%>> "%LOG%"
echo.
echo Building Winnow.exe for %RID%. The first build takes a few minutes.
echo Detail goes to build-win-log.txt.

rem ---------------------------------------------------------------------
rem 1. A .NET 10 SDK - the repo's own copy, the PC's, or a fresh local one.
rem ---------------------------------------------------------------------
echo.
echo == .NET 10 SDK
set "DOTNET="
if exist "%HERE%.dotnet\dotnet.exe" (
    "%HERE%.dotnet\dotnet.exe" --list-sdks 2>nul | findstr /b "10." >nul && set "DOTNET=%HERE%.dotnet\dotnet.exe"
)
if defined DOTNET goto :havesdk

for /f "delims=" %%d in ('where dotnet 2^>nul') do (
    if not defined DOTNET (
        "%%d" --list-sdks 2>nul | findstr /b "10." >nul && set "DOTNET=%%d"
    )
)
if defined DOTNET goto :havesdk

echo    No .NET 10 SDK on this PC - fetching one into .dotnet\ ^(about 250 MB^).
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; $s=Join-Path $env:TEMP 'dotnet-install.ps1'; Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $s; & $s -Channel 10.0 -InstallDir '%HERE%.dotnet' -NoPath" >> "%LOG%" 2>&1
if exist "%HERE%.dotnet\dotnet.exe" set "DOTNET=%HERE%.dotnet\dotnet.exe"
if not defined DOTNET (
    echo    ERROR: could not download the .NET 10 SDK.
    echo    Check the internet connection, or install it from
    echo    https://dotnet.microsoft.com/download/dotnet/10.0 and run this again.
    goto :failed
)

:havesdk
echo    Using %DOTNET%
"%DOTNET%" --version >> "%LOG%" 2>&1

rem ---------------------------------------------------------------------
rem 2. Build. A running Winnow.exe would lock the file, so say so first.
rem ---------------------------------------------------------------------
tasklist /FI "IMAGENAME eq Winnow.exe" 2>nul | findstr /i "Winnow.exe" >nul
if not errorlevel 1 (
    echo.
    echo    Winnow is running - close it first, then run this again.
    goto :failed
)

echo.
echo == Building ^(restores packages, compiles, packs one exe^)
echo ---- dotnet publish ---->> "%LOG%"
"%DOTNET%" publish "%HERE%Winnow.csproj" -c Release -r %RID% --self-contained true -o "%DIST%" >> "%LOG%" 2>&1
if errorlevel 1 (
    echo    Build failed. The compiler's messages are at the end of the log:
    goto :failed
)
if not exist "%EXE%" (
    echo    The build finished but %EXE% is not there.
    goto :failed
)

rem ---------------------------------------------------------------------
rem 3. Prove it runs before saying it works. Winnow is a windowed app, so
rem the self-test writes winnow-selftest.txt beside the exe and that is
rem shown here. The self-test does not ask for admin rights.
rem ---------------------------------------------------------------------
rem An ARM64 exe cannot run on an Intel/AMD PC, so its self-test is skipped there.
set "HOSTARCH=%PROCESSOR_ARCHITECTURE%"
if defined PROCESSOR_ARCHITEW6432 set "HOSTARCH=%PROCESSOR_ARCHITEW6432%"
if /i "%RID%"=="win-arm64" if /i not "%HOSTARCH%"=="ARM64" (
    echo.
    echo == Self-test skipped: an ARM64 exe cannot run on this %HOSTARCH% PC.
    echo ---- selftest skipped ^(arm64 exe on %HOSTARCH% host^) ---->> "%LOG%"
    goto :done
)

echo.
echo == Testing the exe
if exist "%DIST%\winnow-selftest.txt" del /q "%DIST%\winnow-selftest.txt"
echo ---- selftest ---->> "%LOG%"
start "Winnow self-test" /wait "%EXE%" selftest
if not exist "%DIST%\winnow-selftest.txt" (
    echo    The exe did not produce a self-test report - it could not start.
    echo    Usually Defender or a policy refusing an unsigned, never-seen
    echo    program: Windows Security, Protection history will name it.
    goto :failed
)
type "%DIST%\winnow-selftest.txt"
type "%DIST%\winnow-selftest.txt" >> "%LOG%"
findstr /c:"RESULT: all checks passed" "%DIST%\winnow-selftest.txt" >nul
if errorlevel 1 goto :selftestbad

:done
echo.
echo Done: %EXE%
echo.
echo   Double-click Winnow.exe. It asks for administrator rights once.
echo   Settings, the undo journal and logs are kept beside it.
echo.
echo Log: %LOG%
if not defined CI pause
exit /b 0

:selftestbad
echo.
echo The exe runs, but the self-test above found problems.
echo Send build-win-log.txt if you want it looked at.
echo.
if not defined CI pause
exit /b 1

:failed
echo.
echo ---- last 40 lines of the log ----
powershell -NoProfile -Command "Get-Content -LiteralPath '%LOG%' -Tail 40" 2>nul
echo ----------------------------------
echo Full log: %LOG%
echo.
if not defined CI pause
exit /b 1
