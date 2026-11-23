@echo off
rem Build the ACZ text module and its tests (x64, MSVC). One command:  module\build.cmd
rem Needs Visual Studio 2022 (or Build Tools) with the C++ x64 tools. Output: module\build\
rem   version.dll         the module (release: groups enabled in patches.json)
rem   probe\version.dll   probe build: all groups (for the in-game check of not yet proven groups)
rem   patchtest.exe, proxytest.exe   tests (see module\README.md)
setlocal
cd /d "%~dp0"
if defined VCINSTALLDIR goto have_vc
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto no_vc
if not exist build mkdir build
"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath > build\_vsdir.txt
set /p VSDIR=<build\_vsdir.txt
del build\_vsdir.txt
if not defined VSDIR goto no_vc
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
:have_vc
where python >nul 2>nul && ( python tools\gen_header.py --check || exit /b 1 )
where python >nul 2>nul && ( python tests\gen_header_test.py >nul || ( echo gen_header_test failed & exit /b 1 ) )
if not exist build\probe mkdir build\probe
set CFLAGS=/nologo /O2 /W4 /WX /utf-8 /MT /GS /guard:cf /Brepro /D_CRT_SECURE_NO_WARNINGS
set LFLAGS=/DLL /DEF:version.def /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /GUARD:CF /Brepro kernel32.lib user32.lib
set SRC=src\module.c src\proxy.c src\patch.c src\sha256.c
cl %CFLAGS% /Fobuild\ %SRC% /Fe:build\version.dll /link %LFLAGS% || exit /b 1
cl %CFLAGS% /DACZ_PROBE_BUILD=1 /Fobuild\probe\ %SRC% /Fe:build\probe\version.dll /link %LFLAGS% || exit /b 1
cl %CFLAGS% /Fobuild\ tests\patchtest.c src\patch.c src\sha256.c /Fe:build\patchtest.exe || exit /b 1
cl %CFLAGS% /Fobuild\ tests\proxytest.c /Fe:build\proxytest.exe || exit /b 1
del /q build\*.obj build\probe\*.obj build\*.exp build\*.lib build\probe\*.exp build\probe\*.lib 2>nul
echo built: build\version.dll build\probe\version.dll build\patchtest.exe build\proxytest.exe
exit /b 0
:no_vc
echo Visual Studio C++ x64 tools not found (vswhere)
exit /b 1
