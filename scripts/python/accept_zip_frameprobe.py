# -*- coding: utf-8 -*-
import zipfile, sys
sys.stdout.reconfigure(encoding='utf-8')
zf = zipfile.ZipFile(r'<G>\KIMI\release\不问凡尘_MOD环境_v1.6.zip')
names = zf.namelist()
sep = chr(92)
print('FrameProbe hits:', [n for n in names if 'frameprobe' in n.lower()])
pl = [n for n in names if (sep + 'plugins' + sep) in n]
print('plugins entries (%d):' % len(pl))
for n in pl: print('  ', n)
