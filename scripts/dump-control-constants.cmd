@echo off
setlocal
set "JAVA_HOME=C:\Program Files\Java\jdk-21"
set "GHIDRA_HEADLESS_MAXMEM=4G"
set "USERPROFILE=%CD%\.analysis\ghidra-user"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
call ".tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "analysis-ghidra" XiaomiGCommandCenter -process libXiaomiGCommandCenter.dll -noanalysis -scriptPath "scripts" -postScript DumpAddressBytes.java "analysis-ghidra\control-constants.txt" 180358d98 1803589dc 180398f44 180399788 1803a44a4 1803a44b4 1803a44cc 1803a44e4 1803a44fc 1803a4514 1803a4524 1803a4534 1803a4654 1803a465c 1803a4664 1803a46c4 1803a46cc 1803a46f8 1803a4754 1803a475c 1803a4764 1803a478c 1803a4794 1803a479c 1803a47a4 1803a47ac 1803a47b4 1803a47bc 1803a47c4 1803a47e0 1803a47e8 1803a47f0 1803a47f8 1803a4800 1803a4808 1803a4810 1803a4818 1803a4840 1803a4848 1803a4850 1803a4858 1803a49bc
exit /b %errorlevel%
