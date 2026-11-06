@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-ghidra" XiaomiGCommandCenter -process libXiaomiGCommandCenter.dll -noanalysis -scriptPath "scripts" -postScript DecompileAddresses.java "analysis-ghidra\wmi-operations.txt" 1801e9b90 1801e8d10 1801e9df0 1801e9850 1801e9bd0 1801e8ed0 1801e9e50 1801e9900 1801ea0a0 1801e9ae0 1801e9f70 1801e99a0 1801ea000 1801e9a40 1801ea140 1801ea1b0 1801ea220
exit /b %errorlevel%
