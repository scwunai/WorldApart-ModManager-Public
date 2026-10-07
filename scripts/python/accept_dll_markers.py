# -*- coding: utf-8 -*-
"""字符串标记检查：判断 DLL 包含哪些版本的代码"""
import sys

def strings_of(path):
    data = open(path, 'rb').read()
    out = set()
    # UTF-16LE strings
    cur = []
    for i in range(0, len(data) - 1, 2):
        c = data[i] | (data[i+1] << 8)
        if 32 <= c < 0xD800:
            cur.append(chr(c))
        else:
            if len(cur) >= 6:
                out.add(''.join(cur))
            cur = []
    # ASCII strings
    cur = []
    for b in data:
        if 32 <= b < 127:
            cur.append(chr(b))
        else:
            if len(cur) >= 6:
                out.add(''.join(cur))
            cur = []
    return out

markers = [
    'Mod Manager',           # v6 基线日志前缀
    'MOD_DEBUG_WRITETEST',   # v7 P0
    'JUMPSELFTEST',          # v7 P1/P2
    'TryStartQuestProc',     # v7 P2
    'RowStepPerDetent',      # v8 滚动修复
    'ScrollRect',            # v8
    'MOD_EXP_VIDEO',         # v5 视频
    'MOD tab clicked',       # MOD 页 bug 观察
    'OnScroll',
    'ForceReloadTables',
]

for path in sys.argv[1:]:
    ss = strings_of(path)
    print('==', path.split('\\')[-1])
    for m in markers:
        hits = [s for s in ss if m in s][:3]
        print(('  YES ' if hits else '  no  '), m, ('| e.g. ' + hits[0][:60] if hits else ''))
