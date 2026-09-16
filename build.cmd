@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem  GMAnnotation build helper - .NET SDK only, no Visual Studio
rem
rem  KEEP THIS FILE 7-BIT ASCII ONLY.
rem  NEVER PUT goto OR call LABELS IN THIS FILE.
rem
rem  Two independent cmd.exe defects make those two rules mandatory:
rem
rem  1) cmd.exe reads a .cmd file byte by byte using the console
rem     code page (936 / GBK on a Chinese Windows). A single
rem     non-ASCII character makes the reader lose the CR/LF
rem     boundary: the "rem " prefix is swallowed, the rest of the
rem     line is executed as a command, and the whole script
rem     collapses into a storm of
rem       "'xxx' is not recognized as an internal or external command".
rem     Chinese text belongs in the reply and in build.log, never here.
rem
rem  2) Label lookup assumes CRLF line endings. With LF-only endings
rem     the label scanner skips labels at 512-byte boundaries, which
rem     gives an intermittent "The system cannot find the batch label
rem     specified". This repo stores text files with LF, so this file
rem     uses no labels at all - flow is controlled with flags only.
rem
rem  Never write %ProgramFiles(x86)% here either: the ")" inside the
rem  variable name is parsed as the end of a parenthesised block and
rem  shreds every line after it.
rem
rem  Modes, default = auto:
rem    build.cmd            auto    net8.0-windows + net45
rem                                 AutoCAD 2025-2027 and 2015-2024
rem    build.cmd 2025       net8.0-windows only   AutoCAD 2025-2027
rem    build.cmd legacy     net45 only            AutoCAD 2015-2024
rem    build.cmd zwcad      auto + net472         ZWCAD, needs ZWCAD
rem    build.cmd all        everything
rem    build.cmd release    auto, then copy the fresh DLLs into
rem                                 EVERY ..\v0.5* folder next to this
rem                                 source that already holds a
rem                                 GMAnnotation build, so whichever
rem                                 one CAD really loads from gets
rem                                 refreshed. A 2nd argument, or
rem                                 DEPLOY_DIR, limits it to 1 folder.
rem
rem  The complete compiler output is written to build.log next to
rem  this script on every run, so the raw errors can be copied out
rem  of the console window or the whole file handed over.
rem
rem  The .NET SDK is located automatically: PATH first, then the
rem  default install folder - so a double-click works even in a cmd
rem  window that has no dotnet in PATH.
rem
rem  net45 / net472 normally need the matching .NET Framework
rem  targeting pack, which ships with Visual Studio. Both projects
rem  reference Microsoft.NETFramework.ReferenceAssemblies, so a
rem  plain .NET SDK is enough - no MSB3644 any more.
rem
rem  NuGet restore needs nuget.org, or your company mirror
rem  configured in a NuGet.config next to this script.
rem ============================================================

set "ROOT=%~dp0"
set "SRC=%ROOT%src"
set "LOG=%ROOT%build.log"
set "MODE=%~1"
if "%MODE%"=="" set "MODE=auto"

rem  Deploy folder(s) used by "release":
rem    no 2nd argument -> every ..\v0.5* folder that already contains a
rem                       GMAnnotation build is refreshed (auto-detect,
rem                       see the deploy step). Writing to one fixed
rem                       folder while CAD loads from another is exactly
rem                       what produces "the build succeeded but the
rem                       plugin did not change".
rem    2nd argument or DEPLOY_DIR -> only that folder, created if missing.
set "OUT=%ROOT%..\v0.5.2"
if not "%~2"=="" set "OUT=%~2"
if not "%~2"=="" if "%~d2"=="" set "OUT=%ROOT%%~2"
if "%~2"=="" if not "%DEPLOY_DIR%"=="" set "OUT=%DEPLOY_DIR%"
set "EXPLICIT=0"
if not "%~2"=="" set "EXPLICIT=1"
if "%~2"=="" if not "%DEPLOY_DIR%"=="" set "EXPLICIT=1"

rem  Clear the log at the start of every run.
del "%LOG%" >nul 2>nul

rem  Pause at the end only when launched by double-click.
set "DBLCLICK="
echo "%cmdcmdline%" | find /i "%~nx0" >nul 2>nul
if not errorlevel 1 set "DBLCLICK=1"

set "FAILED=0"
set "VALID=0"
set "DEPLOY=0"
set "BUILD_2025=0"
set "BUILD_LEGACY=0"
set "BUILD_ZWCAD=0"

if /i "%MODE%"=="auto"    (set "BUILD_2025=1" & set "BUILD_LEGACY=1" & set "VALID=1")
if /i "%MODE%"=="2025"    (set "BUILD_2025=1" & set "VALID=1")
if /i "%MODE%"=="legacy"  (set "BUILD_LEGACY=1" & set "VALID=1")
if /i "%MODE%"=="zwcad"   (set "BUILD_2025=1" & set "BUILD_LEGACY=1" & set "BUILD_ZWCAD=1" & set "VALID=1")
if /i "%MODE%"=="all"     (set "BUILD_2025=1" & set "BUILD_LEGACY=1" & set "BUILD_ZWCAD=1" & set "VALID=1")
if /i "%MODE%"=="release" (set "BUILD_2025=1" & set "BUILD_LEGACY=1" & set "DEPLOY=1" & set "VALID=1")

if "%VALID%"=="0" (
    set "FAILED=1"
    set "BUILD_2025=0"
    set "BUILD_LEGACY=0"
    set "BUILD_ZWCAD=0"
    echo.
    echo [ERROR] Unknown mode "%MODE%".
    echo         Use one of: auto ^| 2025 ^| legacy ^| zwcad ^| all ^| release
    echo.
)

echo.
echo ==== GMAnnotation build ====
echo Project root : %ROOT%
echo Mode         : %MODE%
echo Targets      : 2025=%BUILD_2025%  legacy=%BUILD_LEGACY%  zwcad=%BUILD_ZWCAD%
echo.

rem  Locate the .NET SDK. No parentheses in these paths on purpose.
set "DOTNET="
where dotnet >nul 2>nul
if not errorlevel 1 set "DOTNET=dotnet"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%SystemDrive%\Program Files\dotnet\dotnet.exe" set "DOTNET=%SystemDrive%\Program Files\dotnet\dotnet.exe"

if not defined DOTNET (
    set "FAILED=1"
    set "BUILD_2025=0"
    set "BUILD_LEGACY=0"
    set "BUILD_ZWCAD=0"
    echo [ERROR] dotnet was not found, neither in PATH nor at
    echo         "%ProgramFiles%\dotnet\dotnet.exe".
    echo         Install the .NET SDK 8.0 or newer from
    echo             https://dotnet.microsoft.com/download
    echo         NOTE: the .NET Runtime alone cannot compile anything.
    echo.
) else (
    echo [OK] dotnet SDK : !DOTNET!
    echo [OK] dotnet SDK version:
    "!DOTNET!" --version
    echo.
)

if "%BUILD_2025%"=="1" (
    echo ------------------------------------------------------------
    echo  GMAnnotation.AutoCAD2025   net8.0-windows   AutoCAD 2025-2027
    echo ------------------------------------------------------------
    "!DOTNET!" build "%SRC%\LAAnnotation.AutoCAD2025.csproj" -c Release -v minimal --nologo >>"%LOG%" 2>&1
    set "RC=!ERRORLEVEL!"
    type "%LOG%"
    if not "!RC!"=="0" set "FAILED=1"
    echo.
)

if "%BUILD_LEGACY%"=="1" (
    echo ------------------------------------------------------------
    echo  GMAnnotation.AutoCAD       net45            AutoCAD 2015-2024
    echo ------------------------------------------------------------
    "!DOTNET!" build "%SRC%\LAAnnotation.AutoCAD.csproj" -c Release -v minimal --nologo >>"%LOG%" 2>&1
    set "RC=!ERRORLEVEL!"
    type "%LOG%"
    if not "!RC!"=="0" set "FAILED=1"
    echo.
)

if "%BUILD_ZWCAD%"=="1" (
    set "ZWCADDIR=C:\Program Files\ZWSOFT\ZWCAD Enterprise"
    if not exist "!ZWCADDIR!\ZwManaged.dll" (
        echo [SKIP] ZWCAD not found at "!ZWCADDIR!".
        echo        Install ZWCAD, or build it explicitly with:
        echo            dotnet build "%SRC%\LAAnnotation.csproj" -c Release -p:ZwcadDir="D:\path\to\ZWCAD"
    ) else (
        echo ------------------------------------------------------------
        echo  GMAnnotation.ZWCAD         net472           ZWCAD
        echo ------------------------------------------------------------
        "!DOTNET!" build "%SRC%\LAAnnotation.csproj" -c Release -v minimal --nologo >>"%LOG%" 2>&1
        set "RC=!ERRORLEVEL!"
        type "%LOG%"
        if not "!RC!"=="0" set "FAILED=1"
        echo.
    )
)

if "%FAILED%"=="0" if "%DEPLOY%"=="1" (
    echo ---- Deploy ----
    set "LIST=%TEMP%\gmannotation_deploy.txt"
    if not defined TEMP set "LIST=%ROOT%build.deploy.txt"
    del "!LIST!" >nul 2>nul
    if "!EXPLICIT!"=="1" (
        >>"!LIST!" echo %OUT%
    ) else (
        rem  Auto-detect: refresh every sibling ..\v0.5* folder that already
        rem  holds a GMAnnotation build. v0.5.1 uses LAAnnotation.* file
        rem  names, so it is skipped automatically.
        for /d %%D in ("%ROOT%..\v0.5*") do (
            set "HIT=0"
            if exist "%%~fD\GMAnnotation.AutoCAD.dll" set "HIT=1"
            if exist "%%~fD\GMAnnotation.AutoCAD2025.dll" set "HIT=1"
            if "!HIT!"=="1" >>"!LIST!" echo %%~fD
        )
    )
    set "HITS=0"
    if exist "!LIST!" for /f "usebackq delims=" %%T in ("!LIST!") do (
        set "HITS=1"
        if not exist "%%T" mkdir "%%T"
        if not exist "%%T\net45" mkdir "%%T\net45"
        if not exist "%%T\net8.0-windows" mkdir "%%T\net8.0-windows"
        if exist "%ROOT%bin-acad\GMAnnotation.AutoCAD.dll" (
            copy /Y "%ROOT%bin-acad\GMAnnotation.AutoCAD.dll" "%%T\net45\" >nul
            if exist "%ROOT%bin-acad\GMAnnotation.AutoCAD.pdb" copy /Y "%ROOT%bin-acad\GMAnnotation.AutoCAD.pdb" "%%T\net45\" >nul
            if exist "%ROOT%bin-acad\System.ValueTuple.dll" copy /Y "%ROOT%bin-acad\System.ValueTuple.dll" "%%T\net45\" >nul
            echo   net45  -^> %%T\GMAnnotation.AutoCAD.dll
        )
        if exist "%ROOT%bin-acad2025-2027\GMAnnotation.AutoCAD2025.dll" (
            copy /Y "%ROOT%bin-acad2025-2027\GMAnnotation.AutoCAD2025.dll" "%%T\net8.0-windows\" >nul
            if exist "%ROOT%bin-acad2025-2027\GMAnnotation.AutoCAD2025.pdb" copy /Y "%ROOT%bin-acad2025-2027\GMAnnotation.AutoCAD2025.pdb" "%%T\net8.0-windows\" >nul
            if exist "%ROOT%bin-acad2025-2027\GMAnnotation.AutoCAD2025.deps.json" copy /Y "%ROOT%bin-acad2025-2027\GMAnnotation.AutoCAD2025.deps.json" "%%T\net8.0-windows\" >nul
            if exist "%ROOT%bin-acad2025-2027\System.Drawing.Common.dll" copy /Y "%ROOT%bin-acad2025-2027\System.Drawing.Common.dll" "%%T\net8.0-windows\" >nul
            echo   net8   -^> %%T\GMAnnotation.AutoCAD2025.dll
        )
    )
    del "!LIST!" >nul 2>nul
    if "!HITS!"=="0" (
        echo   [WARN] no GMAnnotation deploy folder was found next to the
        echo          source, and none was given on the command line.
        echo          Nothing was copied. Pass the folder CAD loads from:
        echo              build.cmd release "D:\path\to\that\folder"
        echo          a relative path is taken from this script's folder:
        echo              build.cmd release ..\v0.5.2.1
        echo.
    ) else (
        echo ---- Deploy done. Restart CAD before testing. ----
    )
    echo.
)

if "%FAILED%"=="1" (
    echo.
    echo ==== BUILD FAILED - read the errors above ====
    echo.
    echo Full build output, all raw errors, has been saved to:
    echo   %LOG%
    echo Send that file if you need the errors diagnosed.
    echo.
    echo Common causes:
    echo   * NuGet cannot reach nuget.org - set up your company mirror in NuGet.config
    echo   * ZWCAD not installed          - use the plain auto mode instead
    echo   * a real compile error         - the error text shows file name and line number
    echo.
) else (
    echo ==== Build succeeded ====
    echo.
    echo Full build log: %LOG%
    echo.
    echo Output DLLs:
    if exist "%ROOT%bin-acad2025-2027\GMAnnotation.AutoCAD2025.dll" echo   %ROOT%bin-acad2025-2027\GMAnnotation.AutoCAD2025.dll
    if exist "%ROOT%bin-acad\GMAnnotation.AutoCAD.dll" echo   %ROOT%bin-acad\GMAnnotation.AutoCAD.dll
    if exist "%ROOT%bin\GMAnnotation.ZWCAD.dll" echo   %ROOT%bin\GMAnnotation.ZWCAD.dll
    echo.
    echo Load the DLL in CAD with NETLOAD, then run GM_PZ_MENU to rebuild the menu.
    echo.
)

if not defined DBLCLICK exit /b %FAILED%
echo Press any key to close this window...
pause >nul
exit /b %FAILED%
