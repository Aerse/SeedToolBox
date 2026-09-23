# Generates src/SeedToolBox/Assets/pinyin.bin for Launcher/Pinyin.cs
#
# Format (little endian):
#   int32  syllable count N
#   N x    UTF-8 syllable, each terminated by '\n'
#   ushort per code point U+4E00..U+9FFF: 0 = no reading, otherwise 1-based syllable index
#
# Polyphonic characters use pypinyin's most common reading.
# Usage: pip install pypinyin && python tools/gen_pinyin.py

import os
import struct
from pypinyin import Style, pinyin

FIRST, LAST = 0x4E00, 0x9FFF
out = os.path.join(os.path.dirname(__file__), '..', 'src', 'SeedToolBox', 'Assets', 'pinyin.bin')

syllables = []
index = {}
table = []
for code in range(FIRST, LAST + 1):
    ch = chr(code)
    py = pinyin(ch, style=Style.NORMAL, errors='ignore')
    s = py[0][0].lower() if py and py[0] and py[0][0] != ch else ''
    s = s.replace('ü', 'v')
    if not s or not s.isascii() or not s.isalpha():
        table.append(0)
        continue
    if s not in index:
        syllables.append(s)
        index[s] = len(syllables)
    table.append(index[s])

with open(out, 'wb') as f:
    f.write(struct.pack('<i', len(syllables)))
    for s in syllables:
        f.write(s.encode('utf-8') + b'\n')
    f.write(struct.pack(f'<{len(table)}H', *table))

print(f'{len(syllables)} syllables, {sum(1 for t in table if t)} chars, {os.path.getsize(out)} bytes')
