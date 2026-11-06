@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-service" MiDeviceService -process MiDeviceServiceEntry.dll -noanalysis -scriptPath "scripts" -postScript DecompileAddresses.java "analysis-service\service-handlers.txt" 180cbb390 180cbec90 180cbec30 180c0bb70 180c0bc00 180c0c260 180b92e60 180c03870 180c039f0 180c2ffb0 180cba8d0 180cbbc20 180c70a80 180c714a0 180c71ab0 180c71fe0
exit /b %errorlevel%
