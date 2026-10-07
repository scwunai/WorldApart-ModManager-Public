@echo off
setlocal
set "VCDIR=C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Tools\MSVC\14.50.35717"
set "KITDIR=C:\Program Files (x86)\Windows Kits\10"
set "SDKVER=10.0.22621.0"
set "INCLUDE=%VCDIR%\include;%KITDIR%\Include\%SDKVER%\ucrt;%KITDIR%\Include\%SDKVER%\um;%KITDIR%\Include\%SDKVER%\shared"
set "LIB=%VCDIR%\lib\x64;%KITDIR%\Lib\%SDKVER%\ucrt\x64;%KITDIR%\Lib\%SDKVER%\um\x64"
set "PATH=%VCDIR%\bin\Hostx64\x64;%PATH%"
cd /d <G>\KIMI\injector

del /q wastart.exe 2>nul
cl /nologo /O1 /MT /W3 wastart.c /Fe:wastart.exe /link /SUBSYSTEM:WINDOWS user32.lib kernel32.lib >build.log 2>&1
if exist wastart.exe (echo WASTART_BUILD_OK) else (echo WASTART_BUILD_FAIL & type build.log)

del /q probe2.dll 2>nul
cl /nologo /LD /O1 /MT /W3 probe2.c /Fe:probe2.dll /link /SUBSYSTEM:WINDOWS >probe2_build.log 2>&1
if exist probe2.dll (echo PROBE2_BUILD_OK) else (echo PROBE2_BUILD_FAIL & type probe2_build.log)

del /q probe3.dll 2>nul
cl /nologo /LD /O1 /MT /W3 probe3.c /Fe:probe3.dll /link /SUBSYSTEM:WINDOWS >probe3_build.log 2>&1
if exist probe3.dll (echo PROBE3_BUILD_OK) else (echo PROBE3_BUILD_FAIL & type probe3_build.log)

del /q probe4.dll 2>nul
cl /nologo /LD /O1 /MT /W3 probe4.c /Fe:probe4.dll /link /SUBSYSTEM:WINDOWS >probe4_build.log 2>&1
if exist probe4.dll (echo PROBE4_BUILD_OK) else (echo PROBE4_BUILD_FAIL & type probe4_build.log)

del /q probe5.dll 2>/dev/null
cl /nologo /LD /O1 /MT /W3 probe5.c /Fe:probe5.dll /link /SUBSYSTEM:WINDOWS >probe5_build.log 2>&1
if exist probe5.dll (echo PROBE5_BUILD_OK) else (echo PROBE5_BUILD_FAIL & type probe5_build.log)

del /q probe6.dll 2>/dev/null
cl /nologo /LD /O1 /MT /W3 probe6.c /Fe:probe6.dll /link /SUBSYSTEM:WINDOWS >probe6_build.log 2>&1
if exist probe6.dll (echo PROBE6_BUILD_OK) else (echo PROBE6_BUILD_FAIL & type probe6_build.log)
