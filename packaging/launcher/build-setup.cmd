@echo off
setlocal
if "%~4"=="" exit /b 2
call "%~1\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
rc.exe /nologo /fo "%~2\SetupBootstrap.res" "%~2\SetupBootstrap.rc"
if errorlevel 1 exit /b %errorlevel%
cl.exe /nologo /std:c++17 /utf-8 /EHsc /O2 /MT /DWIN32_LEAN_AND_MEAN /Fo"%~2\SetupBootstrap.obj" "%~dp0SetupBootstrap.cpp" "%~2\SetupBootstrap.res" /link /SUBSYSTEM:WINDOWS /MANIFEST:NO /OUT:"%~4"
exit /b %errorlevel%
