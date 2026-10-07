# -*- coding: utf-8 -*-
"""工坊 content 与 v1.5 源树全量对账 + 工坊元文件检查"""
import os, hashlib, sys
sys.stdout.reconfigure(encoding='utf-8')

K = r'<G>\KIMI'
tree = os.path.join(K, 'release', '不问凡尘_MOD环境_v1.5')
ws_inner = os.path.join(K, 'release', 'BepInEx适配，环境，MOD管理器，示例MOD和相关调查文档',
                        'BepInEx适配，环境，MOD管理器，示例MOD和相关调查文档')
content = os.path.join(ws_inner, 'content')

def snapshot(root):
    out = {}
    for dp, dn, fn in os.walk(root):
        for f in fn:
            p = os.path.join(dp, f)
            out[os.path.relpath(p, root).lower()] = (os.path.getsize(p), hashlib.md5(open(p, 'rb').read()).hexdigest())
    return out

a = snapshot(tree)
b = snapshot(content)
only_tree = sorted(set(a) - set(b))
only_content = sorted(set(b) - set(a))
diff = sorted(k for k in set(a) & set(b) if a[k] != b[k])
print('tree files =', len(a), ' content files =', len(b))
print('only in tree =', len(only_tree))
for x in only_tree[:20]: print('  T', x)
print('only in content =', len(only_content))
for x in only_content[:20]: print('  C', x)
print('content-diff =', len(diff))
for x in diff[:20]: print('  D', x)

print('== 工坊元文件 ==')
for f in sorted(os.listdir(ws_inner)):
    p = os.path.join(ws_inner, f)
    if os.path.isfile(p):
        print(' ', f, os.path.getsize(p))
