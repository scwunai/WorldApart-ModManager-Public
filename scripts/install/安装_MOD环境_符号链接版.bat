@echo off
setlocal enabledelayedexpansion
rem ============================================================================
rem  不问凡尘 (World Apart) MOD 环境 —— 【符号链接版】安装脚本
rem
rem  与 安装_MOD环境.bat（复制版）的区别：
rem    * 复制版：把 BepInEx\ dotnet\ 示例MOD 整份**复制**进游戏目录；
rem             创意工坊内容更新后，必须重跑复制版脚本才会生效。
rem    * 本脚本：把这些**内容目录**用目录联接（mklink /J）挂进游戏目录；
rem             创意工坊内容一更新，游戏目录立刻跟随，不用重新复制/重装。
rem
rem  行为要点：
rem    * 逐项尝试「联接 -> 符号链接 -> 复制」，任一项失败自动回退到复制并说明原因；
rem    * 启动器小文件（modlauncher.exe / modprobe.dll / doorstop_config.ini /
rem      inject.ini / steam_appid.txt / 启动MOD版.bat / WorldApart_mod.exe）仍照原样复制，
rem      它们体积很小且不含 MOD 内容；
rem    * 装前用 tasklist 检查游戏未运行；
rem    * 幂等：重复运行只会刷新联接，不会破坏已有安装，也不会动源内容；
rem    * 卸载请用 卸载_MOD环境.bat（它会识别联接并只摘链接、不删源内容）。
rem
rem  用法：
rem    1) 双击运行；内容目录默认取脚本自己所在目录；
rem    2) 也可以把内容目录（例如创意工坊 ...\content\4209920\3814675406）
rem       拖到本脚本上运行 —— 拖入的目录即内容目录。
rem ============================================================================

set "SCRIPTDIR=%~dp0"
set "SRC=%~dp0"
set "NLINK=0"
set "NCOPY=0"
set "NINPLACE=0"

echo ============================================================================
echo  不问凡尘 MOD 环境 —— 符号链接版安装
echo ============================================================================
echo.

rem ---------------------------------------------------------------- 源目录 ----
if not "%~1"=="" (
  set "SRC=%~1"
  if not "!SRC:~-1!"=="\" set "SRC=!SRC!\"
)
if not exist "!SRC!runtime\modlauncher.exe" (
  echo [安装] 在 "!SRC!" 下没找到 MOD 环境内容（缺 runtime\modlauncher.exe）。
  set "SRCINPUT="
  set /p "SRCINPUT=请输入 MOD 环境内容目录（如 创意工坊 ...\content\4209920\3814675406）: "
  set "SRC=!SRCINPUT!"
  set SRC=!SRC:"=!
  if not "!SRC:~-1!"=="\" set "SRC=!SRC!\"
)
if not exist "!SRC!runtime\modlauncher.exe" (
  echo [错误] 内容目录无效：!SRC!
  echo        没有找到 !SRC!runtime\modlauncher.exe，已取消。
  pause
  exit /b 1
)

rem ------------------------------------------------------------ 游戏目录 ----
set "GAME=!SRC!"
if not exist "!GAME!WorldApart.exe" (
  echo [安装] 内容目录下没有 WorldApart.exe（内容与游戏目录分开时属正常）。
  set "GAMEINPUT="
  set /p "GAMEINPUT=请输入游戏目录（含 WorldApart.exe，如 C:\Program Files (x86)\Steam\steamapps\common\<游戏文件夹>）: "
  set "GAME=!GAMEINPUT!"
  set GAME=!GAME:"=!
  if not "!GAME:~-1!"=="\" set "GAME=!GAME!\"
)
if not exist "!GAME!WorldApart.exe" (
  echo [错误] 找不到 !GAME!WorldApart.exe，已取消。
  pause
  exit /b 1
)

echo [安装] 内容目录: !SRC!
echo [安装] 游戏目录: !GAME!
echo.

rem ---------------------------------------------------- 游戏未运行检查 ----
call :check_not_running WorldApart.exe
call :check_not_running WorldApart_mod.exe

rem ------------------------------------------------------------ 说明 ----
set "SAMEDIR=0"
if /i "!GAME!"=="!SRC!" (
  set "SAMEDIR=1"
  echo [提示] 内容目录与游戏目录**相同**：目录不能联接到自己（会形成自指死循环），
  echo        凡「源 == 目标」的项会被当作「已在位」（内容本来就在游戏目录里，等同复制完成）；
  echo        目标在游戏目录之外的项目（如 Mods\example.tutorial）仍会正常联接。
  echo        想获得「工坊更新后自动跟随」，请把 MOD 环境放在游戏目录**之外**再运行本脚本。
  echo.
)

rem ------------------------------------------------- 启动器小文件（复制） ----
echo [1/4] 写入启动器文件（小文件，照复制版原样复制）
copy /y "!SRC!runtime\modlauncher.exe" "!GAME!modlauncher.exe" >nul
copy /y "!SRC!runtime\modprobe.dll"  "!GAME!modprobe.dll"  >nul
copy /y "!SRC!runtime\doorstop_config.ini" "!GAME!doorstop_config.ini" >nul
>  "!GAME!inject.ini" echo [inject]
>> "!GAME!inject.ini" echo dll=!GAME!modprobe.dll
>> "!GAME!inject.ini" echo delay_ms=0
>> "!GAME!inject.ini" echo target_mode=2
>> "!GAME!inject.ini" echo entry_tick=0
>> "!GAME!inject.ini" echo watch_ms=25000
<nul (set /p MODAPPID=4209920)> "!GAME!steam_appid.txt"
if not exist "!GAME!WorldApart_mod.exe" (
  fsutil hardlink create "!GAME!WorldApart_mod.exe" "!GAME!WorldApart.exe" >nul 2>&1
  if not exist "!GAME!WorldApart_mod.exe" copy /y "!GAME!WorldApart.exe" "!GAME!WorldApart_mod.exe" >nul
)
if not exist "!GAME!WorldApart_mod_Data\" mklink /j "!GAME!WorldApart_mod_Data" "!GAME!WorldApart_Data" >nul 2>&1

rem ------------------------------------------------ 内容目录（优先联接） ----
echo.
echo [2/4] 挂载内容目录（联接优先，失败自动回退复制）
call :mount "!GAME!BepInEx" "!SRC!BepInEx" "BepInEx"
call :mount "!GAME!dotnet"  "!SRC!dotnet"  "dotnet"

set "MODDIR=%USERPROFILE%\AppData\LocalLow\Nuverse\WorldApart\Mods"
if not exist "!MODDIR!" mkdir "!MODDIR!"
echo.
echo [3/4] 示例 MOD（默认关闭，游戏内 设置 -^> MOD管理 启用）
if exist "!SRC!示例MOD\example.tutorial\" (
  call :mount "!MODDIR!\example.tutorial" "!SRC!示例MOD\example.tutorial" "示例MOD example.tutorial"
) else (
  echo   [跳过] 内容目录里没有 示例MOD\example.tutorial
)

copy /y "!SRC!runtime\启动MOD版.bat" "!GAME!启动MOD版.bat" >nul

rem ------------------------------------------------------------ 启动方式 ----
echo.
echo [4/4] 选择安装方式：
echo         [1] 普通版（默认）—— 以后用 启动MOD版.bat 启动，Steam 点「开始游戏」仍是原版
echo         [2] Steam 直启版   —— 额外装 Steam 直启组件，Steam 点「开始游戏」直接进 MOD 版
set "VER=1"
set /p "VER=请输入 1 或 2 后回车（直接回车即选 1）: "
if "!VER!"=="" set "VER=1"
set "VER=!VER:~0,1!"
if "!VER!"=="" set "VER=1"
set "SD=0"
if "!VER!"=="2" (
  copy /y "!SRC!Steam直启\USERENV.dll" "!GAME!USERENV.dll" >nul
  copy /y "!SRC!Steam直启\userenv_orig.dll" "!GAME!userenv_orig.dll" >nul
  if exist "!GAME!USERENV.dll" if exist "!GAME!userenv_orig.dll" set "SD=1"
  if "!SD!"=="1" (
    echo [完成] 已安装 Steam 直启版：USERENV.dll 与 userenv_orig.dll 已放入游戏目录（均为新增文件，未覆盖游戏既有文件）。
  ) else (
    echo [错误] Steam 直启组件复制失败，请检查内容目录里 Steam直启 目录是否完整。
  )
)

echo.
echo ============================================================================
echo  安装完毕
echo ----------------------------------------------------------------------------
echo    联接/符号链接挂载: !NLINK! 项   ^<- 这些项会随创意工坊内容更新**自动跟随**
echo    回退为复制      : !NCOPY! 项   ^<- 这些项如更新，需要重跑本脚本
echo    源内容已在位    : !NINPLACE! 项   ^<- 内容目录 == 游戏目录，内容本来就在游戏目录里
echo ----------------------------------------------------------------------------
echo    回退原因会打印在上面每一项的 [回退] 行里，常见为：
echo      * 内容目录与游戏目录相同（不能自指）      -^> 见上面的 [提示]
echo      * 目标目录已被真实目录占用且改名失败
echo      * 系统策略不允许符号链接（仅 /D 分支需要管理员或开发者模式）
echo ----------------------------------------------------------------------------
if "!SD!"=="1" (
  echo    启动方式: Steam 直启版（Steam 点「开始游戏」即 MOD 版；也可用 启动MOD版.bat）
) else (
  echo    启动方式: 普通版（用游戏目录下的 启动MOD版.bat 启动；Steam 直接开始仍是原版）
)
echo    启动器小文件（modlauncher.exe / modprobe.dll / doorstop_config.ini / inject.ini /
echo    steam_appid.txt / 启动MOD版.bat / WorldApart_mod.exe）始终是复制，不含 MOD 内容。
echo    卸载：运行 卸载_MOD环境.bat（识别到联接时只摘链接，不会删掉工坊源内容）。
echo    首次启动 MOD 版若黑屏 1 分钟左右属正常：正在为当前游戏版本生成兼容层，仅此一次。
echo ============================================================================
pause
exit /b 0

rem ============================================================================
rem  子过程
rem ============================================================================

rem ---- 检查进程未运行；运行中则终止脚本 -------------------------------------
:check_not_running
tasklist /nh /fi "imagename eq %1" 2>nul | findstr /i /c:"%1" >nul
if not errorlevel 1 goto :running
exit /b 0

:running
echo.
echo [错误] 检测到游戏正在运行（%1）。请先**完全退出游戏**，再重跑本脚本。
echo        本次没有改动任何文件。
pause
exit /b 1

rem ---- 判断 %1 是否联接/符号链接 -> ISL=1/0 ---------------------------------
:is_link
set "ISL=0"
fsutil reparsepoint query "%~1" >nul 2>&1
if not errorlevel 1 set "ISL=1"
exit /b 0

rem ---- 检验刚建好的链接（LP）是否真能读到源（SP）内容 -> LV=1/0 -------------
rem      用例：mklink /J 指向网络路径时 Windows 会「建成功但读不到」，
rem            必须实测一次，否则会把死链当成安装成功。
:alink_ok
set "LV=0"
for /f "delims=" %%F in ('dir /b /a "!SP!" 2^>nul') do (
  if exist "!LP!\%%F" set "LV=1"
  goto :alink_done
)
:alink_done
exit /b 0

rem ---- 为 %1 生成不冲突的备份名 -> BAK --------------------------------------
:mkbak
set "BAK=%~1.bak_%RANDOM%%RANDOM%"
if exist "!BAK!" goto :mkbak
exit /b 0

rem ---- 把 %2 的内容挂到 %1（联接 -> 符号链接 -> 复制），%3 = 显示名 --------
:mount
set "LP=%~1"
set "SP=%~2"
set "NM=%~3"
set "JERR="
set "DERR="
if not exist "!SP!" (
  echo   [跳过] !NM!：源内容不存在 "!SP!"
  exit /b 1
)
rem 自指保护：源 == 目标 时 mklink /J 会**建成一个指向自己的死循环联接**（实测过），必须禁止
if /i "!LP!"=="!SP!" (
  echo   [在位] !NM!：内容目录 == 游戏目录，源内容已在游戏目录内（等同复制完成），无需处理
  set /a NINPLACE+=1
  exit /b 0
)

call :is_link "!LP!"
if "!ISL!"=="1" (
  echo   [清理] !NM!："!LP!" 已是链接，先摘除（rmdir 非递归，源内容不动）
  rmdir "!LP!" >nul 2>&1
) else (
  if exist "!LP!" (
    rmdir "!LP!" >nul 2>&1
    if exist "!LP!" (
      call :mkbak "!LP!"
      echo   [备份] !NM!："!LP!" 是真实目录，改名保留为 "!BAK!"
      move "!LP!" "!BAK!" >nul 2>&1
      if exist "!LP!" (
        echo   [错误] !NM!：无法腾出 "!LP!"（改名失败），该项跳过，请手动处理后重跑
        exit /b 1
      )
    )
  )
)

set "MKLOG=%TEMP%\_modenv_mount_mklink.txt"
mklink /J "!LP!" "!SP!" >"%MKLOG%" 2>&1
call :is_link "!LP!"
if "!ISL!"=="1" (
  call :alink_ok
  if "!LV!"=="1" (
    echo   [联接] !NM!  到  "!SP!"      （mklink /J；免管理员；工坊更新后自动跟随）
    set /a NLINK+=1
    exit /b 0
  )
  echo   [警告] !NM!：mklink /J 建出了联接，但透过它读不到源内容（目标多半在网络路径上），已摘除
  set "JERR=（/J 建成的联接不可用：目标不是本地可解析路径）"
  rmdir "!LP!" >nul 2>&1
) else (
  set "JERR="
  set /p "JERR="<"%MKLOG%"
)

mklink /D "!LP!" "!SP!" >"%MKLOG%" 2>&1
call :is_link "!LP!"
if "!ISL!"=="1" (
  call :alink_ok
  if "!LV!"=="1" (
    echo   [符号链接] !NM!  到  "!SP!"   （mklink /D）
    set /a NLINK+=1
    exit /b 0
  )
  echo   [警告] !NM!：mklink /D 建出了符号链接，但透过它读不到源内容，已摘除
  set "DERR=（/D 建成的链接不可用）"
  rmdir "!LP!" >nul 2>&1
) else (
  set "DERR="
  set /p "DERR="<"%MKLOG%"
)
del "%MKLOG%" >nul 2>&1

echo   [回退] !NM!：联接与符号链接都没建成，改为复制。
echo           mklink /J 报错: !JERR!
echo           mklink /D 报错: !DERR!

:mount_copy
robocopy "!SP!" "!LP!" /e /xo >nul
if errorlevel 8 (
  echo   [错误] !NM! 复制也失败（robocopy rc=!errorlevel!），请检查磁盘空间与权限。
  exit /b 1
)
echo   [复制] !NM! 已复制进游戏目录（此模式下工坊更新后需重跑本脚本）
set /a NCOPY+=1
exit /b 0
