@echo off
setlocal
if "%~2"=="" exit /b 2
call "%~1\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0"
rc.exe /nologo /fo "%~2\Launcher.res" Launcher.rc
if errorlevel 1 exit /b %errorlevel%
cl.exe /nologo /std:c++17 /utf-8 /EHsc /O2 /MT /DWIN32_LEAN_AND_MEAN /Fo"%~2\Launcher.obj" Launcher.cpp "%~2\Launcher.res" /link /SUBSYSTEM:WINDOWS /MANIFEST:NO /OUT:"%~2\GProControl.exe"
set "buildResult=%errorlevel%"
popd
exit /b %buildResult%
