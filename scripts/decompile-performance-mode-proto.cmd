@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-service" MiDeviceService -process MiDeviceServiceEntry.dll -noanalysis -scriptPath "scripts" -postScript DecompileAddresses.java "analysis-service\performance-mode-proto.txt" 180c29540 180c29620 180c29530 180c294e0 180c06dd0 180c071f0 180c07140 180c06a40 180c06df0 180c06f90 180c5e200 180c5e510 180c5eca0 180c5ee60 180c5f180 180c5f4a0 180c5f940 180c5fca0 180c5ffc0
exit /b %errorlevel%
