# Aggregates spikes/S2/out/results/** into markdown tables (python -I summarize.py <what>). Pure stdlib.
import csv, glob, json, os, statistics, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', 'out', 'results'))

def f(x, n=2):
    if x is None: return '-'
    if isinstance(x, float): return f'{x:.{n}f}'
    return str(x)

def load_viewer(tagfilter=None):
    runs = []
    for p in glob.glob(os.path.join(ROOT, os.environ.get('S2_VIEWER_DIR', 'viewer'), '*', '*', 'summary.json')):
        parts = p.replace('\\', '/').split('/')
        grp, label = parts[-3], parts[-2]
        if tagfilter and tagfilter not in grp: continue
        try: d = json.load(open(p, encoding='utf-8'))
        except Exception: continue
        runs.append((grp, label, d))
    return sorted(runs, key=lambda r: (r[0], r[1]))

def adapter_used(d):
    g = d.get('mem', {}).get('gpuPeak', {})
    best = max(g.items(), key=lambda kv: kv[1]['localMB'] + kv[1]['nonLocalMB'], default=(None, None))
    return best[0] or '?'

def short_adapter(n):
    if not n: return '?'
    return 'iGPU' if 'Intel' in n else ('dGPU' if 'NVIDIA' in n else n)

def fling_table(filt=lambda label: label.startswith(('fling-', 'x-', 'hidpi-', 'r1like-'))):
    rows = ['| group | run | adapter | frames | p50 | p95 | p99 | max | >16.7 | >25 | >33.4 | >50 | blank frames | low-res-only frames | max blank px | sharp after end (ms) | tile req->ready p50/p95 | thumb req->ready p50/p95 | upload p50/p95/max | GPU local peak MB | WS peak MB | cancel calls | evicted |', '|' + '---|' * 23]
    for grp, label, d in load_viewer():
        if not filt(label) or 'frames' not in d: continue
        fr = d['frames']; iv = fr['intervalMs']; pl = d['pipeline']
        ad = adapter_used(d)
        gl = d['mem']['gpuPeak'].get(ad, {}).get('localMB', 0)
        rows.append(f"| {grp} | {label} | {short_adapter(ad)} | {fr['frames']} | {f(iv['P50'])} | {f(iv['P95'])} | {f(iv['P99'])} | {f(iv['Max'])} | {fr['over16_7']} | {fr.get('over25')} | {fr['over33_4']} | {fr['over50']} | {fr['blankFrames']} | {fr['lowResOnlyFrames']} | {f(fr['maxBlankPx'],0)} | {f(d['fling']['sharpAfterEndMs'])} | {f(pl['tileRequestToReadyMs']['P50'])}/{f(pl['tileRequestToReadyMs']['P95'])} | {f(pl['thumbRequestToReadyMs']['P50'])}/{f(pl['thumbRequestToReadyMs']['P95'])} | {f(pl['uploadTileMs']['P50'],3)}/{f(pl['uploadTileMs']['P95'],3)}/{f(pl['uploadTileMs']['Max'],3)} | {f(gl,0)} | {f(d['mem']['peakWsMB'],0)} | {pl['cancelCalls']} | {pl['evicted']} |")
    return '\n'.join(rows)

def upload_table():
    rows = ['| group | run | adapter | tile | n | upload call p50 | p95 | max | upload+GPU sync p50 | p95 | max | slot->array copy p50 | engine req->slot p50 | GPU local bytes/tile (measured) | GPU non-local bytes/tile | nominal bytes/tile | CPU private bytes/tile | GPU local freed afterwards (MB delta) |', '|' + '---|' * 18]
    for grp, label, d in load_viewer():
        if 'upload' not in d: continue
        u = d['upload']; ad = adapter_used(d)
        pa = next((a for a in u['perAdapter'] if short_adapter(a['Name']) == short_adapter(ad)), u['perAdapter'][0])
        rows.append(f"| {grp} | {label} | {short_adapter(ad)} | {u['tile']} | {u['n']} | {f(u['uploadCallMs']['P50'],3)} | {f(u['uploadCallMs']['P95'],3)} | {f(u['uploadCallMs']['Max'],3)} | {f(u['uploadPlusGpuSyncMs']['P50'],3)} | {f(u['uploadPlusGpuSyncMs']['P95'],3)} | {f(u['uploadPlusGpuSyncMs']['Max'],3)} | {f(u['slotToManagedCopyMs']['P50'],3)} | {f(u['engineRequestToSlotReadyMs']['P50'],3)} | {f(pa['localBytesPerTile'],0)} | {f(pa['nonLocalBytesPerTile'],0)} | {f(u['nominalBytesPerTile'],0)} | {f(u['cpuBytesPerTile']['privatePerTileBytes'],0)} | {f(pa['localAfterFreeDeltaMB'],1)} |")
    return '\n'.join(rows)

def ttft_table():
    hdr = '| group | run | adapter | iters | state | open RPC p50/p95 | thumb ready p50/p95 | first sharp tile READY p50/p95/max | first sharp tile COMPOSED p50/p95/max | all visible tiles READY p50/p95/max | all visible tiles COMPOSED p50/p95/max |'
    rows = [hdr, '|' + '---|' * 11]
    cold = {}
    def S(t, k):
        v = t[k]; return f"{f(v['P50'])}/{f(v['P95'])}/{f(v['Max'])}"
    for grp, label, d in load_viewer():
        if 'ttft' not in d: continue
        t = d['ttft']
        if t['cold']:
            key = (grp, label.rsplit('-', 1)[0]); cold.setdefault(key, []).append((short_adapter(adapter_used(d)), t)); continue
        rows.append(f"| {grp} | {label} | {short_adapter(adapter_used(d))} | {t['iters']} | warm | {f(t['openRpcMs']['P50'])}/{f(t['openRpcMs']['P95'])} | {f(t['openToThumbReadyMs']['P50'])}/{f(t['openToThumbReadyMs']['P95'])} | {S(t,'openToFirstSharpReadyMs')} | {S(t,'openToFirstSharpComposedMs')} | {S(t,'openToAllSharpReadyMs')} | {S(t,'openToAllSharpComposedMs')} |")
    def pct(v, p):
        v = sorted(v)
        if not v: return 0
        i = p * (len(v) - 1); lo = int(i); hi = min(lo + 1, len(v) - 1); return v[lo] + (v[hi] - v[lo]) * (i - lo)
    for (grp, label), lst in sorted(cold.items()):
        col = lambda k: sorted(x[1]['rows'][0][k] for x in lst)
        def S2(k): v = col(k); return f"{f(pct(v,.5))}/{f(pct(v,.95))}/{f(v[-1])}"
        rows.append(f"| {grp} | {label} (n={len(lst)} launches) | {lst[0][0]} | 1 | cold process (fresh app + engine, OS file cache warm) | {f(pct(col('openRpcMs'),.5))}/{f(pct(col('openRpcMs'),.95))} | {f(pct(col('thumbReadyMs'),.5))}/{f(pct(col('thumbReadyMs'),.95))} | {S2('firstSharpReadyMs')} | {S2('firstSharpComposedMs')} | {S2('allSharpReadyMs')} | {S2('allSharpComposedMs')} |")
    return chr(10).join(rows)

def zoom_table():
    rows = ['| group | run | adapter | transitions | debounce-expiry -> all sharp p50/p95/max | gesture-end -> all sharp p50/p95/max | gesture frame interval p50/p95/max |', '|' + '---|' * 7]
    for grp, label, d in load_viewer():
        if 'zoom' not in d: continue
        z = d['zoom']; a = z['debounceExpiryToAllSharpMs']; b = z['gestureEndToAllSharpMs']; g = z['gestureFrameIntervalMs']
        rows.append(f"| {grp} | {label} | {short_adapter(adapter_used(d))} | {z['transitions']} | {f(a['P50'])}/{f(a['P95'])}/{f(a['Max'])} | {f(b['P50'])}/{f(b['P95'])}/{f(b['Max'])} | {f(g['P50'])}/{f(g['P95'])}/{f(g['Max'])} |")
    return '\n'.join(rows)

def engine_tables(sub='engine-default'):
    base = os.path.join(ROOT, sub)
    out = []
    summ = list(csv.DictReader(open(os.path.join(base, 'summary.csv'))))
    fixtures = sorted({r['fixture'] for r in summ})
    def get(fx, tile, zoom, metric):
        for r in summ:
            if r['fixture'] == fx and r['tile'] == str(tile) and r['zoom'] == str(zoom) and r['metric'] == metric: return r
        return None
    for metric, title in [('render_loaded_page_nonblank_ms', 'render ms per tile, page already loaded, non-blank tiles'), ('render_loaded_page_ms', 'render ms per tile, page already loaded, all sampled tiles (incl. blank margins)')]:
        out.append(f'**{title}** (p50 / p95 / max ms; tiles/s from the mean)\n')
        out.append('| fixture | n | ' + ' | '.join(f'{t}px @{z}%' for t in (256, 512) for z in (100, 200, 400)) + ' |')
        out.append('|' + '---|' * 8)
        for fx in fixtures:
            cells = []; n = '-'
            for t in (256, 512):
                for z in (100, 200, 400):
                    r = get(fx, t, z, metric)
                    if r and int(r['n']) > 0: cells.append(f"{float(r['p50']):.2f} / {float(r['p95']):.2f} / {float(r['max']):.2f} ({float(r['tilesPerSec']):.0f}/s)"); n = r['n'] if n == '-' else n
                    else: cells.append('-')
            out.append(f'| {fx} | {n} | ' + ' | '.join(cells) + ' |')
        out.append('')
    out.append('**first tile of a page (page object already loaded by FPDF_LoadPage, content stream parsed there) and FPDF_LoadPage itself** (p50 / p95 ms)\n')
    out.append('| fixture | LoadPage 512@100% | first tile 512@100% | first tile 512@400% |')
    out.append('|---|---|---|---|')
    for fx in fixtures:
        lp = get(fx, 512, 100, 'load_page_ms'); f1 = get(fx, 512, 100, 'render_first_tile_of_page_ms'); f4 = get(fx, 512, 400, 'render_first_tile_of_page_ms')
        def c(r): return f"{float(r['p50']):.2f} / {float(r['p95']):.2f}" if r else '-'
        out.append(f'| {fx} | {c(lp)} | {c(f1)} | {c(f4)} |')
    out.append('')
    out.append('**warm TTFT inside the engine** (open document -> first tile, FPDF only, n=40; ms)\n')
    out.append('| fixture | tile | open p50 | LoadPage p50 | first tile p50 | TTFT p50 | p95 | max | viewport complete (1200x1000) p50 / p95 |')
    out.append('|---|---|---|---|---|---|---|---|---|')
    for r in csv.DictReader(open(os.path.join(base, 'ttft_warm.csv'))):
        out.append(f"| {r['fixture']} | {r['tile']} | {float(r['open_p50']):.2f} | {float(r['page_p50']):.2f} | {float(r['firstTile_p50']):.2f} | {float(r['ttft_p50']):.2f} | {float(r['ttft_p95']):.2f} | {float(r['ttft_max']):.2f} | {float(r['viewport_p50']):.2f} / {float(r['viewport_p95']):.2f} |")
    out.append('')
    out.append('**progressive rendering** (heaviest 512 tile of the sampled page; n=30 per row; ms)\n')
    out.append('| fixture | zoom | mode | n | p50 | p95 | max | extra |')
    out.append('|---|---|---|---|---|---|---|---|')
    for r in csv.DictReader(open(os.path.join(base, 'progressive.csv'))):
        mode = r['mode']; extra = ''
        if mode.startswith('slice'): extra = f"max pause overshoot {float(r['extra1']):.2f} ms, continues p50 {r['extra2']}"
        elif mode == 'cancel': extra = f"render finished before cancel in {r['extra1']} of 60 (excluded)"
        out.append(f"| {r['fixture']} | {r['zoom']}% | {mode} | {r['n']} | {float(r['p50']):.2f} | {float(r['p95']):.2f} | {float(r['max']):.2f} | {extra} |")
    return '\n'.join(out)

def cold_table():
    p = os.path.join(ROOT, 'engine-cold.csv')
    rows = list(csv.DictReader(open(p, encoding='ascii', errors='ignore')))
    for r in rows: r['fixture'] = r['fixture'].lstrip(chr(92))
    from collections import defaultdict
    g = defaultdict(list)
    for r in rows:
        try: g[(r['fixture'], r['tile'])].append(r)
        except KeyError: pass
    def pct(v, p):
        v = sorted(v); i = p * (len(v) - 1); lo = int(i); hi = min(lo + 1, len(v) - 1); return v[lo] + (v[hi] - v[lo]) * (i - lo)
    out = ['| fixture | tile | n | FPDF_InitLibrary p50 | open p50 | LoadPage p50 | first tile p50 | cold TTFT (excl. init) p50 / p95 / max |', '|---|---|---|---|---|---|---|---|']
    for (fx, t), lst in sorted(g.items()):
        def col(k): return [float(x[k]) for x in lst]
        tt = col('ttft_excl_init_ms')
        out.append(f"| {fx} | {t} | {len(lst)} | {statistics.median(col('init_ms')):.2f} | {statistics.median(col('open_ms')):.2f} | {statistics.median(col('page_ms')):.2f} | {statistics.median(col('firstTile_ms')):.2f} | {pct(tt,.5):.2f} / {pct(tt,.95):.2f} / {max(tt):.2f} |")
    return '\n'.join(out)

def fling_core(prefix=('fling-',), groups=None):
    rows = ['| group | run | frames | interval p50 | p95 | p99 | max (ms) | >16.7 | >25 | >33.4 | >50 | blank frames | low-res-only frames | max blank (px of 1.28M) | sharp after fling end (ms) | tile request to GPU-ready p50 / p95 (ms) | upload call p95 (ms) |', '|' + '---|' * 17]
    for grp, label, d in load_viewer():
        if not label.startswith(prefix) or 'frames' not in d: continue
        if groups and not any(g in grp for g in groups): continue
        fr = d['frames']; iv = fr['intervalMs']; pl = d['pipeline']
        rows.append(f"| {grp} | {label} | {fr['frames']} | {f(iv['P50'])} | {f(iv['P95'])} | {f(iv['P99'])} | {f(iv['Max'])} | {fr['over16_7']} | {fr.get('over25')} | {fr['over33_4']} | {fr['over50']} | {fr['blankFrames']} | {fr['lowResOnlyFrames']} | {f(fr['maxBlankPx'],0)} | {f(d['fling']['sharpAfterEndMs'])} | {f(pl['tileRequestToReadyMs']['P50'])} / {f(pl['tileRequestToReadyMs']['P95'])} | {f(pl['uploadTileMs']['P95'],3)} |")
    return chr(10).join(rows)

def transport_table():
    rows = ['| group | run | request transit p50 / p95 | engine queue p50 / p95 | engine LoadPage p50 / p95 | engine render p50 / p95 | response transit p50 / p95 | slot to managed array copy p50 / p95 | upload call p50 / p95 | total request to GPU-ready p50 / p95 / max (ms) |', '|' + '---|' * 10]
    def pp(x, n=3): return f"{f(x['P50'], n)} / {f(x['P95'], n)}"
    for grp, label, d in load_viewer():
        if not label.startswith(('fling-text2000', 'fling-book', 'fling-vec60', 'fling-tr60', 'x-nosandbox')) or 'pipeline' not in d: continue
        if not label.endswith(('-t512-r1', 'win2d')) and 'x-nosandbox' not in label: continue
        p = d['pipeline']
        rows.append(f"| {grp} | {label} | {pp(p['requestTransitMs'])} | {pp(p['engineQueueMs'])} | {pp(p['engineLoadPageMs'])} | {pp(p['engineRenderMs'])} | {pp(p['responseTransitMs'])} | {pp(p['slotCopyMs'])} | {pp(p['uploadTileMs'])} | {f(p['tileRequestToReadyMs']['P50'])} / {f(p['tileRequestToReadyMs']['P95'])} / {f(p['tileRequestToReadyMs']['Max'])} |")
    return chr(10).join(rows)

def memory_table():
    rows = ['| group | run | adapter | GPU local peak MB | GPU shared peak MB | process private peak MB | process working set peak MB | cached entries at end | nominal cache MB | evicted |', '|' + '---|' * 10]
    for grp, label, d in load_viewer():
        if not label.startswith(('fling-text2000-', 'fling-vec60', 'fling-book', 'x-idle')): continue
        ad = adapter_used(d); g = d['mem']['gpuPeak'].get(ad, {}); pl = d.get('pipeline')
        rows.append(f"| {grp} | {label} | {short_adapter(ad)} | {f(g.get('localMB',0),0)} | {f(g.get('nonLocalMB',0),0)} | {f(d['mem']['peakPrivMB'],0)} | {f(d['mem']['peakWsMB'],0)} | {pl['cachedEntries'] if pl else '-'} | {f(pl['cacheNominalMB'],0) if pl else '-'} | {pl['evicted'] if pl else '-'} |")
    return chr(10).join(rows)

def tier_table():
    out = ['| tile content (512x512 BGRA, 1 MiB) | codec | size KB | ratio | compress p50 / p95 ms | decompress p50 / p95 ms |', '|---|---|---|---|---|---|']
    cur = None
    for line in open(os.path.join(ROOT, 'tierbench.txt'), encoding='utf-8', errors='ignore'):
        line = line.rstrip()
        if line.startswith('== '): cur = line[3:].split()[0].replace('.bgra', ''); continue
        if not line.strip() or cur is None: continue
        import re
        m = re.match(r'\s+(\S+)\s+(\d+) KB\s+ratio\s+([\d.]+)x\s+compress p50\s+([\d.]+) p95\s+([\d.]+) ms\s+decompress p50\s+([\d.]+) p95\s+([\d.]+) ms', line)
        if m: out.append(f"| {cur} | {m.group(1)} | {m.group(2)} | {m.group(3)}x | {m.group(4)} / {m.group(5)} | {m.group(6)} / {m.group(7)} |")
    return chr(10).join(out)

def ecore_table():
    base = {}
    for sub in ('engine-default', 'engine-ecore'):
        base[sub] = {(r['fixture'], r['tile'], r['zoom'], r['metric']): r for r in csv.DictReader(open(os.path.join(ROOT, sub, 'summary.csv')))}
    out = ['| fixture | tile@zoom | default scheduling p50 / p95 ms | pinned to one E-core p50 / p95 ms |', '|---|---|---|---|']
    for fx in ('real-gpo911-book', 'text-book', 'vector-map', 'real-usgs-topo', 'transparency'):
        for t, z in (('512', '100'), ('512', '400')):
            k = (fx, t, z, 'render_loaded_page_nonblank_ms')
            a = base['engine-default'].get(k); b = base['engine-ecore'].get(k)
            if a and b and int(a['n']) > 0: out.append(f"| {fx} | {t}@{z}% | {float(a['p50']):.2f} / {float(a['p95']):.2f} | {float(b['p50']):.2f} / {float(b['p95']):.2f} |")
    return chr(10).join(out)

def progressive_compact():
    rows = list(csv.DictReader(open(os.path.join(ROOT, 'engine-default', 'progressive.csv'))))
    out = ['| fixture | zoom | non-progressive p50 / p95 / max (ms) | progressive, 2 ms slices p50 / p95 / max | largest pause overshoot over all slice sizes (ms) | cancel latency, flag to engine free: n | p50 / p95 / max (ms) | renders that finished before the cancel (excluded) |', '|---|---|---|---|---|---|---|---|']
    keys = []
    for r in rows:
        k = (r['fixture'], r['zoom'])
        if k not in keys: keys.append(k)
    for fx, z in keys:
        g = {r['mode']: r for r in rows if (r['fixture'], r['zoom']) == (fx, z)}
        over = max(float(r['extra1']) for m, r in g.items() if m.startswith('slice'))
        pl = g['plain']; sl = g['slice2.0']; c = g['cancel']
        out.append(f"| {fx} | {z}% | {float(pl['p50']):.2f} / {float(pl['p95']):.2f} / {float(pl['max']):.2f} | {float(sl['p50']):.2f} / {float(sl['p95']):.2f} / {float(sl['max']):.2f} | {over:.2f} | {c['n']} | {float(c['p50']):.2f} / {float(c['p95']):.2f} / {float(c['max']):.2f} | {c['extra1']} of 60 |")
    return chr(10).join(out)

def ttft_engine_cold_compact():
    return cold_table()

if __name__ == '__main__':
    what = sys.argv[1] if len(sys.argv) > 1 else 'fling'
    print({'fling': fling_table, 'upload': upload_table, 'ttft': ttft_table, 'zoom': zoom_table, 'engine': engine_tables, 'cold': cold_table, 'core': fling_core, 'transport': transport_table, 'memory': memory_table, 'tier': tier_table, 'ecore': ecore_table, 'prog': progressive_compact}[what]())
