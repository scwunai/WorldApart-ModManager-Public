# -*- coding: utf-8 -*-
"""P10 任务5：打 v14.3 发布包（沿袭 build_pack_v142.py 的打法）。

v14.2\\ 目录（= v14.2 zip 的同内容源）→ v14.3\\
  * 放入新的「符号链接版安装脚本」+ 替换增强后的「卸载_MOD环境.bat」（两者来自 build_v14_3\\src_utf8\\，统一转 GBK+CRLF）
  * 安装说明.md 追加「复制版 vs 符号链接版」与「v14.3 变更」两节
  * 复核 BepInEx\\interop（154 条目，随复制继承）
  * 打 <codename>\\release\\不问凡尘_MOD环境_v14.3.zip → 校验三插件 MD5 / 条目数 / 行尾编码 / 红线
  * 打印 zip 的 字节数 / MD5 / SHA256

不改游戏目录既有文件；不改 v14.2 目录与 v14.2 zip。
"""
import hashlib
import io
import os
import shutil
import zipfile

BASE = r'<G>'                                          # 游戏安装根（本机路径已脱敏）
RELEASE = os.path.join(BASE, r'<codename>\release')
src = os.path.join(RELEASE, '不问凡尘_MOD环境_v14.2')
dst = os.path.join(RELEASE, '不问凡尘_MOD环境_v14.3')
zip_path = dst + '.zip'
G = BASE
SRC_UTF8 = os.path.join(BASE, r'<codename>\build_v14_3\src_utf8')
GBK_OUT = os.path.join(BASE, r'<codename>\build_v14_3\gbk')

INSTALLER = '安装_MOD环境_符号链接版.bat'
UNINSTALL = '卸载_MOD环境.bat'
NEW_FILES = (INSTALLER, UNINSTALL)

log = io.StringIO()


def w(s=''):
    log.write(s + '\n')


def md5b(b):
    return hashlib.md5(b).hexdigest()


def sha256b(b):
    return hashlib.sha256(b).hexdigest()


def gbk_crlf(text):
    text = text.replace('\r\n', '\n').replace('\r', '\n')
    return text.replace('\n', '\r\n').encode('gbk')


NOTE_SECTIONS = """

---

## 复制版 vs 符号链接版 —— 两个安装脚本怎么选

包里现在有**两个安装脚本**，装出来的 MOD 环境功能完全一样，区别只在「内容怎么进游戏目录」：

| | `安装_MOD环境.bat`（复制版） | `安装_MOD环境_符号链接版.bat`（符号链接版） |
|---|---|---|
| 内容如何进游戏目录 | 把 `BepInEx\\`、`dotnet\\`、示例 MOD **整份复制**过去 | 用目录联接（`mklink /J`）把内容目录**挂**进游戏目录 |
| 创意工坊内容更新以后 | **必须重跑一次脚本**，把新内容再复制一遍 | 游戏目录**自动跟随**，不用重装（游戏目录和工坊内容就是同一份文件） |
| 额外占磁盘 | 内容在游戏目录里再占一份 | 不额外占（只是一条链接记录） |
| 内容目录放哪 | 放哪都行，包括直接放进游戏目录 | 建议放在游戏目录**之外**（例如创意工坊内容目录） |
| 需要管理员吗 | 不需要 | 不需要（联接 `mklink /J` 免管理员；只有退而求其次的 `/D` 符号链接才需要管理员/开发者模式） |
| 装完得到的东西 | 一样：启动器文件 + 三个插件 DLL + 示例 MOD | 一样 |

**符号链接版怎么跑**

1. 双击 `安装_MOD环境_符号链接版.bat`：内容目录默认就是脚本自己所在的目录；
2. 也可以**把内容目录拖到这个脚本上**运行（例如把
   `...\\steamapps\\workshop\\content\\4209920\\3814675406` 整个文件夹拖到脚本图标上）——拖进去的目录就是内容目录；
3. 包不在游戏目录里时，脚本会问一次游戏目录，然后逐项挂载。

**逐项自动回退**：每一项都按「联接 → 符号链接 → 复制」的顺序试。哪一项挂不上（目标在网络盘上、系统不允许符号链接、目标位置被真实目录占着又改不了名、内容目录与游戏目录相同……），
就在那一项下面打印一行 `[回退] … 原因`，并改为复制 —— 装完照样能用，只是那一项不再自动跟随更新。

启动器小文件（`modlauncher.exe`、`modprobe.dll`、`doorstop_config.ini`、`inject.ini`、`steam_appid.txt`、`启动MOD版.bat`、`WorldApart_mod.exe`）
两种脚本都是复制，因为它们体积很小、也不含 MOD 内容。

**卸载**：两种脚本都用 `卸载_MOD环境.bat`。它会认出游戏目录里哪些是链接，问一句是否一并摘除；
**摘链接一律用不带 `/s` 的 `rmdir`，只摘链接本身，绝不会递归删掉创意工坊里的源内容**。

**符号链接版的边界**：如果 Steam 把创意工坊内容目录整个**改名或换到新路径**（不是原地改文件），链接会悬空 —— 重跑一次符号链接版脚本就能恢复。
原地增、删、改文件不受影响，会立刻生效。

---

## v14.3 变更（相对 v14.2）

- **新增符号链接版安装脚本 `安装_MOD环境_符号链接版.bat`**：把内容目录（`BepInEx\\`、`dotnet\\`、示例 MOD）以**目录联接**挂进游戏目录。
  创意工坊内容一更新，游戏目录立刻跟随，**不用重新复制、不用重装**；每一项失败都会自动回退到复制并打印原因。
- **`安装_MOD环境.bat`（复制版）保持原样、一字未改**：老行为完全保留。两个脚本的适用场景见上一节。
- **`卸载_MOD环境.bat` 增强**：如果是符号链接版装的，会检测游戏目录里的 `BepInEx\\`、`dotnet\\`（以及 Mods 里的示例 MOD）是不是链接，
  并询问是否一并摘除；摘除只用不带 `/s` 的 `rmdir`，**不会删到工坊源内容**。
- **可行性已实测**（沙箱，非管理员）：目录联接 `mklink /J` 免管理员即可创建、能透过它读到源文件、源目录原地增删改会即时反映；
  `mklink /D` 目录符号链接需要管理员或开发者模式；文件硬链接不能跨卷；联接**不能**指向网络路径；源目录改名/换路径会让联接悬空。
- 其余与 v14.2 相同：三个插件 DLL、`BepInEx\\interop\\`（154 个互操作程序集，解压即用）、示例 MOD、安装时可选 Steam 直启版（`[1] 普通版` / `[2] Steam 直启版`）。
- 包内 `.bat` 一律 GBK，`.md` 一律 UTF-8（无 BOM）+ CRLF。
"""

FORBIDDEN = ('FrameProbe', 'proxy_inject', 'modprobe.txt')


def main():
    w('== 1) 复制源目录 v14.2 -> v14.3 ==')
    if os.path.exists(dst):
        shutil.rmtree(dst)
    if os.path.exists(zip_path):
        os.remove(zip_path)
    shutil.copytree(src, dst)
    n_src = sum(len(f) for _r, _d, f in os.walk(dst))
    w('   files copied: %d' % n_src)

    w('== 2) 放入 v14.3 的两个 .bat（UTF-8 源 -> GBK + CRLF）==')
    os.makedirs(GBK_OUT, exist_ok=True)
    bat_md5 = {}
    for name in NEW_FILES:
        text = io.open(os.path.join(SRC_UTF8, name), encoding='utf-8').read()
        data = gbk_crlf(text)
        with open(os.path.join(GBK_OUT, name), 'wb') as f:
            f.write(data)
        with open(os.path.join(dst, name), 'wb') as f:
            f.write(data)
        bat_md5[name] = md5b(data)
        w('   %-32s %6d B  MD5=%s  CRLF=%d bareLF=%d 非ASCII行=%d'
          % (name, len(data), bat_md5[name], data.count(b'\r\n'),
             data.count(b'\n') - data.count(b'\r\n'),
             sum(1 for l in data.decode('gbk').split('\r\n')
                 if any(ord(c) > 127 for c in l))))
        assert data.count(b'\n') == data.count(b'\r\n'), 'bare LF in ' + name
        assert data[:3] != b'\xef\xbb\xbf', 'BOM in ' + name

    w('== 3) 复核 BepInEx\\interop（154 条目）==')
    shutil.copytree(os.path.join(G, 'BepInEx', 'interop'),
                    os.path.join(dst, 'BepInEx', 'interop'), dirs_exist_ok=True)
    n_interop = len(os.listdir(os.path.join(dst, 'BepInEx', 'interop')))
    w('   interop files: %d' % n_interop)

    w('== 4) 安装说明.md 追加「复制版 vs 符号链接版」+「v14.3 变更」==')
    note_path = os.path.join(dst, '安装说明.md')
    data = open(note_path, 'rb').read()
    sep = b'\r\n' if b'\r\n' in data else b'\n'
    data = data.replace(sep, b'\r\n') + NOTE_SECTIONS.replace('\r\n', '\n').replace('\n', '\r\n').encode('utf-8')
    with open(note_path, 'wb') as f:
        f.write(data)
    w('   install note bytes: %d' % len(data))

    w('== 5) 打 zip ==')
    count = 0
    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for root, dirs, files in os.walk(dst):
            dirs.sort()
            for fn in sorted(files):
                full = os.path.join(root, fn)
                rel = os.path.relpath(full, os.path.dirname(dst)).replace(os.sep, '/')
                z.write(full, rel)
                count += 1
    w('   entries written: %d' % count)

    raw = open(zip_path, 'rb').read()
    w('   zip size  : %d bytes (%.1f MB)' % (len(raw), len(raw) / 1048576.0))
    w('   zip md5   : %s' % md5b(raw))
    w('   zip sha256: %s' % sha256b(raw))

    w('== 6) 校验 ==')
    ok = True
    expect = {
        'LocalModManager.dll': '0ee671c2c5bcc63b31c349761881c351',
        'LocalModManager.Abstractions.dll': 'fa6f7eb42d27d604db1067c82870b12c',
        'LocalStoryDebug.dll': '6592251165fb86987b0e4d398cbd1a4c',
    }
    with zipfile.ZipFile(zip_path) as z:
        names = z.namelist()
        for fn, want in expect.items():
            zn = '不问凡尘_MOD环境_v14.3/BepInEx/plugins/' + fn
            if zn not in names:
                w('   [FAIL] %s missing in zip' % zn)
                ok = False
                continue
            h_zip = md5b(z.read(zn))
            h_dep = md5b(open(os.path.join(G, 'BepInEx', 'plugins', fn), 'rb').read())
            good = (h_zip == want == h_dep)
            ok = ok and good
            w('   %-34s zip=%s deployed=%s %s'
              % (fn, h_zip, h_dep, 'OK' if good else 'MISMATCH(want %s)' % want))
        bad = [n for n in names if 'FrameProbe' in n or n.endswith('.log')
               or 'BepInEx/cache' in n or 'proxy_inject' in n or n.endswith('modprobe.txt')]
        w('   forbidden entries: %s' % (bad if bad else 'NONE'))
        ok = ok and not bad
        n_zip_interop = len([n for n in names if '/BepInEx/interop/' in n])
        w('   interop entries in zip: %d' % n_zip_interop)
        ok = ok and n_zip_interop == 154

        # 新脚本 / 卸载脚本 在包内且字节 == 本地 gbk 输出
        for fn in NEW_FILES:
            zn = '不问凡尘_MOD环境_v14.3/' + fn
            if zn not in names:
                w('   [FAIL] %s missing in zip' % zn)
                ok = False
                continue
            got = z.read(zn)
            same = md5b(got) == bat_md5[fn]
            ok = ok and same
            w('   %-34s in zip: %d B  MD5=%s  %s' % (fn, len(got), md5b(got),
                                                     'OK' if same else 'MISMATCH'))
        # 复制版脚本必须一字未改（与 v14.2 目录里的副本逐字节相同）
        src_copy = open(os.path.join(src, '安装_MOD环境.bat'), 'rb').read()
        zip_copy = z.read('不问凡尘_MOD环境_v14.3/安装_MOD环境.bat')
        same_copy = src_copy == zip_copy
        ok = ok and same_copy
        w('   复制版 安装_MOD环境.bat 与 v14.2 逐字节相同: %s (MD5=%s)'
          % ('OK' if same_copy else 'CHANGED!!', md5b(zip_copy)))

        need = ['/安装_MOD环境.bat', '/安装_MOD环境_符号链接版.bat', '/卸载_MOD环境.bat',
                '/安装说明.md', '/Steam直启/USERENV.dll', '/Steam直启/userenv_orig.dll',
                '/Steam直启/安装到本目录.bat', '/Steam直启/卸载_Steam直启.bat', '/Steam直启/说明.md']
        for suf in need:
            hit = [n for n in names if n.endswith(suf)]
            w('   %-40s %s' % (suf, 'OK' if hit else 'MISSING'))
            ok = ok and bool(hit)

        txt = z.read('不问凡尘_MOD环境_v14.3/安装说明.md').decode('utf-8')
    for sec in ('## v14.3 变更（相对 v14.2）', '## 复制版 vs 符号链接版'):
        has = sec in txt
        w('   note has %-28s %s' % (sec, 'OK' if has else 'MISSING'))
        ok = ok and has

    files_on_disk = sum(len(f) for _r, _d, f in os.walk(dst))
    w('   files on disk: %d   zip entries: %d' % (files_on_disk, count))
    ok = ok and files_on_disk == count

    w('== 7) 红线自检（新增/改动文件：零内部工程代号、零本机绝对路径）==')
    NEWCHECK = ('不问凡尘_MOD环境_v14.3/安装_MOD环境_符号链接版.bat',
                '不问凡尘_MOD环境_v14.3/卸载_MOD环境.bat',
                '不问凡尘_MOD环境_v14.3/安装说明.md')
    _CODE = 'k' + 'i' + 'm' + 'i'   # 内部工程代号：拆开书写，免得工件自己命中红线
    BAD = [_CODE, 'c:' + os.sep + 'users', 'd:' + os.sep + 'steamlibrary',
           os.path.join('steamapps', 'common', 'a1')]
    with zipfile.ZipFile(zip_path) as z:
        for zn in NEWCHECK:
            t = z.read(zn).decode('gbk' if zn.endswith('.bat') else 'utf-8')
            low = t.lower()
            hits = sorted({b for b in BAD if b in low})
            w('   %-58s codename=%d  hits=%s' % (zn.split('/', 1)[1], low.count(_CODE),
                                                 hits or 'NONE'))
            ok = ok and not hits and not low.count(_CODE)

    w('')
    w('ALL_OK' if ok else 'CHECK_FAILED')
    os.makedirs(os.path.join(BASE, r'<codename>\p10'), exist_ok=True)
    with open(os.path.join(BASE, r'<codename>\p10\build_pack_v143.log'), 'w',
              encoding='utf-8', newline='') as f:
        f.write(log.getvalue())
    print(log.getvalue())
    return 0 if ok else 1


if __name__ == '__main__':
    raise SystemExit(main())
