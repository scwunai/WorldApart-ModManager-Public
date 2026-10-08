@echo off
setlocal enabledelayedexpansion
set "GAME=%~dp0"
if not exist "!GAME!WorldApart.exe" (
  set /p "GAMEINPUT=请输入游戏目录: "
  set "GAME=!GAMEINPUT!\"
)
if not exist "!GAME!WorldApart.exe" ( echo [错误] 找不到 !GAME!WorldApart.exe，已取消。 & pause & exit /b 1 )
del /q "!GAME!modlauncher.exe" "!GAME!modprobe.dll" "!GAME!inject.ini" "!GAME!启动MOD版.bat" 2>nul
del /q "!GAME!WorldApart_mod.exe" 2>nul
if exist "!GAME!WorldApart_mod_Data\" rmdir "!GAME!WorldApart_mod_Data"
echo 已移除 MOD 启动组件。游戏目录下的 BepInEx、dotnet 与 Mods 中的 MOD 包予以保留；
echo 如需完全卸载，请再手动删除游戏目录下的 BepInEx 和 dotnet 文件夹。

rem ---------------------------------------------------------------- 联接清理 ----
rem 符号链接版安装会把 BepInEx\ dotnet\（以及 Mods 里的示例 MOD）以联接/符号链接挂进
rem 游戏目录。这里只**摘链接**，一律用不带 /s 的 rmdir —— 绝不会递归删到工坊源内容。
set "MODDIR=%USERPROFILE%\AppData\LocalLow\Nuverse\WorldApart\Mods"
set "FOUNDLINK=0"
for %%D in ("!GAME!BepInEx" "!GAME!dotnet") do (
  fsutil reparsepoint query %%D >nul 2>&1
  if not errorlevel 1 (
    echo.
    echo [检测] %%D 是联接/符号链接（符号链接版安装）。
    set "FOUNDLINK=1"
  )
)
if exist "!MODDIR!\example.tutorial" (
  fsutil reparsepoint query "!MODDIR!\example.tutorial" >nul 2>&1
  if not errorlevel 1 (
    echo [检测] !MODDIR!\example.tutorial 是联接/符号链接（符号链接版安装）。
    set "FOUNDLINK=1"
  )
)
if "!FOUNDLINK!"=="1" (
  echo.
  set "ANS2="
  set /p "ANS2=是否一并移除这些链接？只摘链接，「内容目录」（工坊源）不受影响 [Y/N]: "
  set "ANS2=!ANS2:~0,1!"
  if /i "!ANS2!"=="Y" (
    for %%D in ("!GAME!BepInEx" "!GAME!dotnet") do (
      fsutil reparsepoint query %%D >nul 2>&1
      if not errorlevel 1 (
        rmdir %%D >nul 2>&1
        fsutil reparsepoint query %%D >nul 2>&1
        if errorlevel 1 ( echo   已摘除链接 %%D ) else ( echo   [警告] 链接摘除失败，请手动删除：%%D )
      )
    )
    if exist "!MODDIR!\example.tutorial" (
      fsutil reparsepoint query "!MODDIR!\example.tutorial" >nul 2>&1
      if not errorlevel 1 (
        rmdir "!MODDIR!\example.tutorial" >nul 2>&1
        fsutil reparsepoint query "!MODDIR!\example.tutorial" >nul 2>&1
        if errorlevel 1 ( echo   已摘除链接 !MODDIR!\example.tutorial ) else ( echo   [警告] 链接摘除失败，请手动删除：!MODDIR!\example.tutorial )
      )
    )
    echo   [完成] 链接已摘除；工坊/内容目录里的原始文件一个都没动。
  ) else (
    echo   已跳过：链接保留（BepInEx\ dotnet\ 仍指向内容目录）。
  )
)

if exist "!GAME!USERENV.dll" (
  echo.
  echo [可选] 检测到游戏目录里有 Steam 直启文件 USERENV.dll。
  set "ANS="
  set /p "ANS=是否一并卸载 Steam 直启（USERENV.dll 与 userenv_orig.dll）？[Y/N]: "
  set "ANS=!ANS:~0,1!"
  if /i "!ANS!"=="Y" (
    tasklist /nh /fi "imagename eq WorldApart.exe" 2>nul | findstr /i "WorldApart.exe" >nul
    if not errorlevel 1 (
      echo [错误] 游戏正在运行，无法删除 Steam 直启文件；请退出游戏后重跑本脚本。
    ) else (
      del /f /q "!GAME!USERENV.dll" 2>nul
      del /f /q "!GAME!userenv_orig.dll" 2>nul
      if exist "!GAME!USERENV.dll" ( echo [警告] USERENV.dll 删除失败，请手动删除。 ) else ( echo   已删除 USERENV.dll )
      if exist "!GAME!userenv_orig.dll" ( echo [警告] userenv_orig.dll 删除失败，请手动删除。 ) else ( echo   已删除 userenv_orig.dll )
      echo   Steam 点「开始游戏」已恢复为原版。
    )
  ) else (
    echo   已跳过：Steam 直启文件保留，Steam 点「开始游戏」仍是 MOD 版。
  )
)
pause
