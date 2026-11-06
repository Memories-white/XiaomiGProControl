@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-ghidra" XiaomiGCommandCenter -process libXiaomiGCommandCenter.dll -noanalysis -scriptPath "scripts" -postScript DecompileAddresses.java "analysis-ghidra\client-data-cmd-vtable.txt" 180047bb0 180047580 180047570 180047590 1800475a0 1801d65e0
exit /b %errorlevel%
