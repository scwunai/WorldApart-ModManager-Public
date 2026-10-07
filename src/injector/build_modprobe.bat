@echo off
setlocal
set "VCDIR=C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Tools\MSVC\14.50.35717"
set "KITDIR=C:\Program Files (x86)\Windows Kits\10"
set "SDKVER=10.0.22621.0"
set "INCLUDE=%VCDIR%\include;%KITDIR%\Include\%SDKVER%\ucrt;%KITDIR%\Include\%SDKVER%\um;%KITDIR%\Include\%SDKVER%\shared"
set "LIB=%VCDIR%\lib\x64;%KITDIR%\Lib\%SDKVER%\ucrt\x64;%KITDIR%\Lib\%SDKVER%\um\x64"
set "PATH=%VCDIR%\bin\Hostx64\x64;%PATH%"
cd /d <G>\KIMI\injector
cl /nologo /LD /O1 /MT /W3 probe6.c /Fe:modprobe.dll /link /SUBSYSTEM:WINDOWS >modprobe_build.log 2>&1
if exist modprobe.dll (echo MODPROBE_BUILD_OK) else (echo MODPROBE_BUILD_FAIL & type modprobe_build.log)
