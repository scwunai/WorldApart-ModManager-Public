# -*- coding: utf-8 -*-
"""环境验收检查：IFEO / settings.json MOD 键 / 关键路径存在性"""
import winreg, os, json

print('== IFEO ==')
base = r'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
found = False
for hive, name in [(winreg.HKEY_LOCAL_MACHINE, 'HKLM'), (winreg.HKEY_CURRENT_USER, 'HKCU')]:
    try:
        k = winreg.OpenKey(hive, base)
    except OSError:
        continue
    i = 0
    while True:
        try:
            sub = winreg.EnumKey(k, i)
        except OSError:
            break
        i += 1
        if 'WorldApart' in sub or 'wastart' in sub.lower():
            found = True
            sk = winreg.OpenKey(hive, os.path.join(base, sub))
            vals = {}
            j = 0
            while True:
                try:
                    vn, vv, _ = winreg.EnumValue(sk, j)
                except OSError:
                    break
                vals[vn] = vv
                j += 1
            print(name, sub, vals)
    winreg.CloseKey(k)
if not found:
    print('!! 未找到任何 WorldApart IFEO 项')

print('== settings.json MOD 键 ==')
sj = r'%USERPROFILE%\AppData\LocalLow\Nuverse\WorldApart\settings.json'
d = json.load(open(sj, encoding='utf-8-sig'))
for k in sorted(d):
    if 'mod' in k.lower():
        print(' ', k, '=', d[k])

print('== 关键路径 ==')
G = r'<G>'
for p in [os.path.join(G, 'WorldApart_mod.exe'),
          os.path.join(G, 'KIMI', 'injector', 'wastart.exe'),
          os.path.join(G, 'KIMI', 'injector', 'modprobe.dll'),
          os.path.join(G, 'KIMI', 'injector', 'inject.ini'),
          os.path.join(G, '启动MOD版.bat'),
          os.path.join(G, 'BepInEx', 'core', 'BepInEx.dll')]:
    print(' ', 'OK ' if os.path.exists(p) else 'MISS', p)
