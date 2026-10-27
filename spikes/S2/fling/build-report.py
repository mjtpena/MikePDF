# Assembles docs/spikes/S2.md from a narrative template and the tables produced by summarize.py (so raw numbers are copied by code, not by hand).
# usage: python -I build-report.py report-template/part1.md report-template/part2.md ../../../docs/spikes/S2.md   (templates hold the narrative; {{name}} markers are replaced by tables)
import os, sys
sys.path.insert(0, os.path.dirname(__file__))
import summarize as S

def sections(text):
    parts = {}; cur = None
    for line in text.split(chr(10)):
        if line.startswith('**'):
            cur = line.strip('*').split('**')[0].strip(); parts[cur] = []
        elif line.startswith('|') and cur is not None:
            parts[cur].append(line)
    return {k: chr(10).join(v) for k, v in parts.items()}

eng = sections(S.engine_tables())
def pick(prefix):
    for k, v in eng.items():
        if k.startswith(prefix): return v
    raise KeyError(prefix)

tables = {
    'engine_tiles': pick('render ms per tile, page already loaded, non-blank'),
    'engine_first': pick('first tile of a page'),
    'engine_ttft': pick('warm TTFT inside the engine'),
    'ecore': S.ecore_table(), 'cold': S.cold_table(), 'prog': S.progressive_compact(),
    'transport': S.transport_table(), 'upload': S.upload_table(), 'memory': S.memory_table(),
    'core': S.fling_core(('fling-',)), 'extras': S.fling_core(('x-', 'hidpi-', 'r1like-')),
    'zoom': S.zoom_table(), 'ttft': S.ttft_table(), 'tier': S.tier_table(),
}
text = open(sys.argv[1], encoding='utf-8').read() + open(sys.argv[2], encoding='utf-8').read()
for k, v in tables.items():
    text = text.replace('{{' + k + '}}', v)
assert '{{' not in text, [l for l in text.split(chr(10)) if '{{' in l]
open(sys.argv[3], 'w', encoding='utf-8', newline=chr(10)).write(text)
print('wrote', sys.argv[3], len(text), 'chars')
