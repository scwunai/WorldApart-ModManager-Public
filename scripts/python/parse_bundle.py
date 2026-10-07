# -*- coding: utf-8 -*-
"""用 UnityPy 解析 Luban 表 bundle，导出 tbnpcbasecfg 等表的真实行数据。"""
import sys, json, os

import UnityPy

BUNDLE = r"<G>\WorldApart_Data\StreamingAssets\yoo\DefaultPackage\b584dd66ff433fa742dc6829f2368641.bundle"
OUTDIR = r"<G>\KIMI\bundle_out"
os.makedirs(OUTDIR, exist_ok=True)

env = UnityPy.load(BUNDLE)
objs = [(o.name, o.type.name, o.path_id) for o in env.objects]
print("object count:", len(objs))
from collections import Counter
print(Counter(t for _, t, _ in objs))

# 先列名字，找表资源
names = sorted(set(n for n, t, _ in objs))
for n in names:
    print(n)
