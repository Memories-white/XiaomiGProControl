@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-service" MiDeviceService -process MiDeviceServiceEntry.dll -noanalysis -scriptPath "scripts" -postScript DecompileAddresses.java "analysis-service\gamebox-business.txt" 180cbc0b0 180cbd950 180cbc4a0 180cbdde0 180cbbb70 180cbc9c0 180cbca70 180cbcbd0 180cbdf80 180cbd8a0 180cbcb20 180cbe050
exit /b %errorlevel%
