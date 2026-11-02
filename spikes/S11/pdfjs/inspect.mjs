// pdf.js (Firefox's PDF engine) structural read of PDFs: pages, outline with resolved targets, named destinations, link targets, page labels.
// Usage: node inspect.mjs file.pdf [more.pdf ...]   Prints one JSON object per line. "key" = the PAGE-<doc>-<nn> marker found in the page text.
import fs from "node:fs";
import * as pdfjs from "pdfjs-dist/legacy/build/pdf.mjs";

const fontDir = new URL("./node_modules/pdfjs-dist/standard_fonts/", import.meta.url).href;

async function keyOf(page) {
  const tc = await page.getTextContent();
  const m = /PAGE-[A-Z0-9]+-\d+/.exec(tc.items.map(i => i.str).join(" "));
  return m ? m[0] : null;
}

async function targetKey(doc, keys, dest) {
  try {
    let d = dest;
    if (typeof d === "string") d = await doc.getDestination(d);
    if (!Array.isArray(d)) return "unresolved";
    const ref = d[0];
    if (ref && typeof ref === "object") {
      const idx = await doc.getPageIndex(ref);
      return keys[idx] ?? `page#${idx}`;
    }
    if (Number.isInteger(ref)) return keys[ref] ?? `page#${ref}`;
    return "unresolved";
  } catch (e) {
    return "unresolved:" + (e.message ?? e).toString().slice(0, 40);
  }
}

async function walk(doc, keys, items, depth, out) {
  for (const it of items ?? []) {
    let target;
    if (it.url) target = "uri";
    else if (it.dest) target = await targetKey(doc, keys, it.dest);
    else target = "none";
    out.push({ depth, title: it.title, target });
    await walk(doc, keys, it.items, depth + 1, out);
  }
}

for (const file of process.argv.slice(2)) {
  const res = { file, ok: false };
  try {
    const data = new Uint8Array(fs.readFileSync(file));
    const task = pdfjs.getDocument({ data, standardFontDataUrl: fontDir, useSystemFonts: false, verbosity: 0 });
    const doc = await task.promise;
    const keys = [];
    const pages = [];
    for (let i = 1; i <= doc.numPages; i++) {
      const page = await doc.getPage(i);
      keys.push(await keyOf(page));
    }
    for (let i = 1; i <= doc.numPages; i++) {
      const page = await doc.getPage(i);
      const links = [];
      for (const a of await page.getAnnotations()) {
        if (a.subtype !== "Link") continue;
        if (a.url) links.push("uri");
        else if (a.dest) links.push(await targetKey(doc, keys, a.dest));
        else if (a.action || a.unsafeUrl) links.push("action:" + (a.action ?? "url"));
        else links.push("none");
      }
      pages.push({ key: keys[i - 1], rotate: page.rotate, links });
    }
    const outline = [];
    await walk(doc, keys, await doc.getOutline(), 0, outline);
    const named = {};
    const dests = (await doc.getDestinations()) ?? {};
    for (const name of Object.keys(dests)) named[name] = await targetKey(doc, keys, dests[name]);
    for (const name of [...Array(12).keys()].map(i => `dest-p${i + 1}`).concat(["legacy-p2", "legacy-p9"])) {
      if (named[name]) continue;
      const d = await doc.getDestination(name);
      if (d) named[name] = await targetKey(doc, keys, d);
    }
    res.ok = true;
    res.numPages = doc.numPages;
    res.pages = pages;
    res.outline = outline;
    res.namedDests = named;
    res.labels = (await doc.getPageLabels()) ?? null;
    await task.destroy();
  } catch (e) {
    res.error = (e.message ?? String(e)).slice(0, 200);
  }
  console.log(JSON.stringify(res));
}
