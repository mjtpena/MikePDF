// Headless pdf.js (Firefox's PDF engine) render of every S4 output, plus a pixel-diff against the render of the
// unmodified input made with the same engine. Usage: node render.mjs <outDir>
import fs from "node:fs";
import path from "node:path";
import { createCanvas } from "@napi-rs/canvas";
import * as pdfjs from "pdfjs-dist/legacy/build/pdf.mjs";
import { PNG } from "pngjs";

const outDir = path.resolve(process.argv[2] ?? "../out");
const manifest = JSON.parse(fs.readFileSync(path.join(outDir, "manifest.json"), "utf8"));
const SCALE = 1.5;
const fontDir = path.resolve("node_modules/pdfjs-dist/standard_fonts").split(path.sep).join("/") + "/";
fs.mkdirSync(path.join(outDir, "render", "pdfjs"), { recursive: true });

async function render(file, mode) {
  const data = new Uint8Array(fs.readFileSync(file));
  const task = pdfjs.getDocument({ data, standardFontDataUrl: fontDir, useSystemFonts: false, verbosity: 0 }); const doc = await task.promise;
  const page = await doc.getPage(1);
  const vp = page.getViewport({ scale: SCALE });
  const canvas = createCanvas(Math.round(vp.width), Math.round(vp.height));
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = "#fff"; ctx.fillRect(0, 0, canvas.width, canvas.height);
  // ENABLE_STORAGE (3): draw annotations AND widgets onto the canvas using /AP (or regenerated text when NeedAppearances)
  await page.render({ canvasContext: ctx, viewport: vp, annotationMode: mode ?? pdfjs.AnnotationMode.ENABLE_STORAGE }).promise;
  const annots = await page.getAnnotations();
  const info = { numAnnots: annots.length, annots: annots.map(a => ({ subtype: a.subtype, fieldType: a.fieldType, fieldName: a.fieldName, fieldValue: a.fieldValue, hasAppearance: a.hasAppearance, checkBox: a.checkBox, radioButton: a.radioButton, buttonValue: a.buttonValue, exportValue: a.exportValue })) };
  await task.destroy();
  return { w: canvas.width, h: canvas.height, rgba: canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data, png: canvas.toBuffer("image/png"), info };
}

function diff(a, b, rect /* [l,b,r,t] in PDF pts or null */, pageH) {
  if (a.w !== b.w || a.h !== b.h) return { error: "size mismatch" };
  let total = 0, inRect = 0;
  let x0 = 1e9, y0 = 1e9, x1 = -1, y1 = -1;
  const pad = 6;
  let rx0 = -1, ry0 = -1, rx1 = -1, ry1 = -1;
  if (rect) {
    rx0 = (rect[0] - pad) * SCALE; rx1 = (rect[2] + pad) * SCALE;
    ry0 = (pageH - rect[3] - pad) * SCALE; ry1 = (pageH - rect[1] + pad) * SCALE;
  }
  for (let y = 0; y < a.h; y++) for (let x = 0; x < a.w; x++) {
    const i = (y * a.w + x) * 4;
    const d = Math.abs(a.rgba[i] - b.rgba[i]) + Math.abs(a.rgba[i + 1] - b.rgba[i + 1]) + Math.abs(a.rgba[i + 2] - b.rgba[i + 2]);
    if (d > 24) {
      total++;
      if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
      if (rect && x >= rx0 && x <= rx1 && y >= ry0 && y <= ry1) inRect++;
    }
  }
  return { changedPixels: total, changedInRect: rect ? inRect : null, bbox: total ? [x0, y0, x1, y1] : null };
}

const results = {};
const baseCache = {};
for (const c of manifest.cases) {
  const inputFile = path.join(outDir, manifest.inputs[c.input].file);
  if (!baseCache[c.input]) {
    try { baseCache[c.input] = await render(inputFile); } catch (e) { baseCache[c.input] = { error: String(e) }; }
  }
  const base = baseCache[c.input];
  for (const [mode, rel] of Object.entries(c.outputs ?? {})) {
    const key = `${c.id}.${mode}`;
    try {
      const r = await render(path.join(outDir, rel));
      fs.writeFileSync(path.join(outDir, "render", "pdfjs", key + ".png"), r.png);
      const rect = c.exp?.rect ?? null;
      const pageH = 792;
      results[key] = { ...diff(base, r, rect, pageH), annotsSeenByPdfjs: r.info.annots.filter(a => a.subtype !== "Widget" || true).length, annots: r.info.annots };
    } catch (e) {
      results[key] = { error: String(e).slice(0, 300) };
    }
    console.log(key, JSON.stringify({ ...results[key], annots: undefined }));
  }
}
// baseline renders for reference
for (const [k, v] of Object.entries(baseCache)) if (v.png) fs.writeFileSync(path.join(outDir, "render", "pdfjs", `input-${k}.png`), v.png);
fs.writeFileSync(path.join(outDir, "pdfjs-results.json"), JSON.stringify({ pdfjsVersion: pdfjs.version, scale: SCALE, results }, null, 1));
console.log("pdf.js", pdfjs.version, "done");
