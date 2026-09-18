# -*- coding: utf-8 -*-
# 更新日志 md -> 记事本可直接打开的 txt（UTF-8 带 BOM + CRLF）
# 由 tools/release.ps1 在打包流程里自动调用，保证 md 与 txt 不会两份漂移。
import io, os, sys
repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
src = os.path.join(repo, 'docs', '更新日志.md')
dst = os.path.join(repo, 'docs', '更新日志.txt')
out = []
for line in io.open(src, encoding='utf-8'):
    t = line.rstrip('\n').rstrip().replace('**', '')
    if t.startswith('# '):
        out.append('=' * 68); out.append(t[2:]); out.append('=' * 68)
    elif t.startswith('## '):
        out.append(''); out.append('■ ' + t[3:])
    elif t.startswith('### '):
        out.append('  --- ' + t[4:] + ' ---')
    elif t.startswith('- '):
        out.append('  · ' + t[2:])
    elif t.startswith('|'):
        cells = [c.strip() for c in t.strip('|').split('|')]
        if all(set(c) <= set('-: ') for c in cells):
            continue
        out.append('  ' + ' ｜ '.join(cells))
    else:
        out.append(t)
io.open(dst, 'w', encoding='utf-8-sig', newline='\r\n').write('\n'.join(out))
print('  TXT = ' + str(os.path.getsize(dst)) + ' bytes, ' + str(len(out)) + ' lines')
