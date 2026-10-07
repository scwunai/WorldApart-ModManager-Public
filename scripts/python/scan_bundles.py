# -*- coding: utf-8 -*-
"""扫描全部 bundle 的容器清单，找出本地化/字符串资源所在位置。"""
import glob, io, os, json
import UnityPy

base = r'<G>\WorldApart_Data\StreamingAssets'
bundles = glob.glob(base + '/**/*.bundle', recursive=True)
print('bundles:', len(bundles))
index = {}
for i, b in enumerate(bundles):
    try:
        raw = open(b, 'rb').read()
        if raw[:7] == b'A1BNDLH':
            raw = raw[24:]
        env = UnityPy.load(io.BytesIO(raw))
        for o in env.objects:
            if o.type.name == 'AssetBundle':
                for p, _ in o.read_typetree()['m_Container']:
                    index.setdefault(p, b)
                break
    except Exception as e:
        pass
    if i % 50 == 0:
        print(i, 'scanned, index size', len(index))
json.dump(index, open(r'<G>\KIMI\bundle_index.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
print('done, total paths:', len(index))
# 搜本地化相关
for p in sorted(index):
    lp = p.lower()
    if any(k in lp for k in ('local', 'string', 'lang', 'text')) and '/luban/' not in lp:
        print(p, '->', os.path.basename(index[p]))
