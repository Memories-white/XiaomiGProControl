@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-ghidra" XiaomiGCommandCenter -process libXiaomiGCommandCenter.dll -noanalysis -scriptPath "scripts" -postScript DecompileAddresses.java "analysis-ghidra\control-handlers.txt" 180116e50 1801175b0 180117880 180117960 180118120 180118350 1801183f0 180118490 180118530 1801185d0 1801188d0 1801189b0 1801c92b0
exit /b %errorlevel%
