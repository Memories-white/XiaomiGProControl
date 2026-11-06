@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-ghidra" XiaomiGCommandCenter -process libXiaomiGCommandCenter.dll -noanalysis -scriptPath "scripts" -postScript FindReferences.java "analysis-ghidra\wmiobtain-string-refs.txt" 1803cc060 1803cc048 1803cc138 1803cc168 1803cc198 1803cc1c8 1803cc1f8 1803cc220
exit /b %errorlevel%
