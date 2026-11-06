@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-ghidra" XiaomiGCommandCenter -process libXiaomiGCommandCenter.dll -noanalysis -scriptPath "scripts" -postScript DumpVtables.java "analysis-ghidra\wmi-vtables.txt" 1803a4b48 1803a4b80 1803a4bb8 1803a4bf0 1803a4c28 1803a4c60 1803a4c98 1803a4cd0 1803a4d08 1803a4d40 1803a4d78 1803a4db0 1803a5bd0
exit /b %errorlevel%
