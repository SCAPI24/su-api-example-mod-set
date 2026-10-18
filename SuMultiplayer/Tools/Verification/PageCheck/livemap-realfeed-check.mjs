/* eslint-disable */
// Real-feed check for Mod/SuMultiplayer/doc/live-map.html
// 用**真实 GET /state**（工具在线时的现场 feed）跑页面自己的脚本，断言：
//   1) 客户端区域线框圈数 == clients[].currentLoaded（每个完整客户端各自相等）；
//   2) 四色 + s=0 中性灰的**逐块**颜色/位置正确（含"未定稿块（s=0）也有线框"）；
//   3) 主机侧 0 个方框（没有任何虚线框 / 同色填充）；
//   4) 图例两组标题 + 中性灰说明 + 统计新行文案正确；UI 上没有验证框图例/勾选框。
//
// 用法： node livemap-realfeed-check.mjs [state-url]
//   默认 http://127.0.0.1:8738/state
import fs from "node:fs";
import vm from "node:vm";

const htmlPath = "P:/Ugit/Survivalcraft/Mod/SuMultiplayer/doc/live-map.html";
const stateUrl = process.argv[2] || "http://127.0.0.1:8738/state";

let pass = 0, fail = 0;
const failures = [];
function check(ok, msg) {
  if (ok) { pass++; console.log("  \u2713 " + msg); return; }
  fail++; failures.push(msg); console.log("  \u2717 " + msg);
}
function section(name) { console.log("\n== " + name + " =="); }

const SC_COLORS = ["#e0a800", "#17a273", "#d1495f", "#2f6fd0"];
const COLOR_OF_STATE = { 1: "#e0a800", 2: "#17a273", 3: "#d1495f", 4: "#2f6fd0" };
const GRAY = "rgba(146,155,168,.95)";        // 页面里 s=0 的中性灰线框

function ctxStub() {
  return {
    ops: [], fillStyle: "#000", strokeStyle: "#000", lineWidth: 1, globalAlpha: 1,
    font: "", textAlign: "", textBaseline: "", dash: [], r: [], l: [],
    setTransform() {}, save() {}, restore() {},
    setLineDash(d) { this.dash = (d || []).slice(); },
    beginPath() { this.r = []; this.l = []; },
    rect(x, y, w, h) { this.r.push([x, y, w, h]); },
    moveTo(x, y) { this.l.push([x, y]); },
    lineTo(x, y) { this.l.push([x, y]); },
    arc(x, y, r) { this.l.push(["arc", x, y, r]); },
    fill() { this.ops.push({ op: "fill", style: this.fillStyle, rects: this.r.slice(), lines: this.l.slice() }); },
    stroke() { this.ops.push({ op: "stroke", style: this.strokeStyle, lw: this.lineWidth, dash: this.dash.slice(), rects: this.r.slice() }); },
    fillRect() {}, strokeRect() {},
    fillText(t, x, y) { this.ops.push({ op: "fillText", text: String(t), x, y }); },
    strokeText() {},
    measureText(t) { return { width: String(t).length * 7 }; }
  };
}
function el(id) {
  return {
    id, innerHTML: "", textContent: "", value: "", checked: false, disabled: false,
    style: {}, attrs: {}, h: {},
    setAttribute(k, v) { this.attrs[k] = String(v); },
    getAttribute(k) { return this.attrs[k] === undefined ? null : this.attrs[k]; },
    addEventListener(e, f) { (this.h[e] = this.h[e] || []).push(f); },
    querySelectorAll() { return []; },
    getBoundingClientRect() { return { left: 0, top: 0, width: 1280, height: 800 }; },
    fire(e, a) { (this.h[e] || []).forEach((f) => f(a)); }
  };
}
async function runPage(feed) {
  const html = fs.readFileSync(htmlPath, "utf8");
  const m = html.match(/<script>([\s\S]*?)<\/script>/);
  const script = m[1];
  const els = new Map();
  const ctx = ctxStub();
  const canvas = el("map");
  canvas.getContext = () => ctx;
  els.set("map", canvas);
  const documentStub = {
    getElementById(id) { if (!els.has(id)) els.set(id, el(id)); return els.get(id); },
    createElement(tag) { const e = el(tag); e.getContext = () => ctxStub(); return e; }
  };
  const clock = { now: 1e6 };
  class FakeDate extends Date { constructor(...a) { super(...(a.length ? a : [clock.now])); } static now() { return clock.now; } }
  const sandbox = {
    document: documentStub,
    window: { innerWidth: 1280, innerHeight: 800, pageXOffset: 0, pageYOffset: 0, scrollTo() {}, addEventListener() {} },
    location: { search: "" },
    fetch: () => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(feed) }),
    setInterval: () => 1, clearInterval: () => {}, setTimeout: () => 1, clearTimeout: () => {},
    Date: FakeDate,
    atob: (s) => Buffer.from(String(s), "base64").toString("binary"),
    URLSearchParams, console
  };
  vm.createContext(sandbox);
  vm.runInContext(script, sandbox, { filename: "live-map.html<script>" });
  await new Promise((r) => setTimeout(r, 80));
  return { els, ctx };
}
function frameOf(els) {
  const sb = String(els.get("statBody").innerHTML);
  const m = sb.match(/canvas 尺寸（格 × 比例）<\/td><td class='mono'>([\d.]+) × ([\d.]+) px/);
  const sc = String(els.get("scaleChip").textContent).split(" ")[0];
  const scale = Number(sc);
  return m && isFinite(scale) && scale > 0 ? { w: +m[1], h: +m[2], scale } : null;
}
// 页面：角色格精确居中 → 反推世界格 → CSS 像素映射
function worldMap(els, state) {
  const f = frameOf(els);
  if (!f) return null;
  const players = state.players || [];
  const sel = players.find((p) => p.key === (state.center && state.center.key)) || players[0];
  if (!sel || !sel.cell) return null;
  const x0 = sel.cell[0] + 0.5 - (f.w / 2) / f.scale;
  const z0 = sel.cell[2] + 0.5 - (f.h / 2) / f.scale;
  return {
    scale: f.scale, x0, z0,
    px: (wx) => (wx - x0) * f.scale,
    pz: (wz) => (wz - z0) * f.scale
  };
}
function wireOps(ctx) {
  return ctx.ops.filter((o) => o.op === "stroke" && o.rects.length &&
    (o.style === GRAY || SC_COLORS.indexOf(o.style) >= 0));
}
function isHostBoxStroke(o) {
  return o.dash.length > 0 && o.rects.length > 0;
}

const main = async () => {
  const res = await fetch(stateUrl, { cache: "no-store" });
  if (!res.ok) { console.error("GET " + stateUrl + " → HTTP " + res.status); process.exit(2); }
  const state = await res.json();
  const ts = new Date().toISOString();
  console.log("真实 feed：" + stateUrl + " @ " + ts);
  console.log("payloadVersion=" + state.payloadVersion + " online=" + state.online +
    " ageMs=" + state.ageMs + " hostChunks=" + (state.chunks || []).length);
  section("0. 载荷版本 v9（动物字段随 v9 引入；v8 应被拒绝）");
  check(state.payloadVersion === 9,
    "live feed 的 payloadVersion = " + state.payloadVersion + "（本页面按 v9 读字段；v8 会在图例报警且没有 animals）");

  const { els, ctx } = await runPage(state);
  const clients = (state.clients || []).filter((c) => c && c.complete !== false);
  const allChunks = clients.reduce((a, c) => a.concat(c.chunks || []), []);
  const expected = { 0: 0, 1: 0, 2: 0, 3: 0, 4: 0 };
  for (const e of allChunks) if (e.length === 3 && e[2] >= 0 && e[2] <= 4) expected[e[2]]++;

  section("1. 线框圈数 == clients[].currentLoaded（含 s=0 未定稿块）");
  const ops = wireOps(ctx);
  const by = {};
  ops.forEach((o) => { by[o.style] = (by[o.style] || 0) + o.rects.length; });
  const total = Object.values(by).reduce((a, b) => a + b, 0);
  const declared = clients.reduce((a, c) => a + (typeof c.currentLoaded === "number" ? c.currentLoaded : 0), 0);
  const chunkCount = allChunks.length;
  check(total === declared, "线框合计 " + total + " == currentLoaded 合计 " + declared);
  check(total === chunkCount, "线框合计 " + total + " == clients[].chunks 条目合计 " + chunkCount);
  check(declared === chunkCount, "currentLoaded 合计 == chunks 条数（" + chunkCount + "）");
  if (expected[0] > 0) {
    check((by[GRAY] || 0) === expected[0],
      "未定稿块（s=0，海面/外圈）也画线框：" + (by[GRAY] || 0) + " 圈中性灰（feed 里 s=0 共 " + expected[0] + " 块）");
  } else {
    check((by[GRAY] || 0) === 0, "本次 feed 没有 s=0 块 → 没有中性灰线框（0 圈）");
  }

  section("2. 四色 + s=0 逐块颜色与位置");
  const map = worldMap(els, state);
  check(!!map, "从统计面板 / 比例 chip 推出画布映射（scale=" + (map ? map.scale : "—") + " px/格）");
  if (map) {
    const cellScale = map.scale;
    const side = 16 * cellScale;
    // 页面规则（已修）：**所有客户端线框几何完全相同**，都画在区块边界上（不内缩、不加 0.5）；
    // 多客户端重叠只靠线宽分层（i=0 最细）。
    const order = clients.slice().sort((a, b) => String(a.key) < String(b.key) ? -1 : (String(a.key) > String(b.key) ? 1 : 0));
    const rectBy = {};
    ops.forEach((o) => {
      o.rects.forEach((r) => {
        const k = Math.round(r[0] * 100) / 100 + "," + Math.round(r[1] * 100) / 100;
        (rectBy[k] = rectBy[k] || []).push(o.style);
      });
    });
    let posOk = 0, colorOk = 0, badPos = [], badColor = [];
    for (const c of order) {
      for (const e of (c.chunks || [])) {
        const cx = e[0], cz = e[1], s = e[2];
        // 区块边界坐标（与地形同一个 16×16 映射；不内缩、不加 0.5）
        const bx = (cx * 16 - map.x0) * cellScale;
        const bz = (cz * 16 - map.z0) * cellScale;
        const k = Math.round(bx * 100) / 100 + "," + Math.round(bz * 100) / 100;
        const styles = rectBy[k] || [];
        const want = s === 0 ? GRAY : COLOR_OF_STATE[s];
        if (styles.length) posOk++; else if (badPos.length < 5) badPos.push([cx, cz, s]);
        if (want && styles.indexOf(want) >= 0) colorOk++;
        else if (badColor.length < 5) badColor.push({ chunk: [cx, cz], s: s, want: want, got: styles });
      }
    }
    check(posOk === allChunks.length, "逐块位置：" + posOk + "/" + allChunks.length + " 块都有对应线框" +
      (badPos.length ? "（缺：" + JSON.stringify(badPos) + "）" : ""));
    check(colorOk === allChunks.length, "逐块颜色：" + colorOk + "/" + allChunks.length + " 块颜色正确（s=0 中性灰 / 2 绿 / 4 蓝 / 1 黄 / 3 红）" +
      (badColor.length ? "（反例：" + JSON.stringify(badColor) + "）" : ""));
  }

  section("3. 对齐：线框矩形边界 == 地形 16×16 区块边界（容差 ≤0.5px）");
  const cellScale = map.scale;
  const side = 16 * cellScale;
  {
    const hostSet = new Set((state.chunks || []).map((c) => c.x + "," + c.z));
    const terrainRects = ctx.ops.filter((o) => o.op === "fill" && o.rects.length > 50).flatMap((o) => o.rects);
    const wireAll = wireOps(ctx);
    const wireAt = (bx, bz) => wireAll.find((o) => o.rects.some((r) => Math.abs(r[0] - bx) < 0.01 && Math.abs(r[1] - bz) < 0.01));
    const overlap = [];
    for (const c of clients) for (const e of (c.chunks || [])) if (hostSet.has(e[0] + "," + e[1])) overlap.push(e);
    check(overlap.length > 0, "存在同时属于主机 chunks[] 与客户端 chunks 的区块（可做对齐比对，共 " + overlap.length + " 块）");
    let checked = 0, worst = 0, bad = [];
    for (const e of overlap) {
      const bx = (e[0] * 16 - map.x0) * cellScale, bz = (e[1] * 16 - map.z0) * cellScale;
      let mnx = 1e9, mnz = 1e9, mxx = -1e9, mxz = -1e9, n = 0;
      for (const r of terrainRects) {
        if (r[0] >= bx - 0.01 && r[0] < bx + side - 0.01 && r[1] >= bz - 0.01 && r[1] < bz + side - 0.01) {
          mnx = Math.min(mnx, r[0]); mnz = Math.min(mnz, r[1]);
          mxx = Math.max(mxx, r[0] + r[2]); mxz = Math.max(mxz, r[1] + r[3]); n++;
        }
      }
      if (!n) continue;
      const op = wireAt(bx, bz);
      if (!op) continue;
      const w = op.rects.find((r) => Math.abs(r[0] - bx) < 0.01 && Math.abs(r[1] - bz) < 0.01);
      const d = Math.max(Math.abs(w[0] - mnx), Math.abs(w[1] - mnz), Math.abs(w[0] + w[2] - mxx), Math.abs(w[1] + w[3] - mxz));
      worst = Math.max(worst, d);
      checked++;
      if (d > 0.5) bad.push([e[0], e[1], Math.round(d * 1000) / 1000]);
    }
    check(checked > 0 && bad.length === 0,
      "对齐回归：" + checked + " 个重合区块的线框边界 vs 地形 16×16 边界最大偏差 " + worst.toFixed(4) +
      " px（要求 ≤0.5）" + (bad.length ? " 超差：" + JSON.stringify(bad) : ""));
    // 客户端独有区块：没有主机地形（无底图可比 → 不是错位），但仍画线框
    const clientOnly = [];
    for (const c of clients) for (const e of (c.chunks || [])) if (!hostSet.has(e[0] + "," + e[1])) clientOnly.push(e);
    if (clientOnly.length) {
      const noTerrain = clientOnly.filter((e) => {
        const bx = (e[0] * 16 - map.x0) * cellScale, bz = (e[1] * 16 - map.z0) * cellScale;
        return !terrainRects.some((r) => r[0] >= bx - 0.01 && r[0] < bx + side - 0.01 && r[1] >= bz - 0.01 && r[1] < bz + side - 0.01);
      }).length;
      check(noTerrain === clientOnly.length, "客户端独有区块 " + clientOnly.length + " 块没有主机地形（无底图可比 → 不是错位）");
    } else {
      check(true, "本次 feed 客户端区块全部落在主机 chunks[] 内（无客户端独有区块）");
    }
  }

  section("4. 主机侧 0 个方框");
  const dashed = ctx.ops.filter((o) => o.op === "stroke" && isHostBoxStroke(o));
  const boxFills = ctx.ops.filter((o) => o.op === "fill" && SC_COLORS.indexOf(o.style) >= 0);
  check(dashed.length === 0, "主机侧没有任何虚线框（含主机区域边界）—— 实际 " + dashed.length);
  check(boxFills.length === 0, "没有任何主机验证框的同色填充 —— 实际 " + boxFills.length);
  check(!els.has("verifyLegend") && !els.has("ckVerify"), "UI 上没有验证框图例容器 / 「显示验证框」勾选框");
  const halo = ctx.ops.filter((o) => o.op === "stroke" && /rgba\(10,14,22/.test(o.style) && o.rects.length > 0);
  const haloCover = halo.reduce((a, o) => a + o.rects.length, 0);
  const uniqueChunks = new Set(allChunks.map((e) => e[0] + "," + e[1])).size;
  check(haloCover === uniqueChunks,
    "客户端线框有深色底衬：halo 覆盖 " + haloCover + " 个**唯一区块**（多客户端同块只垫一次，= " + uniqueChunks + "）；线框圈数 " + total);

  section("5. 动物（animals[]）：只画黑色实心圆点，数量 == animals[] 条数");
  {
    const GRAY2 = "rgba(146,155,168,.95)";
    const animalFills = ctx.ops.filter((o) => o.op === "fill" && o.style === "#000");
    const animalDots = animalFills.flatMap((o) => (o.lines || []).filter((l) => l[0] === "arc"));
    const animals = Array.isArray(state.animals) ? state.animals : null;
    check(animals !== null, "live feed 带 animals 字段（v9）");
    if (animals) {
      check(animalDots.length === animals.length,
        "黑点数量 " + animalDots.length + " == animals[] 条数 " + animals.length);
      if (!state.counts || typeof state.counts.animals !== "number") {
        check(false, "counts.animals 缺失（v9 契约要求）");
      } else {
        check(state.counts.animals === animals.length,
          "counts.animals " + state.counts.animals + " == animals[] 条数 " + animals.length);
      }
      const rad = animalDots.length ? animalDots[0][3] : 0;
      check(animalDots.length === 0 || (rad >= 2 && rad <= 3), "黑点半径在 2–3 px（实际 " + rad + "）");
    }
    // 动物不产生任何文字：同一 feed 去掉 animals 再渲染，canvas 文字集合必须完全一致
    const noAnimals = JSON.parse(JSON.stringify(state));
    delete noAnimals.animals;
    if (noAnimals.counts) delete noAnimals.counts.animals;
    const { ctx: ctxNo } = await runPage(noAnimals);
    const textsOf = (c) => c.ops.filter((o) => o.op === "fillText").map((o) => o.text).sort().join("\u0001");
    check(textsOf(ctxNo) === textsOf(ctx), "动物不产生任何文字标签（有 / 无 animals 的文字集合一致）");
    const rl2 = String(els.get("regionLegend").innerHTML).replace(/<[^>]+>/g, " ").replace(/\s+/g, " ");
    const sb2 = String(els.get("statBody").innerHTML).replace(/<[^>]+>/g, " ").replace(/\s+/g, " ");
    check(/动物（主机端，仅位置）/.test(rl2) && /黑点/.test(rl2), "图例有「动物（主机端，仅位置）· 黑点」");
    check(/动物（animals\[\]）/.test(sb2), "统计面板有「动物（animals[]）」行");
  }

  section("6. 图例 / 统计文案");
  const rl = String(els.get("regionLegend").innerHTML).replace(/<[^>]+>/g, " ").replace(/\s+/g, " ");
  const sb = String(els.get("statBody").innerHTML).replace(/<[^>]+>/g, " ").replace(/\s+/g, " ");
  check(/主机地形（只填充、无框/.test(rl), "图例第 1 组 =「主机地形（只填充、无框，来源 chunks[]）」");
  check(/客户端区域线框（当前内存区块）/.test(rl), "图例第 2 组 =「客户端区域线框（当前内存区块）」");
  check(/中性灰 = 已加载、尚未上报\/裁决/.test(rl), "图例写明中性灰 = 已加载、尚未上报/裁决（s=0）");
  if (expected[0] > 0) {
    check(/中性灰 s=0 \d+/.test(rl), "图例计数行给出 s=0 中性灰块数");
  } else {
    check(true, "本次 feed 没有 s=0 块 → 图例不显示中性灰计数（正常）");
  }
  check(!/验证框/.test(rl) && !/验证框/.test(sb), "图例 / 统计面板都不再出现「验证框」字样");
  const rowMatch = sb.match(/主机地形覆盖 [0-9]+ /);
  check(!!rowMatch && sb.indexOf("无任何方框") >= 0 && sb.indexOf("客户端线框 " + total + " 圈") >= 0,
    "统计面板：「主机地形覆盖 N 块（无任何方框）· 客户端线框 " + total + " 圈」——实际：" +
    ((sb.match(/主机地形 \/ 客户端线框[^|]{0,120}/) || ["无"])[0]));

  console.log("\n---------------------------------------------");
  console.log("真实 feed 结果：" + pass + "/" + (pass + fail) + " 通过" + (fail ? "，" + fail + " 失败" : ""));
  if (fail) { console.log("失败项："); failures.forEach((f) => console.log("  - " + f)); process.exit(1); }
  console.log("RESULT livemap real-feed cases passed");
};

main().catch((e) => { console.error("检查脚本自身异常：" + (e && e.stack ? e.stack : e)); process.exit(2); });
