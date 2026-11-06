@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-service" MiDeviceService -process MiDeviceServiceEntry.dll -noanalysis -scriptPath "scripts" -postScript DecompileAddresses.java "analysis-service\service-protocol.txt" 180c03920 180c039e0 180b92c80 180b923e0 180b92390 180b92640 180c30120 180c30a50 180c30dc0 180c31100 180c31410 180c31850 180c71280 180c71230 180c707c0
exit /b %errorlevel%
