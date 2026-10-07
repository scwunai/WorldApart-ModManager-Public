# -*- coding: utf-8 -*-
import glob, os
base = r'%USERPROFILE%\AppData\LocalLow\Nuverse\WorldApart\StorageV1\1.0\1c19f5e3-8e28-46da-9a06-3168b12f9359\slots'
files = sorted(glob.glob(base + '/*/gameworld.msgpack'), key=os.path.getmtime, reverse=True)
print('slots:', [os.path.basename(os.path.dirname(f)) for f in files])
frag = '先垫半句场面话'.encode('utf-8')
frag2 = '句子节奏'.encode('utf-8')
for f in files[:4]:
    data = open(f, 'rb').read()
    print(os.path.basename(os.path.dirname(f)), len(data),
          'persona快照?', (frag in data) or (frag2 in data),
          'storytest?', b'storytest' in data,
          'mod痕迹?', any(x in data for x in (b'modIdMap', b'ModStamp', b'com.kimi')))
