@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-service" MiDeviceService -process MiDeviceServiceEntry.dll -noanalysis -scriptPath "scripts" -postScript FindReferences.java "analysis-service\wmiexec-handler-refs.txt" 180c30120 180c30dc0
exit /b %errorlevel%
