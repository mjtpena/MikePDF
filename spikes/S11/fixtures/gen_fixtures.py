#!/usr/bin/env python3
"""S11 fixture generator. Pure standard library; writes classic-xref PDFs by hand so every object number is known.

Fixtures (all pages carry unique, extractable text "PAGE-<doc>-<nn>" and a page-specific coloured rectangle):
  A.pdf        10 pages. Outline (3 levels, one entry with a named-destination action, one with a URI), links of every kind
               (explicit /Dest, GoTo action, named /Dest string, link to the last page, URI), named destinations (name tree
               and legacy /Dests), page labels (roman then "A-" prefixed), AcroForm (text, text, checkbox, a parent/kid pair),
               tagged structure, optional content (one ON and one OFF layer), annotations (note + popup + reply, highlight, free text).
  B.pdf        5 pages. Its own outline, links, named destinations and form fields whose names CLASH with A's.
  C.pdf        6 pages with /Rotate 0/90/180/270 and MediaBox/CropBox offsets.
  big-<n>.pdf  100 pages each (n = 1..5): text, an image every 5 pages, an outline entry every 10 pages, a link per page.
  big-600.pdf  600 pages with 60 top-level bookmarks (every 10 pages), two children each.
Usage: gen_fixtures.py <outdir>
"""
import os
import random
import struct
import sys
import zlib


class Pdf:
    def __init__(self):
        self.objs = {}          # number -> bytes (full object body, without "n 0 obj"/"endobj")
        self.next = 1

    def reserve(self, n=1):
        first = self.next
        self.next += n
        return first if n == 1 else list(range(first, first + n))

    def set(self, num, body):
        if isinstance(body, str):
            body = body.encode('latin-1')
        self.objs[num] = body

    def add(self, body):
        n = self.reserve()
        self.set(n, body)
        return n

    def stream(self, dict_extra, data, compress=False):
        if isinstance(data, str):
            data = data.encode('latin-1')
        if compress:
            data = zlib.compress(data)
            dict_extra += ' /Filter /FlateDecode'
        return ('<< %s /Length %d >>\nstream\n' % (dict_extra, len(data))).encode('latin-1') + data + b'\nendstream'

    def write(self, path, root, info=None):
        out = bytearray(b'%PDF-1.7\n%\xe2\xe3\xcf\xd3\n')
        offsets = {}
        for n in sorted(self.objs):
            offsets[n] = len(out)
            out += ('%d 0 obj\n' % n).encode() + self.objs[n] + b'\nendobj\n'
        xref = len(out)
        size = max(self.objs) + 1
        out += ('xref\n0 %d\n' % size).encode()
        out += b'0000000000 65535 f \n'
        for n in range(1, size):
            if n in offsets:
                out += ('%010d 00000 n \n' % offsets[n]).encode()
            else:
                out += b'0000000000 65535 f \n'
        trailer = '<< /Size %d /Root %d 0 R' % (size, root)
        if info:
            trailer += ' /Info %d 0 R' % info
        trailer += ' /ID [<%s> <%s>] >>' % (os.urandom(16).hex(), os.urandom(16).hex())
        out += ('trailer\n%s\nstartxref\n%d\n%%%%EOF\n' % (trailer, xref)).encode()
        with open(path, 'wb') as f:
            f.write(out)


def esc(s):
    return s.replace('\\', '\\\\').replace('(', '\\(').replace(')', '\\)')


def pdfstr(s):
    # UTF-16BE with BOM for non-ASCII, literal otherwise
    try:
        s.encode('ascii')
        return '(%s)' % esc(s)
    except UnicodeEncodeError:
        return '<FEFF%s>' % s.encode('utf-16-be').hex().upper()


COLORS = [(0.9, 0.2, 0.2), (0.2, 0.7, 0.3), (0.2, 0.3, 0.9), (0.9, 0.7, 0.1), (0.6, 0.2, 0.7),
          (0.1, 0.7, 0.7), (0.9, 0.5, 0.1), (0.4, 0.4, 0.4), (0.7, 0.9, 0.2), (0.9, 0.3, 0.6)]


def page_content(doc, i, extra='', mcid=None):
    r, g, b = COLORS[(i - 1) % len(COLORS)]
    text = 'PAGE-%s-%02d' % (doc, i)
    body = ''
    if mcid is not None:
        body += '/P <</MCID %d>> BDC\n' % mcid
    body += 'BT /F1 28 Tf 72 700 Td (%s) Tj ET\n' % text
    if mcid is not None:
        body += 'EMC\n'
    body += '%.2f %.2f %.2f rg 72 560 200 80 re f\n' % (r, g, b)
    body += 'BT /F1 12 Tf 72 540 Td (Body text line for page %d of document %s.) Tj ET\n' % (i, doc)
    return body + extra


def build_A(path):
    p = Pdf()
    N = 10
    cat, pages, font, info = p.reserve(), p.reserve(), p.reserve(), p.reserve()
    page_ids = p.reserve(N)
    cont_ids = p.reserve(N)
    p.set(font, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>')
    p.set(info, '<< /Title (Fixture A) /Producer (S11 generator) >>')

    # optional content groups
    oc_on, oc_off = p.reserve(), p.reserve()
    p.set(oc_on, '<< /Type /OCG /Name (Layer ON) >>')
    p.set(oc_off, '<< /Type /OCG /Name (Layer OFF) >>')

    # structure tree: Document > P (one per page)
    st_root, st_doc, st_parent_tree = p.reserve(), p.reserve(), p.reserve()
    st_elems = p.reserve(N)

    # named destinations
    names = {}      # name -> page index (1-based)
    for i in range(1, N + 1):
        names['dest-p%d' % i] = i

    # form fields: name (page 2), email (page 5), agree (page 8), address -> street, city (page 8)
    f_name, f_email, f_agree, f_addr, f_street, f_city = p.reserve(6)
    form_pages = {2: [f_name], 5: [f_email], 8: [f_agree, f_street, f_city]}

    # annotations per page
    annots = {i: [] for i in range(1, N + 1)}

    def link(i, rect, dest=None, action=None, uri=None):
        b = '<< /Type /Annot /Subtype /Link /Rect [%s] /Border [0 0 0] /C [0 0 1]' % rect
        if dest:
            b += ' /Dest %s' % dest
        if action:
            b += ' /A %s' % action
        if uri:
            b += ' /A << /S /URI /URI (%s) >>' % uri
        b += ' /F 4 >>'
        return p.add(b)

    for i in range(1, N + 1):
        nxt = i % N + 1
        annots[i].append(link(i, '72 460 250 480', dest='[%d 0 R /XYZ 72 700 null]' % page_ids[nxt - 1]))
        annots[i].append(link(i, '72 430 250 450', action='<< /S /GoTo /D [%d 0 R /Fit] >>' % page_ids[(i + 3) % N]))
        annots[i].append(link(i, '72 400 250 420', dest='(dest-p%d)' % ((i + 1) % N + 1)))
        if i == 1:
            annots[i].append(link(i, '72 370 250 390', dest='[%d 0 R /Fit]' % page_ids[N - 1]))   # to the last page
            annots[i].append(link(i, '72 340 250 360', uri='https://example.test/'))
            annots[i].append(link(i, '72 310 250 330', action='<< /S /GoTo /D (dest-p10) >>'))

    # annotations: note + popup + reply on page 1; highlight on page 3; free text on page 4
    note, popup, reply = p.reserve(3)
    p.set(note, '<< /Type /Annot /Subtype /Text /Rect [400 700 420 720] /Contents (A note) /T (Alice) /Name /Comment /Popup %d 0 R /F 28 /P %d 0 R /NM (note-1) >>' % (popup, page_ids[0]))
    p.set(popup, '<< /Type /Annot /Subtype /Popup /Rect [430 700 560 760] /Parent %d 0 R /Open false /F 28 >>' % note)
    p.set(reply, '<< /Type /Annot /Subtype /Text /Rect [400 680 420 700] /Contents (A reply) /T (Bob) /IRT %d 0 R /RT /R /F 28 /P %d 0 R /NM (reply-1) >>' % (note, page_ids[0]))
    annots[1] += [note, popup, reply]
    hl = p.add('<< /Type /Annot /Subtype /Highlight /Rect [72 695 200 725] /QuadPoints [72 725 200 725 72 695 200 695] /C [1 1 0] /Contents (hl) /F 4 /NM (hl-1) >>')
    annots[3].append(hl)
    ft = p.add('<< /Type /Annot /Subtype /FreeText /Rect [300 400 500 460] /Contents (Free text) /DA (/Helv 12 Tf 0 g) /F 4 /NM (ft-1) >>')
    annots[4].append(ft)

    # widgets
    def widget_body(num, name, ftype, rect, page, parent=None, extra=''):
        b = '<< /Type /Annot /Subtype /Widget /Rect [%s] /F 4 /P %d 0 R' % (rect, page_ids[page - 1])
        if parent:
            b += ' /Parent %d 0 R' % parent
        if name:
            b += ' /T (%s)' % name
        if ftype:
            b += ' /FT /%s' % ftype
        return b + ' ' + extra + ' >>'

    ap_empty = p.add(p.stream('/Type /XObject /Subtype /Form /BBox [0 0 150 20]', '0 g'))
    p.set(f_name, widget_body(f_name, 'name', 'Tx', '72 250 222 270', 2, extra='/V (Alice) /DA (/Helv 12 Tf 0 g) /AP << /N %d 0 R >>' % ap_empty))
    p.set(f_email, widget_body(f_email, 'email', 'Tx', '72 250 222 270', 5, extra='/V (a@example.test) /DA (/Helv 12 Tf 0 g) /AP << /N %d 0 R >>' % ap_empty))
    ap_on = p.add(p.stream('/Type /XObject /Subtype /Form /BBox [0 0 14 14]', '0 g 2 2 10 10 re f'))
    ap_off = p.add(p.stream('/Type /XObject /Subtype /Form /BBox [0 0 14 14]', '0 g 2 2 10 10 re S'))
    p.set(f_agree, widget_body(f_agree, 'agree', 'Btn', '72 250 86 264', 8, extra='/V /Yes /AS /Yes /AP << /N << /Yes %d 0 R /Off %d 0 R >> >>' % (ap_on, ap_off)))
    p.set(f_addr, '<< /T (address) /FT /Tx /Kids [%d 0 R %d 0 R] >>' % (f_street, f_city))
    p.set(f_street, widget_body(f_street, 'street', None, '72 200 222 220', 8, parent=f_addr, extra='/V (1 Main St) /DA (/Helv 12 Tf 0 g) /AP << /N %d 0 R >>' % ap_empty))
    p.set(f_city, widget_body(f_city, 'city', None, '72 170 222 190', 8, parent=f_addr, extra='/V (Springfield) /DA (/Helv 12 Tf 0 g) /AP << /N %d 0 R >>' % ap_empty))
    for pg, ws in form_pages.items():
        annots[pg] += ws

    # pages
    for i in range(1, N + 1):
        extra = ''
        res = '/Font << /F1 %d 0 R >>' % font
        if i == 3:
            res += ' /Properties << /MC0 %d 0 R /MC1 %d 0 R >>' % (oc_on, oc_off)
            extra = ('/OC /MC0 BDC 0 0.6 0 rg 300 560 100 80 re f EMC\n'
                     '/OC /MC1 BDC 1 0 0 rg 420 560 100 80 re f EMC\n')
        p.set(cont_ids[i - 1], p.stream('', page_content('A', i, extra, mcid=0)))
        rot = ''
        if i == 6:
            rot = ' /Rotate 90'
        crop = ''
        if i == 7:
            crop = ' /CropBox [50 30 560 760]'
        p.set(page_ids[i - 1], '<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792]%s%s /Resources << %s >> /Contents %d 0 R /Annots [%s] /StructParents %d >>'
              % (pages, rot, crop, res, cont_ids[i - 1], ' '.join('%d 0 R' % a for a in annots[i]), i - 1))
        p.set(st_elems[i - 1], '<< /Type /StructElem /S /P /P %d 0 R /Pg %d 0 R /K 0 >>' % (st_doc, page_ids[i - 1]))
    p.set(pages, '<< /Type /Pages /Kids [%s] /Count %d >>' % (' '.join('%d 0 R' % x for x in page_ids), N))
    p.set(st_doc, '<< /Type /StructElem /S /Document /P %d 0 R /K [%s] >>' % (st_root, ' '.join('%d 0 R' % x for x in st_elems)))
    p.set(st_parent_tree, '<< /Nums [%s] >>' % ' '.join('%d [%d 0 R]' % (i, st_elems[i]) for i in range(N)))
    p.set(st_root, '<< /Type /StructTreeRoot /K %d 0 R /ParentTree %d 0 R /ParentTreeNextKey %d >>' % (st_doc, st_parent_tree, N))

    # outline: 3 chapters x 2-3 sections
    o_root = p.reserve()
    chapters = [('Chapter 1', 1, [('Section 1.1', 2), ('Section 1.2', 3)]),
                ('Chapter 2', 4, [('Section 2.1', 5), ('Section 2.2 (named destination)', 'dest-p6')]),
                ('Chapter 3', 7, [('Section 3.1', 8), ('Section 3.2 (URI)', 'uri'), ('Section 3.3', 10)])]
    ch_ids = [p.reserve() for _ in chapters]
    sec_ids = [[p.reserve() for _ in ch[2]] for ch in chapters]

    def dest_for(t):
        if t == 'uri':
            return '/A << /S /URI /URI (https://example.test/) >>'
        if isinstance(t, str):
            return '/A << /S /GoTo /D (%s) >>' % t
        return '/Dest [%d 0 R /XYZ 0 792 null]' % page_ids[t - 1]
    for ci, (title, pg, secs) in enumerate(chapters):
        cid = ch_ids[ci]
        b = '<< /Title (%s) /Parent %d 0 R %s' % (title, o_root, dest_for(pg))
        if ci > 0:
            b += ' /Prev %d 0 R' % ch_ids[ci - 1]
        if ci < len(chapters) - 1:
            b += ' /Next %d 0 R' % ch_ids[ci + 1]
        b += ' /First %d 0 R /Last %d 0 R /Count %d >>' % (sec_ids[ci][0], sec_ids[ci][-1], len(secs) if ci != 1 else -len(secs))
        p.set(cid, b)
        for si, (st, spg) in enumerate(secs):
            sid = sec_ids[ci][si]
            b = '<< /Title (%s) /Parent %d 0 R %s' % (st, cid, dest_for(spg))
            if si > 0:
                b += ' /Prev %d 0 R' % sec_ids[ci][si - 1]
            if si < len(secs) - 1:
                b += ' /Next %d 0 R' % sec_ids[ci][si + 1]
            p.set(sid, b + ' >>')
    p.set(o_root, '<< /Type /Outlines /First %d 0 R /Last %d 0 R /Count %d >>' % (ch_ids[0], ch_ids[-1], 3 + 2 + 3))

    # name tree and legacy dests
    nm = sorted(names)
    names_obj = p.add('<< /Names [%s] /Limits [(%s) (%s)] >>' % (' '.join('(%s) [%d 0 R /Fit]' % (n, page_ids[names[n] - 1]) for n in nm), nm[0], nm[-1]))
    legacy = '<< /legacy-p2 [%d 0 R /Fit] /legacy-p9 [%d 0 R /XYZ 0 700 null] >>' % (page_ids[1], page_ids[8])
    legacy_obj = p.add(legacy)

    acro = p.add('<< /Fields [%d 0 R %d 0 R %d 0 R %d 0 R] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv %d 0 R >> >> >>' % (f_name, f_email, f_agree, f_addr, font))
    p.set(cat, ('<< /Type /Catalog /Pages %d 0 R /Outlines %d 0 R /Names << /Dests %d 0 R >> /Dests %d 0 R /AcroForm %d 0 R '
                '/PageLabels << /Nums [0 << /S /r >> 2 << /S /D /P (A-) >>] >> /StructTreeRoot %d 0 R /MarkInfo << /Marked true >> '
                '/OCProperties << /OCGs [%d 0 R %d 0 R] /D << /ON [%d 0 R] /OFF [%d 0 R] /Order [%d 0 R %d 0 R] >> >> /PageMode /UseOutlines /Lang (en-US) >>')
          % (pages, o_root, names_obj, legacy_obj, acro, st_root, oc_on, oc_off, oc_on, oc_off, oc_on, oc_off))
    p.write(path, cat, info)


def build_B(path):
    p = Pdf()
    N = 5
    cat, pages, font, info = p.reserve(), p.reserve(), p.reserve(), p.reserve()
    page_ids = p.reserve(N)
    cont_ids = p.reserve(N)
    p.set(font, '<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>')
    p.set(info, '<< /Title (Fixture B) >>')
    f_name, f_agree, f_addr, f_street = p.reserve(4)
    ap_empty = p.add(p.stream('/Type /XObject /Subtype /Form /BBox [0 0 150 20]', '0 g'))
    ap_on = p.add(p.stream('/Type /XObject /Subtype /Form /BBox [0 0 14 14]', '0 g 2 2 10 10 re f'))
    ap_off = p.add(p.stream('/Type /XObject /Subtype /Form /BBox [0 0 14 14]', '0 g 2 2 10 10 re S'))
    pg = lambda i: page_ids[i - 1]
    p.set(f_name, '<< /Type /Annot /Subtype /Widget /Rect [72 250 222 270] /F 4 /P %d 0 R /T (name) /FT /Tx /V (Bruno) /DA (/Helv 12 Tf 0 g) /AP << /N %d 0 R >> >>' % (pg(2), ap_empty))
    p.set(f_agree, '<< /Type /Annot /Subtype /Widget /Rect [72 200 86 214] /F 4 /P %d 0 R /T (agree) /FT /Btn /V /Off /AS /Off /AP << /N << /Yes %d 0 R /Off %d 0 R >> >> >>' % (pg(4), ap_on, ap_off))
    p.set(f_addr, '<< /T (address) /FT /Tx /Kids [%d 0 R] >>' % f_street)
    p.set(f_street, '<< /Type /Annot /Subtype /Widget /Rect [72 150 222 170] /F 4 /P %d 0 R /Parent %d 0 R /T (street) /V (9 Other Rd) /DA (/Helv 12 Tf 0 g) /AP << /N %d 0 R >> >>' % (pg(4), f_addr, ap_empty))
    annots = {i: [] for i in range(1, N + 1)}
    annots[2].append(f_name)
    annots[4] += [f_agree, f_street]
    for i in range(1, N + 1):
        nxt = i % N + 1
        annots[i].append(p.add('<< /Type /Annot /Subtype /Link /Rect [72 460 250 480] /Border [0 0 0] /Dest [%d 0 R /Fit] /F 4 >>' % pg(nxt)))
        annots[i].append(p.add('<< /Type /Annot /Subtype /Link /Rect [72 430 250 450] /Border [0 0 0] /Dest (dest-p%d) /F 4 >>' % nxt))
    for i in range(1, N + 1):
        p.set(cont_ids[i - 1], p.stream('', page_content('B', i)))
        p.set(pg(i), '<< /Type /Page /Parent %d 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R /Annots [%s] >>'
              % (pages, font, cont_ids[i - 1], ' '.join('%d 0 R' % a for a in annots[i])))
    p.set(pages, '<< /Type /Pages /Kids [%s] /Count %d >>' % (' '.join('%d 0 R' % x for x in page_ids), N))
    o_root = p.reserve()
    items = [p.reserve() for _ in range(3)]
    spec = [('Part I', 1), ('Part II', 3), ('Part III', 5)]
    for k, (t, pgn) in enumerate(spec):
        b = '<< /Title (%s) /Parent %d 0 R /Dest [%d 0 R /Fit]' % (t, o_root, pg(pgn))
        if k > 0:
            b += ' /Prev %d 0 R' % items[k - 1]
        if k < 2:
            b += ' /Next %d 0 R' % items[k + 1]
        p.set(items[k], b + ' >>')
    p.set(o_root, '<< /Type /Outlines /First %d 0 R /Last %d 0 R /Count 3 >>' % (items[0], items[-1]))
    names = sorted('dest-p%d' % i for i in range(1, N + 1))
    names_obj = p.add('<< /Names [%s] /Limits [(%s) (%s)] >>' % (' '.join('(%s) [%d 0 R /Fit]' % (n, pg(int(n[-1]))) for n in names), names[0], names[-1]))
    acro = p.add('<< /Fields [%d 0 R %d 0 R %d 0 R] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv %d 0 R >> >> >>' % (f_name, f_agree, f_addr, font))
    p.set(cat, '<< /Type /Catalog /Pages %d 0 R /Outlines %d 0 R /Names << /Dests %d 0 R >> /AcroForm %d 0 R /PageLabels << /Nums [0 << /S /D /P (B-) >>] >> >>' % (pages, o_root, names_obj, acro))
    p.write(path, cat, info)


def build_C(path):
    p = Pdf()
    cat, pages, font = p.reserve(), p.reserve(), p.reserve()
    specs = [(0, '[0 0 612 792]', None), (90, '[0 0 612 792]', None), (180, '[0 0 612 792]', '[50 30 560 740]'),
             (270, '[0 0 612 792]', '[50 30 560 740]'), (0, '[-100 -50 512 742]', '[-50 -20 450 700]'), (90, '[100 100 712 892]', '[150 130 650 800]')]
    page_ids = p.reserve(len(specs))
    cont = p.reserve(len(specs))
    p.set(font, '<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>')
    for i, (rot, mb, cb) in enumerate(specs, 1):
        # content is drawn relative to the MediaBox origin so the page content is visible
        ox, oy = [float(x) for x in mb.strip('[]').split()[:2]]
        body = 'BT /F1 28 Tf %f %f Td (PAGE-C-%02d) Tj ET\n' % (ox + 72, oy + 690, i)
        body += '%.2f %.2f %.2f rg %f %f 200 80 re f\n' % (*COLORS[(i - 1) % 10], ox + 72, oy + 560)
        body += '0 0 0 RG 4 w %f %f 100 100 re S\n' % (ox + 10, oy + 10)
        p.set(cont[i - 1], p.stream('', body))
        p.set(page_ids[i - 1], '<< /Type /Page /Parent %d 0 R /MediaBox %s%s%s /Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>'
              % (pages, mb, ' /Rotate %d' % rot if rot else '', ' /CropBox %s' % cb if cb else '', font, cont[i - 1]))
    p.set(pages, '<< /Type /Pages /Kids [%s] /Count %d >>' % (' '.join('%d 0 R' % x for x in page_ids), len(specs)))
    p.set(cat, '<< /Type /Catalog /Pages %d 0 R >>' % pages)
    p.write(path, cat)


def build_big(path, doc, n, seed, outline_every=10, children=0):
    rnd = random.Random(seed)
    p = Pdf()
    cat, pages, font = p.reserve(), p.reserve(), p.reserve()
    page_ids = p.reserve(n)
    cont = p.reserve(n)
    p.set(font, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>')
    img_ids = {}
    for i in range(1, n + 1):
        if i % 5 == 1:
            # 96 KB of incompressible noise as a 128x256 RGB image: gives the file realistic bulk
            noise = bytes(rnd.getrandbits(8) for _ in range(128 * 256 * 3))
            img_ids[i] = p.add(p.stream('/Type /XObject /Subtype /Image /Width 128 /Height 256 /ColorSpace /DeviceRGB /BitsPerComponent 8', noise))
    annots = {}
    for i in range(1, n + 1):
        nxt = i % n + 1
        annots[i] = [p.add('<< /Type /Annot /Subtype /Link /Rect [72 460 250 480] /Border [0 0 0] /Dest [%d 0 R /Fit] /F 4 >>' % page_ids[nxt - 1])]
    for i in range(1, n + 1):
        body = page_content(doc, i)
        for k in range(30):
            body += 'BT /F1 10 Tf 72 %d Td (%s) Tj ET\n' % (500 - k * 12, 'Lorem ipsum dolor sit amet %d-%d' % (i, k))
        res = '/Font << /F1 %d 0 R >>' % font
        base_img = (i - 1) // 5 * 5 + 1
        if base_img in img_ids:
            res += ' /XObject << /Im0 %d 0 R >>' % img_ids[base_img]
            body += 'q 100 0 0 100 400 560 cm /Im0 Do Q\n'
        p.set(cont[i - 1], p.stream('', body, compress=True))
        p.set(page_ids[i - 1], '<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] /Resources << %s >> /Contents %d 0 R /Annots [%d 0 R] >>' % (pages, res, cont[i - 1], annots[i][0]))
    p.set(pages, '<< /Type /Pages /Kids [%s] /Count %d >>' % (' '.join('%d 0 R' % x for x in page_ids), n))
    # outline
    starts = list(range(1, n + 1, outline_every))
    o_root = p.reserve()
    top = p.reserve(len(starts))
    kids = {}
    for k, s in enumerate(starts):
        if children:
            kids[k] = p.reserve(children)
    for k, s in enumerate(starts):
        b = '<< /Title (%s chapter %d) /Parent %d 0 R /Dest [%d 0 R /Fit]' % (doc, k + 1, o_root, page_ids[s - 1])
        if k > 0:
            b += ' /Prev %d 0 R' % top[k - 1]
        if k < len(starts) - 1:
            b += ' /Next %d 0 R' % top[k + 1]
        if children:
            b += ' /First %d 0 R /Last %d 0 R /Count %d' % (kids[k][0], kids[k][-1], children)
        p.set(top[k], b + ' >>')
        for c in range(children):
            cid = kids[k][c]
            cb = '<< /Title (%s section %d.%d) /Parent %d 0 R /Dest [%d 0 R /Fit]' % (doc, k + 1, c + 1, top[k], page_ids[min(n, s + c + 1) - 1])
            if c > 0:
                cb += ' /Prev %d 0 R' % kids[k][c - 1]
            if c < children - 1:
                cb += ' /Next %d 0 R' % kids[k][c + 1]
            p.set(cid, cb + ' >>')
    p.set(o_root, '<< /Type /Outlines /First %d 0 R /Last %d 0 R /Count %d >>' % (top[0], top[-1], len(starts) * (1 + children)))
    p.set(cat, '<< /Type /Catalog /Pages %d 0 R /Outlines %d 0 R >>' % (pages, o_root))
    p.write(path, cat)


if __name__ == '__main__':
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    build_A(os.path.join(out, 'A.pdf'))
    build_B(os.path.join(out, 'B.pdf'))
    build_C(os.path.join(out, 'C.pdf'))
    for k in range(1, 6):
        build_big(os.path.join(out, 'big-%d.pdf' % k), 'D%d' % k, 100, 1000 + k)
    build_big(os.path.join(out, 'big-600.pdf'), 'L', 600, 7, outline_every=10, children=2)
    for f in sorted(os.listdir(out)):
        print(f, os.path.getsize(os.path.join(out, f)))
