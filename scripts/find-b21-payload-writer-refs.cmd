@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-ghidra" XiaomiGCommandCenter -process libXiaomiGCommandCenter.dll -noanalysis -scriptPath "scripts" -postScript FindReferences.java "analysis-ghidra\b21-payload-writer-refs.txt" 18035ab90 18035ad00 18035c120 18035c200 18035c2c0 18035c350 18035c3f0 18035c480 18035c520 18035c600 18035c680 18035c700 18035c860
exit /b %errorlevel%
