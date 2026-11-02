"""Compact markdown tables from the S10 jsonl files (reads out/raw, prints to stdout).
Usage: summarize.py matrix|attack|meta|crash|stress <file.jsonl>"""
import json
import re
import sys


def load(p):
    try:
        return [json.loads(l) for l in open(p, encoding='utf-8')]
    except FileNotFoundError:
        return []


def code(steps):
    m = re.findall(r'(\d+) \(', steps)
    return m[-1] if m else ''


def matrix(path):
    rows = load(path)
    mechs = list(dict.fromkeys(r['mechanism'] for r in rows))
    holders = list(dict.fromkeys(r['holder'] for r in rows))
    print('| holder | ' + ' | '.join(mechs) + ' |')
    print('|' + '---|' * (len(mechs) + 1))
    for h in holders:
        cells = []
        for m in mechs:
            r = next(x for x in rows if x['holder'] == h and x['mechanism'] == m)
            cells.append('OK' if r['ok'] else 'fail ' + code(r['steps']))
        print('| ' + h + ' | ' + ' | '.join(cells) + ' |')
    bad = [r for r in rows if r['ok'] and ('CHANGED' in str(r['viewSees']) or 'EXC' in str(r['viewSees']))]
    print('\nholders that did not see the old content intact after a successful replace:', len(bad))


def attack(path):
    rows = load(path)
    ops = list(dict.fromkeys(r['op'] for r in rows))
    shares = list(dict.fromkeys(r['brokerShare'] for r in rows))
    print('| broker share | engine view | ' + ' | '.join(ops) + ' |')
    print('|' + '---|' * (len(ops) + 2))
    for s in shares:
        for v in (False, True):
            cells = []
            for o in ops:
                r = next((x for x in rows if x['brokerShare'] == s and x['engineView'] == v and x['op'] == o), None)
                if r is None:
                    cells.append('-')
                    continue
                res = r['result']
                cells.append('allowed' if res.startswith('SUCC') else 'denied' + (' (' + re.findall(r'(\d+) \(', res)[0] + ')' if re.findall(r'(\d+) \(', res) else ''))
            print('| ' + s + ' | ' + ('yes' if v else 'no') + ' | ' + ' | '.join(cells) + ' |')


def meta(path):
    rows = load(path)
    for variant in dict.fromkeys(r['variant'] for r in rows):
        print('\n**%s**\n' % variant)
        print('| mechanism | result | DACL | Zone.Identifier | other stream | attributes | creation time | file ID | links |')
        print('|---|---|---|---|---|---|---|---|---|')
        for r in rows:
            if r['variant'] != variant:
                continue
            if not r['ok']:
                print('| %s | FAILED: %s | | | | | | | |' % (r['mechanism'], r['error']))
                continue
            extra = ''
            if 'symlink' in r:
                extra = ' ; symlink: %s; real file: %s' % (r['symlink'], r['realFile'])
            if 'hardlinkName' in r:
                extra = ' ; other hard link: %s' % r['hardlinkName']
            print('| %s | ok | %s | %s | %s | %s | %s | %s | %s%s |' % (r['mechanism'], r['dacl'], r['zone'], r['customStream'], r['attrs'], r['creation'], r['fileId'], r['links'], extra))


def crash(path):
    rows = load(path)
    print('| design | mechanism | killed at | on disk after the kill | recovery | verdict |')
    print('|---|---|---|---|---|---|')
    for r in rows:
        print('| %s | %s | %s | %s | %s | %s |' % (r['design'], r['mechanism'], r['killStep'], r['afterCrash'], r['recovery'], r['verdict']))


def stress(path):
    rows = load(path)
    print('| mechanism | kills | target = old | target = new | target MISSING | target torn |')
    print('|---|---|---|---|---|---|')
    for r in rows:
        print('| %s | %d | %d | %d | %d | %d |' % (r['mechanism'], r['rounds'], r['targetA'], r['targetB'], r['targetMissing'], r['targetTorn']))


if __name__ == '__main__':
    {'matrix': matrix, 'attack': attack, 'meta': meta, 'crash': crash, 'stress': stress}[sys.argv[1]](sys.argv[2])
