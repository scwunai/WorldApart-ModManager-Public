# -*- coding: utf-8 -*-
"""v1.6 发布包验收：zip 完整性、条目数、去 kimi 扫描、工坊 content 与 v1.5 源树一致性"""
import zipfile, os, hashlib, sys

G = r'<G>'
K = os.path.join(G, 'KIMI')
zip_path = os.path.join(K, 'release', '不问凡尘_MOD环境_v1.6.zip')
tree = os.path.join(K, 'release', '不问凡尘_MOD环境_v1.5')  # v1.6 与 v1.5 同树
ws = os.path.join(K, 'release', 'BepInEx适配，环境，MOD管理器，示例MOD和相关调查文档')

sys.stdout.reconfigure(encoding='utf-8')

print('== zip 基本信息 ==')
zf = zipfile.ZipFile(zip_path)
bad = zf.testzip()
names = zf.namelist()
total = os.path.getsize(zip_path)
print('size MB = %.1f' % (total / 1048576), ' entries =', len(names), ' testzip bad =', bad)

print('== zip 内去 kimi 扫描（文件名 + 文本内容） ==')
hits = 0
for n in names:
    if 'kimi' in n.lower():
        print('  NAME HIT:', n); hits += 1
    low = n.lower()
    if low.endswith(('.txt', '.md', '.json', '.bat', '.ps1', '.vdf', '.cfg', '.cs', '.ini')):
        try:
            t = zf.read(n)
            for enc in ('utf-8', 'gbk'):
                try:
                    s = t.decode(enc); break
                except Exception:
                    s = None
            if s and 'kimi' in s.lower():
                print('  TEXT HIT:', n); hits += 1
        except Exception as e:
            print('  read err', n, e)
print('kimi hits =', hits)

print('== zip 内 DLL 插件 MD5 ==')
for n in names:
    if n.endswith('LocalModManager.dll'):
        d = zf.read(n)
        print(' ', n, hashlib.md5(d).hexdigest(), len(d))

print('== 工坊 content 与源树一致性（抽样关键文件）==')
def md5(p):
    return hashlib.md5(open(p, 'rb').read()).hexdigest()
inner = None
for dp, dn, fn in os.walk(ws):
    if 'content' in dn:
        inner = os.path.join(dp, 'content'); break
print('content dir =', inner)
pairs = [
    (os.path.join(tree, 'BepInEx', 'plugins', 'LocalModManager.dll'), None),
    (os.path.join(tree, 'mod.json'), None),
]
for src, _ in pairs:
    rel = os.path.relpath(src, tree)
    dst = os.path.join(inner, rel)
    if os.path.exists(dst):
        a, b = md5(src), md5(dst)
        print(' ', rel, 'MATCH' if a == b else 'DIFF', a[:12], b[:12])
    else:
        print(' ', rel, 'MISSING in content')
# workshop meta files
for f in os.listdir(ws):
    print(' ws root:', f)
