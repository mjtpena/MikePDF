// Contact sheets: for each case, crops of the annotation/widget region from the PDFium render and the pdf.js render
// (incremental | full). Usage: node montage.mjs <outDir>
import fs from "node:fs";
import path from "node:path";
import { createCanvas, loadImage } from "@napi-rs/canvas";

const outDir = path.resolve(process.argv[2] ?? "../out");
const manifest = JSON.parse(fs.readFileSync(path.join(outDir, "manifest.json"), "utf8"));
const S = 1.5;
const formRect = {
  txt_single: [150, 700, 400, 722], txt_multi: [150, 620, 400, 684], txt_comb: [150, 580, 400, 602], chk: [150, 540, 170, 560],
  radioGroup: [150, 500, 220, 520], combo_ro: [150, 460, 400, 482], combo_edit: [150, 420, 400, 442], list_multi: [150, 320, 400, 400], push: [150, 270, 250, 298], "Text Box": [100, 300, 200, 330], Combo_Editable: [100, 400, 200, 430], Checkbox: [135, 250, 155, 270]
};
fs.mkdirSync(path.join(outDir, "sheets"), { recursive: true });

const CELL_W = 400, CELL_H = 190, LABEL_W = 150, HEAD = 22;
const colsDefault = [["pdfium", "inc"], ["pdfium", "full"], ["pdfjs", "inc"], ["pdfjs", "full"]];
const colsR = [["pdfium", "inc"], ["pdfjs", "inc"], ["pdfjs", "incR"], ["pdfjs", "fullR"]];
const groups = {
  annots: [c => c.group === "annot" || c.group === "annot-quire", colsDefault],
  annotsR: [c => c.group === "annot" && c.outputs && Object.keys(c.outputs).length > 0, colsR],
  forms: [c => c.group === "form", colsDefault],
  sig: [c => c.group === "sig", colsDefault]
};

for (const [gname, [pred, cols]] of Object.entries(groups)) {
  const cases = manifest.cases.filter(pred).filter(c => c.outputs);
  const perSheet = 6;
  for (let s = 0; s * perSheet < cases.length; s++) {
    const chunk = cases.slice(s * perSheet, (s + 1) * perSheet);
    const canvas = createCanvas(LABEL_W + cols.length * CELL_W, HEAD + chunk.length * CELL_H);
    const ctx = canvas.getContext("2d");
    ctx.fillStyle = "#ddd"; ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = "#000"; ctx.font = "13px sans-serif";
    cols.forEach(([eng, mode], i) => ctx.fillText(`${eng} ${mode}`, LABEL_W + i * CELL_W + 6, 15));
    for (let r = 0; r < chunk.length; r++) {
      const c = chunk[r];
      const y0 = HEAD + r * CELL_H;
      ctx.fillStyle = "#000"; ctx.font = "12px sans-serif";
      ctx.fillText(c.id, 6, y0 + 16);
      const words = c.title.match(/.{1,22}(\s|$)/g) ?? [c.title];
      words.slice(0, 7).forEach((w, i) => ctx.fillText(w.trim(), 6, y0 + 34 + i * 14));
      let rect = c.exp?.rect ?? (c.exp?.fieldName ? formRect[c.exp.fieldName] : null);
      if (!rect) rect = [60, 60, 560, 740];
      const PAGE_H = c.input === "corpus-forms" ? 600 : 792;
      const m = 14;
      const cx0 = Math.max(0, (rect[0] - m)) * S, cx1 = Math.min(c.input === "corpus-forms" ? 300 : 612, rect[2] + m) * S;
      const cy0 = Math.max(0, PAGE_H - rect[3] - m) * S, cy1 = Math.min(PAGE_H, PAGE_H - rect[1] + m) * S;
      for (let i = 0; i < cols.length; i++) {
        const [eng, mode] = cols[i];
        const f = path.join(outDir, "render", eng === "pdfium" ? "pdfium" : "pdfjs", `${c.id}.${mode}.png`);
        const x = LABEL_W + i * CELL_W;
        ctx.fillStyle = "#fff"; ctx.fillRect(x + 2, y0 + 2, CELL_W - 4, CELL_H - 4);
        if (!fs.existsSync(f)) { ctx.fillStyle = "#900"; ctx.fillText("(no render)", x + 10, y0 + 30); continue; }
        const img = await loadImage(f);
        const sw = cx1 - cx0, sh = cy1 - cy0;
        const k = Math.min((CELL_W - 8) / sw, (CELL_H - 8) / sh, 2);
        ctx.imageSmoothingEnabled = true;
        ctx.drawImage(img, cx0, cy0, sw, sh, x + 4, y0 + 4, sw * k, sh * k);
        ctx.strokeStyle = "#aaa"; ctx.strokeRect(x + 2, y0 + 2, CELL_W - 4, CELL_H - 4);
      }
    }
    const fn = path.join(outDir, "sheets", `${gname}-${s + 1}.png`);
    fs.writeFileSync(fn, canvas.toBuffer("image/png"));
    console.log("wrote", fn, chunk.map(c => c.id).join(","));
  }
}
