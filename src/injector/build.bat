@echo off
setlocal
set "VCDIR=C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Tools\MSVC\14.50.35717"
set "KITDIR=C:\Program Files (x86)\Windows Kits\10"
set "SDKVER=10.0.22621.0"
set "INCLUDE=%VCDIR%\include;%KITDIR%\Include\%SDKVER%\ucrt;%KITDIR%\Include\%SDKVER%\um;%KITDIR%\Include\%SDKVER%\shared"
set "LIB=%VCDIR%\lib\x64;%KITDIR%\Lib\%SDKVER%\ucrt\x64;%KITDIR%\Lib\%SDKVER%\um\x64"
set "PATH=%VCDIR%\bin\Hostx64\x64;%PATH%"
cd /d <G>\KIMI\injector
cl /nologo /O1 /MT /W3 wastart.c /Fe:wastart.exe /link /SUBSYSTEM:WINDOWS user32.lib kernel32.lib >build.log 2>&1
if exist wastart.exe (echo BUILD_OK) else (echo BUILD_FAIL & type build.log)
