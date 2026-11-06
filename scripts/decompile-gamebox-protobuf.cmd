@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-service" MiDeviceService -process MiDeviceServiceEntry.dll -noanalysis -scriptPath "scripts" -postScript DecompileAddresses.java "analysis-service\gamebox-protobuf.txt" 180b92f60 180b92a40 180b92a00 180b92a70 180b92ab0 180b92b10 180c038e0 180c0c480 180c0cb30 180c0c470 180c0c410 180c0c570 180e25f00 180c0ca80 180c0c970 180c06a40 180c0c5b0 180c0c7a0
exit /b %errorlevel%
