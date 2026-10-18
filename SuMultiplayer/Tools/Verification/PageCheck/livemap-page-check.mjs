/* eslint-disable */
// Headless check for Mod/SuMultiplayer/doc/live-map.html
// 用一个"录屏式" canvas 2D 上下文 + DOM 桩，在 Node 里跑页面自己的 <script>，
// 喂合成 feed（两个角色离得很远：主机 chunk(-5,8) / 客户端 chunk(-13,13)），
// 然后对录到的绘制指令与 DOM 文本做断言。
//
// 用法： node livemap-page-check.mjs [live-map.html 路径]

import fs from "node:fs";
import vm from "node:vm";

const htmlPath = process.argv[2] || "P:/Ugit/Survivalcraft/Mod/SuMultiplayer/doc/live-map.html";
const html = fs.readFileSync(htmlPath, "utf8");
const scriptMatch = html.match(/<script>([\s\S]*?)<\/script>/);
if (!scriptMatch) {
  console.error("找不到页面里的 <script> 块：" + htmlPath);
  process.exit(2);
}
const pageScript = scriptMatch[1];
// 只给测试插一个拟合计数器：把页面 IIFE 外层加一个全局 var，并在 fitWholeRegion 开头自增。
// 页面自身不改；页面结构变了就明确失败，而不是悄悄测不到。
const instrumentedScript = pageScript
  .replace("(function () {", "var __lmFitCount = 0;\r\n(function () {")
  .replace("function fitWholeRegion() {", "function fitWholeRegion() { __lmFitCount++;");
if (!/var __lmFitCount = 0;/.test(instrumentedScript) || !/__lmFitCount\+\+/.test(instrumentedScript)) {
  console.error("无法给页面脚本插入拟合计数（页面结构可能变了）：" + htmlPath);
  process.exit(2);
}

let pass = 0, fail = 0;
const failures = [];
function check(ok, msg) {
  if (ok) { pass++; console.log("  \u2713 " + msg); return; }
  fail++; failures.push(msg); console.log("  \u2717 " + msg);
}
function section(name) { console.log("\n== " + name + " =="); }

/* ---------------- canvas 2D 桩：把绘制指令录下来 ---------------- */
function makeCtx() {
  return {
    ops: [],
    fillStyle: "#000", strokeStyle: "#000", lineWidth: 1, globalAlpha: 1,
    font: "", textAlign: "", textBaseline: "", imageSmoothingEnabled: true,
    dash: [], pendingRects: [], pendingLines: [],
    setTransform() {}, save() {}, restore() {},
    setLineDash(d) { this.dash = (d || []).slice(); },
    beginPath() { this.pendingRects = []; this.pendingLines = []; },
    rect(x, y, w, h) { this.pendingRects.push([x, y, w, h]); },
    moveTo(x, y) { this.pendingLines.push(["m", x, y]); },
    lineTo(x, y) { this.pendingLines.push(["l", x, y]); },
    arc(x, y, r) { this.pendingLines.push(["arc", x, y, r]); },
    fill() {
      // 动物的黑点是 fill(arc)，没有 rect：把路径也记下来（rects 断言不受影响）
      this.ops.push({ op: "fill", style: this.fillStyle, alpha: this.globalAlpha, rects: this.pendingRects.slice(), lines: this.pendingLines.slice() });
    },
    stroke() {
      this.ops.push({
        op: "stroke", style: this.strokeStyle, lw: this.lineWidth, dash: this.dash.slice(),
        rects: this.pendingRects.slice(), lines: this.pendingLines.slice()
      });
    },
    fillRect(x, y, w, h) { this.ops.push({ op: "fillRect", x, y, w, h, style: this.fillStyle }); },
    strokeRect(x, y, w, h) { this.ops.push({ op: "strokeRect", x, y, w, h, style: this.strokeStyle, dash: this.dash.slice() }); },
    fillText(t, x, y) { this.ops.push({ op: "fillText", text: String(t), x, y, style: this.fillStyle }); },
    strokeText(t, x, y) { this.ops.push({ op: "strokeText", text: String(t), x, y, style: this.strokeStyle, lw: this.lineWidth }); },
    measureText(t) { return { width: String(t).length * 7.2 }; }
  };
}

function makeEl(id) {
  return {
    id, className: "", textContent: "", innerHTML: "", value: "", checked: false, disabled: false,
    style: {}, attrs: {}, handlers: {},
    setAttribute(k, v) { this.attrs[k] = String(v); },
    getAttribute(k) { return Object.prototype.hasOwnProperty.call(this.attrs, k) ? this.attrs[k] : null; },
    addEventListener(ev, fn) { (this.handlers[ev] = this.handlers[ev] || []).push(fn); },
    querySelectorAll() { return []; },
    getBoundingClientRect() { return { left: 0, top: 0, width: 1200, height: 800 }; },
    fire(ev, arg) { (this.handlers[ev] || []).forEach((fn) => fn(arg)); }
  };
}

/* ---------------- 跑一次页面脚本（喂一份 feed） ---------------- */
async function runPage(feed, search) {
  const els = new Map();
  const ctx = makeCtx();
  const canvas = makeEl("map");
  canvas.getContext = () => ctx;
  canvas.width = 10; canvas.height = 10;
  els.set("map", canvas);

  const documentStub = {
    getElementById(id) {
      if (!els.has(id)) els.set(id, makeEl(id));
      return els.get(id);
    },
    createElement(tag) { const e = makeEl(tag); e.getContext = () => makeCtx(); return e; }
  };

  // 假时钟 + 可控定时器：自动拟合的 3 s 静默期 / 250 ms 节流要能确定性地测试。
  const clock = { now: 1000000 };
  class FakeDate extends Date {
    constructor(...args) { super(...(args.length ? args : [clock.now])); }
    static now() { return clock.now; }
  }
  const timers = [];
  let timerSeq = 0;
  function runDueTimers() {
    for (;;) {
      timers.sort((a, b) => a.at - b.at);
      const t = timers[0];
      if (!t || t.at > clock.now) break;
      timers.shift();
      try { t.fn(); } catch (e) { /* 与浏览器一致：定时器异常不阻断主流程 */ }
    }
  }
  function advance(ms) {
    clock.now += Math.max(0, Number(ms) || 0);
    runDueTimers();
  }
  function setTimeoutStub(fn, delay) {
    const id = ++timerSeq;
    timers.push({ id, at: clock.now + Math.max(0, Number(delay) || 0), fn });
    return id;
  }
  function clearTimeoutStub(id) {
    const i = timers.findIndex((t) => t.id === id);
    if (i >= 0) timers.splice(i, 1);
  }

  // 多帧：reload(nextFeed) 把下一次 fetch 的响应换掉，再点页面自己的「立即刷新」。
  let currentFeed = Array.isArray(feed) ? feed[0] : feed;
  const sandbox = {
    document: documentStub,
    window: {
      innerWidth: 1280, innerHeight: 800, pageXOffset: 0, pageYOffset: 0,
      scrollTo() {}, addEventListener() {}
    },
    location: { search: search || "" },
    fetch: () => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(currentFeed) }),
    setInterval: () => 1,
    clearInterval: () => {},
    setTimeout: setTimeoutStub,
    clearTimeout: clearTimeoutStub,
    Date: FakeDate,
    atob: (s) => Buffer.from(String(s), "base64").toString("binary"),
    URLSearchParams,
    console
  };
  vm.createContext(sandbox);
  vm.runInContext(instrumentedScript, sandbox, { filename: "live-map.html<script>" });
  await new Promise((r) => setTimeout(r, 40));

  async function reload(nextFeed, advanceMs) {
    if (nextFeed !== undefined) currentFeed = nextFeed;
    if (advanceMs) advance(advanceMs);
    els.get("btnReload").fire("click");
    await new Promise((r) => setTimeout(r, 20));
  }
  function scaleOfPage() {
    const el = els.get("scaleChip");
    return Number(String(el ? el.textContent : "").split(" ")[0]);
  }
  return {
    ctx, els, canvas, sandbox, clock, timers,
    advance, reload, scale: scaleOfPage,
    fitCount: () => sandbox.__lmFitCount
  };
}

/* ---------------- 合成 feed ---------------- */
function chunkData(contentsId, top) {
  const b = Buffer.alloc(768);
  for (let i = 0; i < 256; i++) { b.writeUInt16LE(contentsId, i * 2); b[512 + i] = top; }
  return b.toString("base64");
}
const HOST_CELL = [-75, 70, 140];        // chunk(-5,8)
const CLIENT_CELL = [-207, 70, 217];     // chunk(-13,13)
const HOST_CHUNKS = [
  [-6, 7], [-6, 8], [-6, 9],
  [-5, 7], [-5, 8], [-5, 9],
  [-4, 7], [-4, 8], [-4, 9],
  [-12, 13]                              // 与客户端区域重叠的那一块
];
// v7：每条 = [cx, cz, s]（s = 该块相对**这个客户端自己**的校验状态）
//   绿 2 = 6 块 / 蓝 4 = 1 块 / 黄 1 = 1 块 / 红 3 = 1 块
const CLIENT_CHUNKS = [
  [-14, 12, 2], [-13, 12, 2], [-12, 12, 2],
  [-14, 13, 3], [-13, 13, 2], [-12, 13, 4],
  [-14, 14, 1], [-13, 14, 2], [-12, 14, 2]
];
// 第二个玩家端：与主机 / 第一个客户端都不重叠的一块（15,15）……（17,17），另加**一块真正落在主机 chunks[]
// 里**的区块（-5,8）—— 后者用来证明"主机视图的并集真的把每个客户端的地形都画进来了"，而不是只画了两个区域。
// 第二个玩家端：11 块，其中 (-5,8) 落在主机 chunks[] 里、(-13,12) 与客户端 1234 的区块**互相重叠**
// （后者用来验证"多个客户端覆盖同一块 → 按稳定键排序逐个内缩"的规则）。
//   绿 2 = 6 / 蓝 4 = 2 / 黄 1 = 2 / 红 3 = 1
const CLIENT2_CHUNKS = [
  [15, 15, 2], [16, 15, 2], [17, 15, 2],
  [15, 16, 4], [16, 16, 2], [17, 16, 2],
  [15, 17, 1], [16, 17, 3], [17, 17, 2],
  [-5, 8, 4], [-13, 12, 1]
];
function baseFeed() {
  return {
    t: 123456, online: true, ageMs: 120, samples: 5, droppedPartial: 0,
    payloadVersion: 9, decodeFailures: 0, decodeFailureReason: "",
    view: "all", radius: 0,
    watched: { id: 1234, name: "Android User" },
    center: { cell: HOST_CELL, cellReason: "", chunk: [-5, 8] },
    players: [
      { id: -1, name: "Host", isHost: true, cell: HOST_CELL, cellReason: "", chunk: [-5, 8] },
      { id: 1234, name: "Android User", isHost: false, cell: CLIENT_CELL, cellReason: "", chunk: [-13, 13] }
    ],
    centers: [
      { id: -1, name: "Host", isHost: true, chunk: [-5, 8] },
      { id: 1234, name: "Android User", isHost: false, chunk: [-13, 13] }
    ],
    clients: [
      { id: 1234, name: "Android User", reported: 9, currentLoaded: 9, merged: 9, complete: true, chunks: CLIENT_CHUNKS }
    ],
    // v9：主机端动物（仅位置；页面只画黑色实心圆点，不画名字/坐标标签）
    animals: [
      { x: -100.5, y: 70, z: 120.5 },
      { x: -90.2, y: 70, z: 130.8 },
      { x: -80.9, y: 70, z: 141.2 }
    ],
    chunks: HOST_CHUNKS.map(([x, z], i) => ({ x, z, s: i === 0 ? 1 : (i === 1 ? 3 : 2), n: 0, d: chunkData(2, 64) })),
    bounds: { chunkMin: [-14, 7], chunkMax: [-4, 14], cellMin: [-224, 112], cellMax: [-49, 239] },
    boundsSource: "hostChunks+clientChunks",
    counts: { chunks: 10, columns: 2560, players: 2, clientChunks: 9, currentLoadedChunks: 9, animals: 3, pendingMarks: 0, pendingClients: 0, clientsComplete: true, clientsIncompleteReason: "" }
  };
}
/* 两个玩家端的 feed：主机 chunks[] 10 块 + Android User 9 块（含 1 块与主机重叠）+ Tablet User 10 块 */
function twoClientFeed() {
  const feed = baseFeed();
  feed.players.push({ id: 4321, name: "Tablet User", isHost: false, cell: [258, 70, 258], cellReason: "", chunk: [16, 16] });
  feed.clients.push({ id: 4321, name: "Tablet User", reported: 11, currentLoaded: 11, merged: 11, complete: true, chunks: CLIENT2_CHUNKS });
  feed.counts = { chunks: 10, columns: 2560, players: 3, clientChunks: 20, currentLoadedChunks: 20, animals: 3, pendingMarks: 0, pendingClients: 0, clientsComplete: true, clientsIncompleteReason: "" };
  feed.bounds = { chunkMin: [-14, 7], chunkMax: [17, 17], cellMin: [-224, 112], cellMax: [287, 287] };
  feed.boundsSource = "hostChunks+clientChunks";
  return feed;
}

/* ---------------- 录到的绘制指令的常用查询 ---------------- */
const MAGENTA_FILL = (rgb) => "rgba(181,23,158," + rgb + ")";
function fills(ctx, style) { return ctx.ops.filter((o) => o.op === "fill" && o.style === style); }
function strokes(ctx, style) { return ctx.ops.filter((o) => o.op === "stroke" && o.style === style); }
const SIDE_124 = 16 * 12.4;
const SC_COLORS = ["#e0a800", "#17a273", "#d1495f", "#2f6fd0"];
// 主机侧**不画任何框**（用户要求：主机端不需要任何方框，主机只保留地形填充）。
// 图上唯一的框层 = 客户端区域线框：**实线**、粗、画在区块边界上、带深色 halo。
function solidColorStrokes(ctx) {
  return ctx.ops.filter((o) => o.op === "stroke" && SC_COLORS.indexOf(o.style) >= 0 && !o.dash.length);
}
function wireRects(ctx, color) {
  const out = [];
  strokes(ctx, color).forEach((o) => {
    if (o.dash.length) return;                        // 客户端线框一律实线
    o.rects.forEach((r) => { if (!(Math.abs(r[3] - 20) < 0.01)) out.push(r); });
  });
  return out;
}
function wireRectsAll(ctx) {
  const out = [];
  solidColorStrokes(ctx).forEach((o) => {
    o.rects.forEach((r) => {
      if (Math.abs(r[3] - 20) < 0.01) return;         // 角色标签边框
      out.push(r);
    });
  });
  return out;
}
// 某客户端区块的唯一线框（按区块→像素映射找矩形左上角；inset 由调用方给出）
function wireRectOf(ctx, color, map, cx, cz, inset) {
  const bx = map.px(cx * 16) + (inset || 0), bz = map.pz(cz * 16) + (inset || 0);
  return wireRects(ctx, color).find((r) => Math.abs(r[0] - bx) < 0.01 && Math.abs(r[1] - bz) < 0.01) || null;
}
// 填充是否落在某个"只有客户端"的区块足迹里（用来证明客户端区块**不填地形**）
function fillsInsideChunks(ctx, chunks) {
  const bad = [];
  const scale = 12.4;
  ctx.ops.forEach((o) => {
    if (o.op !== "fill") return;
    o.rects.forEach((r) => {
      const wx = r[0] / scale, wz = r[1] / scale;   // 只做相对判断：区块足迹按同一比例缩放
      const ch = chunks.find(([cx, cz]) => wx >= cx * 16 && wx < cx * 16 + 16 && wz >= cz * 16 && wz < cz * 16 + 16);
      if (ch) bad.push([ch[0], ch[1]]);
    });
  });
  return bad;
}
function rectsOf(ops) { var out = []; ops.forEach((o) => o.rects.forEach((r) => out.push(r))); return out; }
function texts(ctx) { return ctx.ops.filter((o) => o.op === "fillText").map((o) => o.text); }
// 页面在 CSS 画布超过 8192 px 时会**故意**降采样后备存储：canvas.width = cssW × dprUsed（不是 cssW）。
// dprUsed 直接读页面自己写在统计面板「canvas 后备像素」那一行的值，避免测试自己猜。
function dprUsedOf(els) {
  const m = els.get("statBody").innerHTML.match(/canvas 后备像素<\/td><td class='mono'>([\d.]+) × 逻辑像素/);
  return m ? Number(m[1]) : 1;
}
function canvasBackingOf(canvas, els) { return [canvas.width, canvas.height, dprUsedOf(els)]; }
// 画布 CSS 尺寸（页面自己写在统计面板「canvas 尺寸（格 × 比例）」行的 "W × H px = ..."）
function frameCssOf(els) {
  const m = els.get("statBody").innerHTML.match(/canvas 尺寸（格 × 比例）<\/td><td class='mono'>([\d.]+) × ([\d.]+) px/);
  return m ? [Number(m[1]), Number(m[2])] : null;
}
function sceneOf(canvas, els) { return { dpr: dprUsedOf(els), size: frameCssOf(els) }; }
// 页面把十字画在**逻辑（CSS）像素**坐标上；画布后备存储可能是 cssW × dprUsed（超大画布降采样）。
// 两个坐标系都有的量是"画布 CSS 尺寸"（页面写在统计面板里）——用它判断"是否精确居中"。
function crossIsCentred(c, canvas, els) {
  if (!c) return false;
  const size = frameCssOf(els);
  if (!size) return false;
  return Math.abs(c[0] - size[0] / 2) < 1 && Math.abs(c[1] - size[1] / 2) < 1;
}
// 从"画布 CSS 尺寸"反推世界格 → CSS 像素映射（与页面 frameOf 的规则一致：角色格精确居中）
function worldToCss(els, who, scale) {
  const size = frameCssOf(els);
  if (!size) return null;
  const x0 = who[0] + 0.5 - (size[0] / 2) / scale;
  const z0 = who[2] + 0.5 - (size[1] / 2) / scale;
  return { x0, z0, wPx: size[0], hPx: size[1], px: (wx) => (wx - x0) * scale, pz: (wz) => (wz - z0) * scale };
}
// 该 chunk 是否完全落在画布之外
function chunkOffCanvas(map, cx, cz, scale) {
  const side = 16 * scale;
  const bx = map.px(cx * 16), bz = map.pz(cz * 16);
  return (bx + side <= 0 || bz + side <= 0 || bx >= map.wPx || bz >= map.hPx);
}
function markerCentre(ctx) {
  // 选中角色的十字用 arc(cx,cz,4) 画圆点：从描边指令里取半径 4 的 arc
  const ops = ctx.ops.filter((o) => o.op === "stroke");
  for (const o of ops) {
    for (const l of o.lines) if (l[0] === "arc" && l[3] === 4) return [l[1], l[2]];
  }
  return null;
}
// 格内数字（chunks[].n）：页面用 fillText/strokeText 画纯数字文本；刻度标签形如 "x-75"，标记标签含名字，
// 所以 /^\d+$/ 能精确挑出数字。
function digitOps(ctx) { return ctx.ops.filter((o) => o.op === "fillText" && /^\d+$/.test(o.text)); }
function digitStrokeOps(ctx) { return ctx.ops.filter((o) => o.op === "strokeText" && /^\d+$/.test(o.text)); }
function round3(v) { return Math.round(v * 1000) / 1000; }
// 描边深色底衬（halo）：页面用近黑半透明 rgba（每个通道 ≤ 60、alpha 在 (0,1) 之间）。
// 判定只看颜色本身，不依赖页面里具体写了哪个常量值。
function isDarkHalo(style) {
  const m = String(style).match(/^rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([\d.]+)\s*\)$/);
  if (!m) return false;
  const rgb = [Number(m[1]), Number(m[2]), Number(m[3])];
  const a = Number(m[4]);
  return Math.max(...rgb) <= 60 && a > 0 && a < 1;
}
function rectKey(r) { return r.map((v) => Math.round(v * 100) / 100).join(","); }
function rectsKey(rects) { return rects.map(rectKey).sort().join(" | "); }
function darkStrokesWithRects(ctx, rects) {
  const key = rectsKey(rects || []);
  return ctx.ops.filter((o) => o.op === "stroke" && isDarkHalo(o.style) && rectsKey(o.rects) === key);
}
function nearRect(a, b, tol) {
  if (!a || !b || a.length !== b.length) return false;
  const t = tol === undefined ? 0.01 : tol;
  return a.every((v, i) => Math.abs(v - b[i]) <= t);
}
// 与页面 fitWholeRegion 同一套兜底常量：1280×800 窗口 → 可用 1160×540 CSS px
function expectedFitScale(colsX, colsZ) {
  const availW = Math.max(240, 1280 - 120);
  const availH = Math.max(200, 800 - 260);
  let s = Math.min(availW / colsX, availH / colsZ);
  if (!isFinite(s) || s <= 0) s = 12.4;
  if (Math.abs(s - 12.4) < 0.06) s = 12.4;         // clampScale 的默认比例吸附
  s = Math.max(0.15, Math.min(40, s));
  return Math.round(s * 1000) / 1000;
}

/* ================= 用例 ================= */
async function main() {
  const htmlBytes = fs.statSync(htmlPath).size;

  section("A. 两个角色 + 两块区域（默认 feed，选中主机）：地形只填主机、客户端画四色线框");
  {
    const feed = baseFeed();
    const { ctx, els, canvas } = await runPage(feed, "");
    const t = texts(ctx);
    check(t.includes("Host (-75,140) chunk(-5,8)"), "主机标记标签 = Host (-75,140) chunk(-5,8)（实际：" + t.filter((x) => x.startsWith("Host "))[0] + "）");
    check(t.includes("Android User (-207,217) chunk(-13,13)"), "客户端标记标签 = Android User (-207,217) chunk(-13,13)（实际：" + t.filter((x) => x.startsWith("Android User"))[0] + "）");

    const c = markerCentre(ctx);
    const sizeA = frameCssOf(els) || [canvas.width, canvas.height];
    check(crossIsCentred(c, canvas, els),
      "选中角色（主机）精确居中：十字中心 (" + (c && c[0].toFixed(2)) + "," + (c && c[1].toFixed(2)) +
      ") = 画布中心 (" + (sizeA[0] / 2) + "," + (sizeA[1] / 2) + ") CSS px");

    // —— 地形填充只来自主机 chunks[]：一个批量填充，10 区块 × 256 列 ——
    const terrain = ctx.ops.filter((o) => o.op === "fill" && o.rects.length > 100);
    check(terrain.length === 1 && terrain[0].rects.length === 10 * 256,
      "地形填充按 (id,高度档) 批量画主机 10 区块：" + (terrain.length ? terrain[0].rects.length : 0) + " 格 = 10 × 256");

    // —— 客户端区块只画线框（四色），不填任何底色 ——
    const wg = wireRects(ctx, "#17a273"), wb = wireRects(ctx, "#2f6fd0"), wy = wireRects(ctx, "#e0a800"), wr = wireRects(ctx, "#d1495f");
    check(wg.length === 6 && wb.length === 1 && wy.length === 1 && wr.length === 1,
      "客户端 9 块全部画成线框并按各自 s 着色：绿 6 / 蓝 1 / 黄 1 / 红 1（实际 " +
      wg.length + "/" + wb.length + "/" + wy.length + "/" + wr.length + "）");
    check(wg.length + wb.length + wy.length + wr.length === 9, "每个客户端区块恰好一圈线框（9 块 = 9 圈，不重复）");
    const magentaFills = ctx.ops.filter((o) => o.op === "fill" && String(o.style).indexOf("181,23,158") >= 0);
    check(magentaFills.length === 0, "不再画品红区域底色（旧版浅底 / 深底均已删除），实际 " + magentaFills.length + " 个");

    // 客户端独有区块（9 块里不在主机 chunks[] 的 8 块）足迹里不允许出现任何 fill
    const hostSetA = new Set(feed.chunks.map((ch) => ch.x + "," + ch.z));
    const clientOnlyA = CLIENT_CHUNKS.filter(([cx, cz]) => !hostSetA.has(cx + "," + cz));
    const mapA = worldToCss(els, HOST_CELL, 12.4);
    const sideA = 16 * 12.4;
    let fillInClientOnly = 0;
    ctx.ops.forEach((o) => {
      if (o.op !== "fill") return;
      o.rects.forEach((r) => {
        if (Math.abs(r[3] - 20) < 0.01) return;          // 角色标签的白色底框（不是地形填充）
        clientOnlyA.forEach(([cx, cz]) => {
          const x0 = mapA.px(cx * 16), z0 = mapA.pz(cz * 16);
          if (r[0] >= x0 - 0.01 && r[0] < x0 + sideA - 0.01 && r[1] >= z0 - 0.01 && r[1] < z0 + sideA - 0.01) fillInClientOnly++;
        });
      });
    });
    check(fillInClientOnly === 0, "客户端独有的 8 个区块里**没有任何填充**（不填地形、不做高度着色）——实际 " + fillInClientOnly + " 格");

    // —— 线框几何与区块格对齐、带深色底衬、实线 ——
    const overlapWire = wireRectOf(ctx, "#2f6fd0", mapA, -12, 13, 0);
    check(!!overlapWire && Math.abs(overlapWire[2] - sideA) < 0.01,
      "线框矩形与地形区块边界严格对齐（宽 = 16×12.4 = " + sideA + " px）");
    const wireHalo = ctx.ops.filter((o) => o.op === "stroke" && isDarkHalo(o.style) && o.rects.length === 9);
    check(wireHalo.length === 1, "9 圈线框下面垫了 1 层深色底衬（同一套矩形，先底衬后彩线）");
    check(strokes(ctx, "#17a273").filter((o) => o.dash.length === 0).length >= 1 &&
      wireRects(ctx, "#17a273").length > 0, "客户端线框是实线（主机验证框才是虚线）");

    // 两块区域在画布上分开
    const clientRight = Math.max(...wg.concat(wb, wy, wr).map((r) => r[0] + r[2]));
    const hostOnlyLeft = Math.min(...terrain[0].rects.filter((r) => r[0] > clientRight + 1).map((r) => r[0]));
    const gap = hostOnlyLeft - clientRight;
    check(gap > 3 * 16 * 12.4, "两块区域在画布上分开：客户端区域右缘与主机区域左缘相隔 " +
      gap.toFixed(1) + " px（> 3 区块 = " + (3 * 16 * 12.4).toFixed(1) + " px）");
    check(canvas.width > (clientRight + gap) && canvas.height > 100, "画布把两块区域都装下（canvas " + canvas.width + "×" + canvas.height + "）");

    // —— 图例：地形填充来源 + 四色线框 + "只画线框不填地形" + 计数 ——
    const rl = els.get("regionLegend").innerHTML;
    check(/主机地形（只填充、无框，来源 <code>chunks\[\]<\/code>）/.test(rl),
      "图例第 1 组标题 =「主机地形（只填充、无框，来源 chunks[]）」");
    check(/客户端区域线框（当前内存区块）/.test(rl), "图例第 2 组标题 =「客户端区域线框（当前内存区块）」");
    check(!/验证框/.test(rl), "图例里不再出现「验证框」字样");
    check(/客户端线框 · 绿 = 已验证同步/.test(rl) && /客户端线框 · 蓝 = 同步了主机端数据/.test(rl) &&
      /客户端线框 · 黄 = 已上报、待裁决/.test(rl) && /客户端线框 · 红 = 验证失败/.test(rl),
      "图例列出客户端线框四色（绿 / 蓝 / 黄 / 红）与各自的 s");
    check(/客户端区块<b>只画线框、不填地形<\/b>/.test(rl), "图例写明「客户端区块只画线框、不填地形」");
    check(/客户端 1 个 · 线框 9 块（绿 6 \/ 蓝 1 \/ 黄 1 \/ 红 1） · 其中 1 块落在主机地形上/.test(rl),
      "图例给出客户端数 / 线框块数 / 四色计数 / 落在主机地形上的块数——实际：" +
      ((rl.match(/客户端 \d+ 个 · 线框[^<]*/) || ["无"])[0]));
    check(!/品红|外框/.test(rl), "图例不再出现品红 / 区域外框（旧解释已删除）");
    check(!/⚠/.test(rl), "这份 feed 没有缺失字段 → 图例不报警（实际：" + (rl.match(/⚠[^<]*/) || ["无"])[0] + "）");
    check(/当前视图/.test(rl) && /Host（主机 \+ 全部客户端）/.test(rl), "图例「当前视图」= Host（主机 + 全部客户端）");
    check(/本视图区块 2 个集合 · 共 18 区块/.test(rl),
      "图例写明本视图区块集合数（主机 1 + 客户端 1 = 2 个 · 共 18 区块去重后）——实际：" +
      ((rl.match(/本视图区块[^<]*/) || ["无"])[0]));
    check(/标记 全部角色（2 个）/.test(rl), "图例写明主机视图画全部角色的标记（2 个）");

    const sel = els.get("centerSel").innerHTML;
    check(/Host（房主） · \(-75,140\) · chunk -5,8/.test(sel), "居中角色下拉框含主机条目（坐标 + 区块）");
    check(/Android User · \(-207,217\) · chunk -13,13/.test(sel), "居中角色下拉框含客户端条目（坐标 + 区块）");

    const sb = els.get("statBody").innerHTML;
    check(/Host（键 -1）（房主）：\(-75,70,140\) · chunk\(-5,8\)/.test(sb), "统计面板列出主机绝对坐标与区块");
    check(/Android User（键 1234）：\(-207,70,217\) · chunk\(-13,13\)/.test(sb), "统计面板列出客户端绝对坐标与区块");
    check(/当前内存 9 块（绿 6 \/ 蓝 1 \/ 黄 1 \/ 红 1）· 会话累计上报 9/.test(sb),
      "统计面板列出该客户端**当前内存**区块数与四色计数（reported 作为会话累计参考值）");
    check(/payloadVersion \/ 解包失败<\/td><td class='mono'>9 \/ 0/.test(sb), "统计面板显示 payloadVersion=9 / 解包失败 0");
    check(/客户端线框（clients\[\]\.chunks）<\/td><td class='mono'>线框 9 块（绿 6 \/ 蓝 1 \/ 黄 1 \/ 红 1）/.test(sb),
      "统计面板「客户端线框」一行给出四色计数");
    check(/地形填充来源<\/td><td class='mono'>只有主机 <code>chunks\[\]<\/code>/.test(sb), "统计面板写明地形填充只来自主机 chunks[]");
    check(/当前视图（由居中角色决定）<\/td><td class='mono'>Host（主机 \+ 全部客户端）/.test(sb),
      "统计面板「当前视图」一行 = Host（主机 + 全部客户端）");
    check(/正在画的区块集合<\/td><td class='mono'>2 个 · 共 18 区块/.test(sb), "统计面板写明集合数 2 个 · 共 18 区块");
    check(/地形 = 主机 <code>chunks\[\]<\/code>/.test(sb), "统计面板写明地形 = 主机 chunks[]（仅主机加载的区域）");
    check(/主机地形 \/ 客户端线框<\/td><td class='mono'>主机地形覆盖 \d+ \/ 10 块（<b>无任何方框<\/b>）· 客户端线框 9 圈（绿 6 \/ 蓝 1 \/ 黄 1 \/ 红 1）/.test(sb),
      "统计面板新增「主机地形 / 客户端线框」行：主机地形覆盖 N 块（无任何方框）· 客户端线框 N 圈四色明细 —— 实际：" +
      ((sb.match(/主机地形 \/ 客户端线框[^]{0,160}/) || ["无"])[0]));
    check(!/主机验证框/.test(sb), "统计面板不再有「主机验证框」计数行");

    // 主机侧**不画任何框**（用户要求：主机端不需要任何方框，主机只保留地形填充）
    check(ctx.ops.filter((o) => o.op === "stroke" && o.dash.length > 0 && o.rects.length).length === 0,
      "主机侧没有任何虚线框（chunks[].s 不再渲染；主机区域边界也已删除）");
    check(fills(ctx, "#e0a800").length === 0 && fills(ctx, "#17a273").length === 0 &&
      fills(ctx, "#d1495f").length === 0 && fills(ctx, "#2f6fd0").length === 0,
      "主机侧没有任何同色填充（旧验证框的淡底已删除）");
    check(!els.has("verifyLegend") && !els.has("ckVerify"),
      "页面上不再有验证框图例容器 / 「显示验证框」勾选框");

    // 切到客户端居中：只画该客户端的 9 圈线框 + 落在它集合内的主机地形；不画主机标记与其它区块
    ctx.ops.length = 0;
    els.get("centerSel").fire("change", { target: { value: "1234" } });
    const t2 = texts(ctx);
    const c2 = markerCentre(ctx);
    const sizeA2 = frameCssOf(els) || [canvas.width, canvas.height];
    check(crossIsCentred(c2, canvas, els),
      "切到客户端后它的标记精确居中（标记 " + (c2 && c2[0].toFixed(2)) + "," + (c2 && c2[1].toFixed(2)) +
      " = 画布中心 " + (sizeA2[0] / 2) + "," + (sizeA2[1] / 2) + " CSS px）");
    check(t2.includes("Android User (-207,217) chunk(-13,13)"), "切到客户端后它的标签仍在（坐标 + 区块）");
    check(!t2.includes("Host (-75,140) chunk(-5,8)"),
      "切到客户端后**主机标记不画**（只显示该客户端自己的地形与角色）——实际标记：" +
      JSON.stringify(t2.filter((x) => /chunk\(/.test(x) && !/^x|^z/.test(x))));
    check(!t2.some((x) => x.startsWith("Host ")), "切到客户端后没有任何主机标记标签");

    const wg2 = wireRects(ctx, "#17a273"), wb2 = wireRects(ctx, "#2f6fd0"), wy2 = wireRects(ctx, "#e0a800"), wr2 = wireRects(ctx, "#d1495f");
    check(wg2.length === 6 && wb2.length === 1 && wy2.length === 1 && wr2.length === 1,
      "切到客户端后只画它自己的 9 圈线框（绿 6 / 蓝 1 / 黄 1 / 红 1，其它客户端一块都不画）");
    const terrain2 = ctx.ops.filter((o) => o.op === "fill" && o.rects.length === 256);
    check(terrain2.length === 1, "客户端视图的地形只来自**落在它区块集合内**的 1 块主机区块 (-12,13)（256 列）");
    const mapC = worldToCss(els, CLIENT_CELL, 12.4);
    check(!!wireRectOf(ctx, "#2f6fd0", mapC, -12, 13, 0), "该重叠区块的主机地形上仍叠着它自己的蓝色线框");
    const rl2 = els.get("regionLegend").innerHTML;
    check(/Android User（仅该客户端）/.test(rl2), "图例「当前视图」= Android User（仅该客户端）");
    check(/只画它自己的 9 个区块线框/.test(rl2), "图例说明客户端视图只画它自己的 9 个区块线框（其它客户端与别处主机地形都不画）");
    const sb2 = els.get("statBody").innerHTML;
    check(/当前视图（由居中角色决定）<\/td><td class='mono'>Android User（仅该客户端）/.test(sb2),
      "统计面板「当前视图」一行 = Android User（仅该客户端）");
    check(/只有该客户端自己：Android User（键 1234）/.test(sb2), "统计面板写明只画该客户端自己的标记");
    check(/地形只取落在它区块集合内的主机 <code>chunks\[\]<\/code>/.test(sb2),
      "统计面板写明客户端视图的地形仍只取落在它区块集合内的主机 chunks[]");

    // 「整片区域」按钮（放在最后：前面的线框几何断言都基于默认 12.4 px/格）
    const before = Number(els.get("scaleChip").textContent.split(" ")[0]);
    els.get("btnFitAll").fire("click");
    const after = Number(els.get("scaleChip").textContent.split(" ")[0]);
    check(after > 0 && after < before, "「整片区域」把比例调到能装下整片区域（" + before + " → " + after + " px/格）");
  }

  section("B. 旧参数 ?verify：仍接受但已忽略（主机侧本来就不画任何框）");
  {
    const a = await runPage(baseFeed(), "");
    const b = await runPage(baseFeed(), "?verify=0");
    const sig = (c) => c.ops.filter((o) => o.op === "stroke").map((o) => o.style + "|" + o.lw + "|" + (o.dash.length ? "dash" : "solid") + "|" + o.rects.length).join(";");
    check(sig(a.ctx) === sig(b.ctx), "?verify=0 与不传参数的渲染完全一致（旧参数被忽略、不报错）");
    check(wireRectsAll(a.ctx).length === 9 && wireRectsAll(b.ctx).length === 9,
      "不传参数 / ?verify=0 时客户端 9 圈区域线框都照常画");
    [a, b].forEach((r, i) => {
      check(r.ctx.ops.filter((o) => o.op === "stroke" && o.dash.length > 0 && o.rects.length).length === 0,
        (i ? "?verify=0" : "默认") + "：主机侧没有任何虚线框");
    });
    check(!b.els.has("verifyLegend") && !b.els.has("ckVerify"),
      "验证框图例容器与「显示验证框」勾选框都已从 UI 删除");
  }

  section("C. 旧版 feed（没有 players / clients 字段）必须明说，不画错区域");
  {
    const feed = baseFeed();
    delete feed.players;
    delete feed.clients;
    delete feed.payloadVersion;
    const { ctx, els } = await runPage(feed, "");
    const rl = els.get("regionLegend").innerHTML;
    const t = texts(ctx);
    check(/没有 <code>players<\/code> 字段/.test(rl), "图例明说 feed 没有 players 字段");
    check(/没有 <code>clients<\/code> 字段/.test(rl), "图例明说 feed 没有 clients 字段（画不出客户端线框）");
    check(wireRectsAll(ctx).length === 0, "没有 clients 字段时不画任何客户端线框（不猜）");
    check(t.includes("Host (-75,140) chunk(-5,8)"), "旧版 feed 下主机坐标仍来自 center.cell（不是区块中心）");
    check(t.includes("Android User 坐标未知 chunk(-13,13)"), "旧版 feed 下客户端标签明确写「坐标未知」（实际：" +
      t.filter((x) => x.startsWith("Android User"))[0] + "）");
    check(/坐标未知/.test(els.get("statBody").innerHTML), "统计面板也写明坐标未知");
  }

  section("D. 客户端区域数据不完整（complete=false）→ 不画 + 说明条数");
  {
    const feed = baseFeed();
    feed.clients[0].merged = 8;
    feed.clients[0].complete = false;
    feed.counts.clientsComplete = false;
    feed.counts.clientsIncompleteReason = "玩家端 1234（Android User）的已加载区块只合并到 8/9 条";
    const { ctx, els } = await runPage(feed, "");
    check(wireRectsAll(ctx).length === 0, "不完整 → 一块线框都不画（绝不画错的区域）");
    const rl = els.get("regionLegend").innerHTML;
    check(/不完整/.test(rl) && /Android User（键 1234，当前内存 9 条 \/ 已合并 8 条）/.test(rl),
      "图例写明是哪个客户端、当前内存几条 / 合并了几条");
    check(/只合并到 8\/9 条/.test(rl), "图例转述工具自己的不完整原因");
    check(/不完整：已合并 8\/9 → 不画/.test(els.get("statBody").innerHTML), "统计面板同样写明不完整（按 currentLoaded 判定）");
  }

  section("E. 客户端坐标读不到（cell=null + cellReason）→ 标记退回区块中心并写明原因");
  {
    const feed = baseFeed();
    feed.players[1].cell = null;
    feed.players[1].cellReason = "clientId=1234 的联机角色还没有 ComponentBody（位置读不到）";
    feed.center = { cell: null, cellReason: feed.players[1].cellReason, chunk: [-13, 13] };
    const { ctx, els } = await runPage(feed, "?center=1234");
    const t = texts(ctx);
    check(t.includes("Android User 坐标未知 chunk(-13,13)"), "标签写「坐标未知」而不是假坐标（实际：" +
      t.filter((x) => x.startsWith("Android User"))[0] + "）");
    check(/还没有 ComponentBody/.test(els.get("statBody").innerHTML), "统计面板给出读不到坐标的具体原因");
    check(/cell<\/code> 为 null/.test(els.get("regionLegend").innerHTML), "图例提示有角色的 cell 为 null");
    const c = markerCentre(ctx);
    check(!!c && Math.abs(c[0] - ctx.ops[0].x) >= 0, "坐标未知时仍能居中（标记落在区块中心）");
    check(wireRectsAll(ctx).length === 9, "客户端线框照常画（区域与坐标是两件事，9 圈都在）");
  }

  section("F. 版本不匹配必须报警（payloadVersion=3 / 6，decodeFailures>0）");
  {
    const feed = baseFeed();
    feed.payloadVersion = 3;
    feed.decodeFailures = 7;
    feed.decodeFailureReason = "MapState 载荷解包失败：工具期望版本 v9";
    const { els } = await runPage(feed, "");
    const rl = els.get("regionLegend").innerHTML;
    check(/payloadVersion<\/code> = 3/.test(rl), "图例明说 feed 的 payloadVersion 不是 9");
    check(/本页面按 v9 读字段/.test(rl), "图例写明页面按 v9 读字段");
    check(/7 次载荷解包失败/.test(rl), "图例转述解包失败次数与原因");

    // 旧版 v6 工具（只有 [cx,cz]、没有状态）同样必须显式报警，而不是静默当 0 画
    const feed6 = baseFeed();
    feed6.payloadVersion = 6;
    feed6.clients[0].chunks = CLIENT_CHUNKS.map(([cx, cz]) => [cx, cz]);
    const r6 = await runPage(feed6, "");
    const rl6 = r6.els.get("regionLegend").innerHTML;
    // v8（上一版）也必须被 v9 页面明确拒绝（版本字节是唯一闸门，不猜字段）
    const feed8 = baseFeed();
    feed8.payloadVersion = 8;
    const r8 = await runPage(feed8, "");
    const rl8 = r8.els.get("regionLegend").innerHTML;
    check(/payloadVersion<\/code> = 8/.test(rl8) && /本页面按 v9 读字段/.test(rl8),
      "v8 feed（上一版，无 animals）在图例里明确报警：页面按 v9 读字段");
    check(/payloadVersion<\/code> = 6/.test(rl6) && /本页面按 v9 读字段/.test(rl6),
      "v6 feed 在图例里明确报警（版本字节级不兼容，不猜字段）");
    check(/9 个 <code>clients\[\]\.chunks<\/code> 条目只有 <code>\[cx,cz\]<\/code>/.test(rl6) &&
      /不画线框/.test(rl6), "v6 的 [cx,cz] 条目 → 图例明说 9 块不画线框（绝不猜状态）");
    check(wireRectsAll(r6.ctx).length === 0, "v6 feed 里没有任何线框被画出来（不猜状态）");
  }

  section("G. window 视图（远处角色在画布外）");
  {
    const feed = baseFeed();
    feed.view = "window";
    feed.radius = 2;
    const { ctx } = await runPage(feed, "");
    const t = texts(ctx);
    check(t.some((x) => /Android User .*chunk\(-13,13\) ·画布外/.test(x)), "画布外的角色贴边显示并标注「画布外」（实际：" +
      (t.filter((x) => x.startsWith("Android User"))[0] || "无") + "）");
    check(t.includes("Host (-75,140) chunk(-5,8)"), "窗口模式仍有主机标记与坐标");
  }

  section("H. 视图作用域（两个客户端）：主机视图 = 主机地形 + 全部客户端线框；客户端视图 = 只有它自己的线框");
  {
    // ---- (a) 选中主机：地形只有主机 10 块；线框 = 客户端 1234 的 9 圈 + 客户端 4321 的 11 圈；全部标记 ----
    const feed = twoClientFeed();
    const { ctx, els, canvas } = await runPage(feed, "?center=-1");
    const t = texts(ctx);
    check(t.includes("Host (-75,140) chunk(-5,8)") &&
      t.includes("Android User (-207,217) chunk(-13,13)") &&
      t.includes("Tablet User (258,258) chunk(16,16)"),
      "主机视图画出全部 3 个角色的标记（Host / Android User / Tablet User）——实际：" +
      JSON.stringify(t.filter((x) => /chunk\(-?\d/.test(x))));

    const terrain = ctx.ops.filter((o) => o.op === "fill" && o.rects.length > 100);
    check(terrain.length === 1 && terrain[0].rects.length === 10 * 256,
      "主机视图的地形仍然只有主机自己的 10 个区块（" + (terrain.length ? terrain[0].rects.length : 0) + " 格）");
    const wg = wireRects(ctx, "#17a273"), wb = wireRects(ctx, "#2f6fd0"), wy = wireRects(ctx, "#e0a800"), wr = wireRects(ctx, "#d1495f");
    check(wg.length === 12 && wb.length === 3 && wy.length === 3 && wr.length === 2,
      "两个客户端的 20 个区块全部画成线框：绿 12 / 蓝 3 / 黄 3 / 红 2（实际 " +
      wg.length + "/" + wb.length + "/" + wy.length + "/" + wr.length + "）");
    check(wg.length + wb.length + wy.length + wr.length === 20,
      "每块一圈线框（9 + 11 = 20 圈，客户端区块绝不参与地形填充）");
    // 强化：圈数 == 全部完整客户端里 s∈1..4 的条目数（一个不落、一个不多）
    const stateEntriesH = feed.clients.reduce((n, c) =>
      n + (c.complete === false ? 0 : c.chunks.filter((e) => e[2] >= 1 && e[2] <= 4).length), 0);
    check(stateEntriesH === 20 && wireRectsAll(ctx).length === stateEntriesH,
      "线框圈数 == 全部完整客户端里 s∈1..4 的条目数（" + stateEntriesH + " 条 = " + stateEntriesH +      " 圈）——实际 " + wireRectsAll(ctx).length + " 圈");

    // 多客户端覆盖同一块 (-13,12)：几何必须**完全相同**（都贴区块边界），只用线宽区分
    const mapHost = worldToCss(els, HOST_CELL, 12.4);
    const sharedA = wireRectOf(ctx, "#17a273", mapHost, -13, 12, 0);
    const sharedB = wireRectOf(ctx, "#e0a800", mapHost, -13, 12, 0);
    check(!!sharedA && !!sharedB && Math.abs(sharedA[0] - sharedB[0]) < 0.01 && Math.abs(sharedA[1] - sharedB[1]) < 0.01 &&
      Math.abs(sharedA[2] - sharedB[2]) < 0.01 && Math.abs(sharedA[3] - sharedB[3]) < 0.01,
      "同一区块被两个客户端覆盖 → 两个线框**几何完全相同**（都贴区块边界，不偏移）");
    const lwAt = (c, color, map, cx, cz) => {
      const rr = wireRectOf(c, color, map, cx, cz, 0);
      if (!rr) return 0;
      const op = c.ops.find((o) => o.op === "stroke" && o.style === color &&
        o.rects.some((x) => Math.abs(x[0] - rr[0]) < 0.01 && Math.abs(x[1] - rr[1]) < 0.01 && Math.abs(x[2] - rr[2]) < 0.01));
      return op ? op.lw : 0;
    };
    const lwB = lwAt(ctx, "#e0a800", mapHost, -13, 12), lwA = lwAt(ctx, "#17a273", mapHost, -13, 12);
    check(lwB > lwA && lwA > 0, "两个客户端只用**线宽**区分：第 2 个更宽（" + lwB.toFixed(2) + " > " + lwA.toFixed(2) + " px）");
    check(/几何都画在同一条区块边界上/.test(els.get("regionLegend").innerHTML),
      "图例写明多客户端规则：几何都贴同一条区块边界（不内缩），只用线宽分层");

    const rl = els.get("regionLegend").innerHTML;
    check(/本视图区块 3 个集合 · 共 27 区块/.test(rl),
      "主机视图的区块集合 = 主机 + 2 个客户端 = 3 个 · 共 27 区块（10+9+11 去重 3 块重叠）——实际：" +
      ((rl.match(/本视图区块[^<]*/) || ["无"])[0]));
    check(/客户端 2 个 · 线框 20 块（绿 12 \/ 蓝 3 \/ 黄 3 \/ 红 2） · 其中 2 块落在主机地形上/.test(rl),
      "图例给出线框四色计数与「落在主机地形上」的块数——实际：" +
      ((rl.match(/客户端 \d+ 个 · 线框[^<]*/) || ["无"])[0]));
    check(/标记 全部角色（3 个）/.test(rl), "图例写明主机视图画全部 3 个角色的标记");
    const sb = els.get("statBody").innerHTML;
    check(/正在画的区块集合<\/td><td class='mono'>3 个 · 共 27 区块/.test(sb), "统计面板写明集合数 3 个 · 共 27 区块");
    check(/Android User（键 1234）[\s\S]*Tablet User（键 4321）/.test(sb), "统计面板列出两个客户端");
    check(/客户端线框（clients\[\]\.chunks）<\/td><td class='mono'>线框 20 块（绿 12 \/ 蓝 3 \/ 黄 3 \/ 红 2）/.test(sb),
      "统计面板「客户端线框」一行 = 20 块四色计数");

    // 并集把最右的客户端（chunk 17）拉进画布
    const dprA = dprUsedOf(els);
    const zMinA = Math.min(112, 7 * 16, 12 * 16, 15 * 16), zMaxA = 287;
    const halfXA = Math.max(Math.abs(HOST_CELL[0] - (-14 * 16)), Math.abs(HOST_CELL[0] - (17 * 16 + 15)));
    const halfZA = Math.max(Math.abs(HOST_CELL[2] - zMinA), Math.abs(HOST_CELL[2] - zMaxA));
    const cssWA = Math.ceil((2 * halfXA + 1) * 12.4 - 1e-9), cssHA = Math.ceil((2 * halfZA + 1) * 12.4 - 1e-9);
    check(Math.abs(canvas.width - cssWA * dprA) <= 1 && Math.abs(canvas.height - cssHA * dprA) <= 1,
      "主机视图的画布 = 并集跨度（含客户端 4321 的 chunk 17）：canvas " + canvas.width + "×" + canvas.height +
      " px = 期望 " + (cssWA * dprA).toFixed(1) + "×" + (cssHA * dprA).toFixed(1) +
      "（CSS " + cssWA + "×" + cssHA + " × dprUsed " + dprA + "）");
    check(crossIsCentred(markerCentre(ctx), canvas, els),
      "主机在并集画布上仍精确居中（十字 x=" + markerCentre(ctx)[0] + " = CSS 画布中心 " + ((frameCssOf(els) || [0])[0] / 2) + "）");
    check(feed.chunks.every((c) => !chunkOffCanvas(mapHost, c.x, c.z, 12.4)),
      "主机视图的画布装下主机全部 10 个区块（无一块被剔出画布）");

    // ---- (b) 选中客户端 1234：只画它自己的 9 圈线框、只画它自己的标记 ----
    ctx.ops.length = 0;
    els.get("centerSel").fire("change", { target: { value: "1234" } });
    const t2 = texts(ctx);
    const c1234 = markerCentre(ctx);
    const sizeB = frameCssOf(els) || [canvas.width, canvas.height];
    check(crossIsCentred(c1234, canvas, els),
      "客户端视图：画布精确居中在客户端 1234（标记 " + (c1234 && c1234[0].toFixed(2)) + "," + (c1234 && c1234[1].toFixed(2)) +
      " = 画布中心 " + (sizeB[0] / 2) + "," + (sizeB[1] / 2) + " CSS px，画布 " + sizeB[0] + "×" + sizeB[1] + "）");
    check(t2.includes("Android User (-207,217) chunk(-13,13)"), "客户端视图画出它自己的标记标签");
    check(!t2.some((x) => x.startsWith("Host ")) && !t2.some((x) => x.startsWith("Tablet User ")),
      "客户端视图**不画**主机与其它客户端的标记——实际：" + JSON.stringify(t2.filter((x) => /chunk\(/.test(x))));
    const wgB = wireRects(ctx, "#17a273"), wbB = wireRects(ctx, "#2f6fd0"), wyB = wireRects(ctx, "#e0a800"), wrB = wireRects(ctx, "#d1495f");
    check(wgB.length === 6 && wbB.length === 1 && wyB.length === 1 && wrB.length === 1,
      "客户端 1234 视图只画它自己的 9 圈线框（绿 6 / 蓝 1 / 黄 1 / 红 1）");
    check(wireRects(ctx, "#e0a800").length === 1 && !wireRectOf(ctx, "#e0a800", mapHost, -13, 12, 0),
      "客户端 1234 视图不再画客户端 4321 的那圈黄色线框（重叠块只留自己的一圈）");
    const map = worldToCss(els, CLIENT_CELL, 12.4);
    const decInScope = feed.chunks.filter((c) => CLIENT_CHUNKS.some(([cx, cz]) => cx === c.x && cz === c.z) &&
      !chunkOffCanvas(map, c.x, c.z, 12.4));

    const terrain2 = ctx.ops.filter((o) => o.op === "fill" && o.rects.length > decInScope.length * 256);
    check(terrain2.length === 0,
      "客户端视图的地形只来自作用域内的 " + decInScope.length + " 块主机区块（不是主机全部 " + feed.chunks.length +
      " 块）：实际还有 " + terrain2.length + " 个更大的地形填充");
    check(decInScope.length === 1 && decInScope[0].x === -12 && decInScope[0].z === 13 &&
      ctx.ops.filter((o) => o.op === "fill" && o.rects.length === 256).length === 1,
      "客户端视图只画出与客户端区块重合的那一块主机地形 (-12,13)（256 列）");
    check(ctx.ops.filter((o) => o.op === "stroke" && o.dash.length > 0 && o.rects.length).length === 0,
      "客户端视图同样没有任何主机侧框（主机地形只填充）");
    const rl2 = els.get("regionLegend").innerHTML;
    check(/Android User（仅该客户端）/.test(rl2) && /本视图区块 1 个集合 · 共 9 区块/.test(rl2),
      "图例「当前视图」= Android User（仅该客户端）· 1 个集合 · 共 9 区块");
    check(/客户端 1 个 · 线框 9 块（绿 6 \/ 蓝 1 \/ 黄 1 \/ 红 1）/.test(rl2), "图例写明只画到 1 个客户端 / 9 圈线框");
    const sb2 = els.get("statBody").innerHTML;
    check(/当前视图（由居中角色决定）<\/td><td class='mono'>Android User（仅该客户端）/.test(sb2),
      "统计面板「当前视图」= Android User（仅该客户端）");
    check(/本视图画的标记<\/td><td class='mono'>只有该客户端自己：Android User（键 1234）/.test(sb2), "统计面板写明只画该客户端的标记");

    // ---- (c) 再切到客户端 4321：只画它自己的 11 圈线框，地形只取 (-5,8) ----
    ctx.ops.length = 0;
    els.get("centerSel").fire("change", { target: { value: "4321" } });
    const t3 = texts(ctx);
    const c4321 = markerCentre(ctx);
    check(t3.includes("Tablet User (258,258) chunk(16,16)"), "切到客户端 4321 后画它自己的标记标签");
    check(!t3.some((x) => x.startsWith("Host ")) && !t3.some((x) => x.startsWith("Android User ")),
      "客户端 4321 视图不画主机与客户端 1234 的标记——实际：" + JSON.stringify(t3.filter((x) => /chunk\(/.test(x))));
    const sizeC = frameCssOf(els) || [canvas.width, canvas.height];
    check(crossIsCentred(c4321, canvas, els),
      "画布精确居中在客户端 4321（标记 " + (c4321 && c4321[0].toFixed(2)) + "," + (c4321 && c4321[1].toFixed(2)) +
      " = 画布中心 " + (sizeC[0] / 2) + "," + (sizeC[1] / 2) + " CSS px，画布 " + sizeC[0] + "×" + sizeC[1] + "）");
    const wgC = wireRects(ctx, "#17a273"), wbC = wireRects(ctx, "#2f6fd0"), wyC = wireRects(ctx, "#e0a800"), wrC = wireRects(ctx, "#d1495f");
    check(wgC.length === 6 && wbC.length === 2 && wyC.length === 2 && wrC.length === 1,
      "客户端 4321 视图只画它自己的 11 圈线框（绿 6 / 蓝 2 / 黄 2 / 红 1）");
    check(ctx.ops.filter((o) => o.op === "fill" && o.rects.length === 256).length === 1,
      "客户端 4321 视图的地形只来自主机区块 (-5,8)（256 列）");
    check(/Tablet User（仅该客户端）/.test(els.get("regionLegend").innerHTML), "图例切到 Tablet User（仅该客户端）");
    check(/本视图区块 1 个集合 · 共 11 区块/.test(els.get("regionLegend").innerHTML),
      "图例写明客户端 4321 视图 1 个集合 · 共 11 区块");

    // ---- (d) 客户端 complete=false：主机视图的线框集合必须把它排除 ----
    const feed2 = twoClientFeed();
    feed2.clients[1].merged = 4;
    feed2.clients[1].complete = false;
    const r2 = await runPage(feed2, "");
    const rl3 = r2.els.get("regionLegend").innerHTML;
    check(/本视图区块 2 个集合 · 共 18 区块/.test(rl3),
      "不完整的客户端不进主机视图的线框集合（集合 2 个 · 共 18 区块）——实际：" +
      ((rl3.match(/本视图区块[^<]*/) || ["无"])[0]));
    check(/客户端 1 个 · 线框 9 块/.test(rl3), "不完整的客户端一块线框都不画（只剩 1 个客户端 / 9 圈）");
    check(wireRectsAll(r2.ctx).length === 9, "不完整的客户端 11 块全部不画（0 圈），完整客户端 9 圈照画");
    check(r2.ctx.ops.filter((o) => o.op === "fill" && o.rects.length > 100).length === 1,
      "不完整的客户端不影响主机地形照常画");
  }

  section("I. 格内数字（chunks[].n）：n>0 才画，0 / 缺失 / 未知一律不画");
  {
    // ---- (a) 两块 n>0（1 与 2）、一块 n=0、其余 n=0 → 只画 2 个数字，且精确落在区块正中 ----
    const feed = baseFeed();
    const IDX_A = 2;                              // (-6,9)
    const IDX_B = 9;                              // (-12,13)：与客户端 1234 的区域重叠的那一块
    feed.chunks[IDX_A].n = 1;
    feed.chunks[IDX_B].n = 2;
    feed.counts.pendingMarks = 2;
    feed.counts.pendingClients = 3;
    const { ctx, els } = await runPage(feed, "");
    const digits = digitOps(ctx);
    check(digits.length === 2, "只有 n>0 的 2 块画了数字（n=0 的区块一个都不画）——实际 " + digits.length + "：" +
      JSON.stringify(digits.map((d) => d.text)));
    check(digits.map((d) => d.text).sort().join(",") === "1,2", "数字文本 = 各块自己的 n（1 与 2）");
    const map = worldToCss(els, HOST_CELL, 12.4);
    const side = 16 * 12.4;
    const expected = [IDX_A, IDX_B].map((i) => {
      const c = feed.chunks[i];
      return [round3(map.px(c.x * 16) + side / 2), round3(map.pz(c.z * 16) + side / 2)];
    });
    const actual = digits.map((d) => [round3(d.x), round3(d.y)]);
    check(expected.every((e) => actual.some((a) => Math.abs(a[0] - e[0]) < 0.01 && Math.abs(a[1] - e[1]) < 0.01)),
      "每个数字都精确画在对应区块 16×16 格的正中（区块→像素与地形同一套映射）：期望 " +
      JSON.stringify(expected) + " / 实际 " + JSON.stringify(actual));
    check(digits.every((d) => d.style === "#fff3c4"), "数字用亮色填充（#fff3c4）");
    const dStrokes = digitStrokeOps(ctx);
    check(dStrokes.length === 2 && dStrokes.every((o) => /rgba\(24,18,8/.test(o.style) && o.lw >= 2),
      "数字有深色描边 halo（rgba(24,18,8,…)，lineWidth ≥ 2 px）——实际 " + dStrokes.length + " 条");
    check(digits.every((d) => dStrokes.some((s) => s.text === d.text && s.x === d.x && s.y === d.y)),
      "描边与填充同一坐标（同一个数字不会错位成两层）");
    // 主机侧不画任何框：画数字的前后都不应该出现任何主机框
    check(ctx.ops.filter((o) => o.op === "stroke" && o.dash.length > 0 && o.rects.length).length === 0,
      "画数字时主机侧同样没有任何框（唯一框层是客户端线框）");

    const pl = els.get("pendingLegend").innerHTML;
    check(/欠更新数字/.test(pl) && /格内数字 = 该区块还有几个/.test(pl),
      "数字图例解释含义（该区块还有几个在线客户端没拿到最新内容）");
    check(/0 \/ 缺失不画数字/.test(pl), "数字图例写明 0 / 缺失不画数字");
    check(/n&gt;0 的区块 2 块 · 欠更新客户端合计 3 · 本视图画出 2 个数字/.test(pl),
      "数字图例给出标块数 / 客户端合计 / 本视图画出的数字数——实际：" +
      ((pl.match(/n&gt;0 的区块[^<]*/) || ["无"])[0]));
    check(!/⚠/.test(pl), "v5 feed 带 chunks[].n → 数字图例不报警（实际：" + ((pl.match(/⚠[^<]*/) || ["无"])[0]) + "）");
    const sb = els.get("statBody").innerHTML;
    check(/欠更新数字（chunks\[\]\.n）<\/td><td class='mono'>n&gt;0 的区块 2 块 · 欠更新客户端合计 3 · 本视图画出 2 个数字/.test(sb),
      "统计面板同样给出数字统计");
    check(/pendingMarks 2 · pendingClients 3/.test(sb), "统计面板转述工具 counts.pendingMarks / pendingClients");

    // ---- (b) 全是 n=0（全部客户端都已核对到最新）→ 一个数字都不画 ----
    const feedZero = baseFeed();
    feedZero.chunks.forEach((c) => { c.n = 0; });
    const rZero = await runPage(feedZero, "");
    check(digitOps(rZero.ctx).length === 0, "全部 n=0 → 不画任何数字");
    check(/n&gt;0 的区块 0 块 · 欠更新客户端合计 0/.test(rZero.els.get("pendingLegend").innerHTML),
      "图例如实写 0 块标注");

    // ---- (c) 旧版 feed：chunks 里根本没有 n 字段 → 不画 + 图例明说 ----
    const feedOld = baseFeed();
    feedOld.chunks.forEach((c) => { delete c.n; });
    delete feedOld.counts.pendingMarks;
    delete feedOld.counts.pendingClients;
    const rOld = await runPage(feedOld, "");
    check(digitOps(rOld.ctx).length === 0, "缺 chunks[].n 字段（旧版主机/工具）→ 一个数字都不画（绝不猜）");
    const plOld = rOld.els.get("pendingLegend").innerHTML;
    check(/没有 <code>chunks\[\]\.n<\/code> 字段/.test(plOld), "数字图例明说 feed 没有 chunks[].n 字段");
    check(/一个数字都不画/.test(plOld), "数字图例写明因此不画数字");
    check(/⚠/.test(plOld), "数字图例给出 ⚠ 报警");
    check(/feed 没有 <code>chunks\[\]\.n<\/code> 字段（旧版主机\/工具）→ 不画数字/.test(rOld.els.get("statBody").innerHTML),
      "统计面板同步写明「不画数字」");

    // ---- (d) 非法 n（负数 / 非数字）→ 同样不画，并在图例里计数 ----
    const feedBad = baseFeed();
    feedBad.chunks[1].n = -3;
    feedBad.chunks[2].n = "abc";
    const rBad = await runPage(feedBad, "");
    check(digitOps(rBad.ctx).length === 0, "非法 n（负数 / 非数字）→ 不画数字");
    check(/2 个区块的 <code>n<\/code> 不是 0 \/ 正整数/.test(rBad.els.get("pendingLegend").innerHTML),
      "数字图例如实报出非法 n 的块数");

    // ---- (e) 视图作用域：数字只画在本视图作用域内的区块上（与地形 / 验证框同源） ----
    // 注意：切视图要用工具栏下拉框（现有测试用例的做法）；?center= 只在第一个样本生效一次，
    // 而 draw() 自己也会调一次 pickCenter，所以 ?center= 不能用来断言"当前画的是哪个视图"。
    const feedHostOnly = baseFeed();
    feedHostOnly.chunks[4].n = 3;                 // (-5,8)：主机自己的区块，不在客户端 1234 的 9 块里
    const rHostOnly = await runPage(feedHostOnly, "");
    check(digitOps(rHostOnly.ctx).length === 1, "主机视图画 n>0 的数字（1 个）");
    rHostOnly.ctx.ops.length = 0;
    rHostOnly.els.get("centerSel").fire("change", { target: { value: "1234" } });
    check(digitOps(rHostOnly.ctx).length === 0,
      "客户端视图**不画**不属于该客户端的区块上的数字（切到客户端不静默带出主机的数字）");
    const feedShared = baseFeed();
    feedShared.chunks[9].n = 2;                   // (-12,13)：也在客户端 1234 上报的区块里
    const rShared = await runPage(feedShared, "");
    rShared.ctx.ops.length = 0;
    rShared.els.get("centerSel").fire("change", { target: { value: "1234" } });
    const sharedDigits = digitOps(rShared.ctx);
    check(sharedDigits.length === 1 && sharedDigits[0].text === "2",
      "客户端视图里属于该客户端区块的数字照常画（1 个，文本 = 2）——实际 " +
      JSON.stringify(sharedDigits.map((d) => d.text)));

    // ---- (f) 欠账数字只落在主机格：n 永不画在「仅客户端」区块中心（客户端区块没有主机 chunks[]） ----
    const feedAllN = baseFeed();
    feedAllN.chunks.forEach((c) => { c.n = 7; });
    feedAllN.counts.pendingMarks = feedAllN.chunks.length;
    feedAllN.counts.pendingClients = feedAllN.chunks.length * 7;
    const rAllN = await runPage(feedAllN, "");
    const digitsAllN = digitOps(rAllN.ctx);
    check(digitsAllN.length === feedAllN.chunks.length,
      "n>0 的主机区块全部标注（" + feedAllN.chunks.length + " 个数字）——实际 " + digitsAllN.length);
    const mapAllN = worldToCss(rAllN.els, HOST_CELL, 12.4);
    const hostSetAllN = new Set(feedAllN.chunks.map((c) => c.x + "," + c.z));
    const clientOnlyAllN = CLIENT_CHUNKS.filter(([cx, cz]) => !hostSetAllN.has(cx + "," + cz));
    const centreOf = (cx, cz) => [round3(mapAllN.px(cx * 16) + (16 * 12.4) / 2), round3(mapAllN.pz(cz * 16) + (16 * 12.4) / 2)];
    const digitAt = (centre) => digitsAllN.find((d) => Math.abs(d.x - centre[0]) < 0.01 && Math.abs(d.y - centre[1]) < 0.01);
    const clientOnlyHit = clientOnlyAllN.filter(([cx, cz]) => !!digitAt(centreOf(cx, cz)));
    check(clientOnlyAllN.length > 0 && clientOnlyHit.length === 0,
      "欠账数字永不落在「仅客户端」区块中心（客户端区块不承载主机欠账计数）——仅客户端 " +
      clientOnlyAllN.length + " 块 / 命中 " + clientOnlyHit.length + " 块");
  }

  section("J. 数据变化自动「整片区域」：变化才拟合、未变不拟合、手动缩放 3 s 静默、按当前视图范围");
  {
    // ---- (a) bounds 变化 → 恰好一次自动拟合；首帧只记基线 ----
    const p = await runPage(baseFeed(), "");
    const scale0 = p.scale();
    check(p.fitCount() === 0, "首帧只记基线、不自动拟合（初始 12.4 px/格 / ?scale= 不被抢）");
    const f2 = baseFeed();
    f2.bounds = { chunkMin: [-14, 7], chunkMax: [-4, 15], cellMin: [-224, 112], cellMax: [-49, 255] };
    await p.reload(f2);
    check(p.fitCount() === 1, "(a) bounds 变化的一帧恰好触发 1 次自动拟合（实际 " + p.fitCount() + " 次）");
    const scaleA = p.scale();
    check(scaleA > 0 && scaleA < scale0, "(a) 自动拟合把比例缩小到能装下整片区域（" + scale0 + " → " + scaleA + " px/格）");

    // ---- (b) 同一份数据再来一帧 → 不拟合、比例不变 ----
    const fitB = p.fitCount();
    await p.reload(f2);
    check(p.fitCount() === fitB, "(b) 数据未变化的帧不自动拟合（0 次新拟合）");
    check(p.scale() === scaleA, "(b) 未变化帧的比例保持不变（无视觉抖动，仍 " + scaleA + " px/格）");

    // ---- (c) 手动缩放 → 静默期不抢；3 s 后下一次变化恢复自动拟合 ----
    const beforeZoom = p.scale();
    p.els.get("btnZoomIn").fire("click");
    const zoomed = p.scale();
    check(zoomed > beforeZoom, "手动「＋ 放大」仍生效（" + beforeZoom + " → " + zoomed + " px/格）");
    const f3 = baseFeed();
    f3.bounds = { chunkMin: [-14, 7], chunkMax: [-4, 16], cellMin: [-224, 112], cellMax: [-49, 271] };
    const fitC = p.fitCount();
    await p.reload(f3);                                  // 假时钟没走：仍在 3 s 静默期内
    check(p.fitCount() === fitC, "(c) 手动缩放后的 3 s 静默期内，数据变化不抢用户的缩放（0 次自动拟合）");
    check(p.scale() === zoomed, "(c) 静默期内比例保持用户手动缩放后的值（" + zoomed + " px/格）");
    const f4 = baseFeed();
    f4.bounds = { chunkMin: [-14, 7], chunkMax: [-4, 17], cellMin: [-224, 112], cellMax: [-49, 287] };
    await p.reload(f4, 3100);                            // 越过 3 s 静默期 + 新的数据变化
    check(p.fitCount() === fitC + 1, "(c) 静默期（3 s）结束后的下一次数据变化恢复自动拟合（+1 次）");
    check(p.scale() < zoomed, "(c) 恢复后比例回到能装下整片区域的值（" + zoomed + " → " + p.scale() + " px/格）");

    // ---- (d) 自动拟合按当前「居中角色」视图取范围 ----
    // 客户端 1234 自己的区域 = chunk x -14..-12 / z 12..14，角色格 (-207,217)
    //   → 半跨 30 / 25，画布 61 × 51 格；主机并集（含客户端 4321 的 chunk 17）= 725 × 295 格。
    const d = await runPage(twoClientFeed(), "");
    d.els.get("centerSel").fire("change", { target: { value: "1234" } });
    const dClient = twoClientFeed();
    dClient.bounds = null;                               // 客户端视图本来就不并入主机 bounds
    dClient.boundsSource = "none";
    const beforeClient = d.fitCount();
    await d.reload(dClient);
    const scaleClient = d.scale();
    const expectedClient = expectedFitScale(61, 51);
    check(d.fitCount() === beforeClient + 1, "(d) 切到客户端后数据变化触发 1 次自动拟合");
    check(Math.abs(scaleClient - expectedClient) < 0.001,
      "(d) 客户端视图自动拟合只按它自己的区域（61 × 51 格 → " + expectedClient +
      " px/格，实际 " + scaleClient + "）");
    check(scaleClient > 5, "(d) 客户端视图没有被主机 bounds / 其它客户端撑大（实际 " + scaleClient + " px/格）");

    d.els.get("centerSel").fire("change", { target: { value: "-1" } });
    const dHost = twoClientFeed();
    dHost.bounds = { chunkMin: [-14, 7], chunkMax: [-4, 14], cellMin: [-224, 112], cellMax: [-177, 239] };
    const beforeHost = d.fitCount();
    await d.reload(dHost, 300);                          // 越过 250 ms 节流窗口（否则要等尾随拟合）
    const scaleHost = d.scale();
    const expectedHost = expectedFitScale(725, 295);
    check(d.fitCount() === beforeHost + 1, "(d) 切回主机后数据变化触发 1 次自动拟合");
    check(Math.abs(scaleHost - expectedHost) < 0.001,
      "(d) 主机视图自动拟合按并集（主机 ∪ 全部客户端 → 725 × 295 格 → " + expectedHost +
      " px/格，实际 " + scaleHost + "）");
    check(scaleHost < 2 && scaleClient > 5,
      "(d) 主机并集与客户端自己的区域得到明显不同的拟合比例（" + scaleHost + " vs " + scaleClient + " px/格）");

    // ---- (e) 节流：250 ms 内连续变化只立即拟合一次，窗口结束时尾随补一次 ----
    const thr = await runPage(baseFeed(), "");
    const g1 = baseFeed();
    g1.bounds = { chunkMin: [-14, 7], chunkMax: [-4, 15], cellMin: [-224, 112], cellMax: [-49, 255] };
    await thr.reload(g1);
    const fitT = thr.fitCount();
    check(fitT === 1, "节流：第一次变化立即拟合（1 次）");
    const g2 = baseFeed();
    g2.bounds = { chunkMin: [-14, 7], chunkMax: [-4, 16], cellMin: [-224, 112], cellMax: [-49, 271] };
    await thr.reload(g2);                                // 同一假时刻：落在 250 ms 节流窗口内
    check(thr.fitCount() === fitT, "节流：250 ms 窗口内的第二次变化不立即拟合");
    thr.advance(250);                                    // 窗口结束 → 尾随拟合补上最后一次变化
    check(thr.fitCount() === fitT + 1, "节流：窗口结束后的尾随拟合补上最后一次变化（+1 次）");
    const g3 = baseFeed();
    g3.bounds = { chunkMin: [-14, 7], chunkMax: [-4, 17], cellMin: [-224, 112], cellMax: [-49, 287] };
    thr.advance(250);
    await thr.reload(g3);
    check(thr.fitCount() === fitT + 2, "节流：窗口过去后新的变化立即拟合（+1 次）");
  }

  section("K. 主机侧没有任何框：默认 / ?verify=0 / ?verify=1 都不画；客户端区域线框是唯一框层");
  {
    const cases = [{ q: "", label: "默认" }, { q: "?verify=0", label: "?verify=0" }, { q: "?verify=1", label: "?verify=1" }];
    for (const c of cases) {
      const r = await runPage(baseFeed(), c.q);
      const dashed = r.ctx.ops.filter((o) => o.op === "stroke" && o.dash.length > 0 && o.rects.length);
      check(dashed.length === 0, "（" + c.label + "）没有任何虚线描边（主机框 / 主机区域边界都已删除）—— 实际 " + dashed.length);
      check(r.ctx.ops.filter((o) => o.op === "fill" && SC_COLORS.indexOf(o.style) >= 0).length === 0,
        "（" + c.label + "）没有主机验证框的淡色填充");
      check(wireRectsAll(r.ctx).length === 9, "（" + c.label + "）客户端 9 圈区域线框照常画");
    }
    // 客户端线框：实线 + 一层深色底衬
    const { ctx, els } = await runPage(baseFeed(), "");
    const wires = solidColorStrokes(ctx).filter((o) => o.rects.length);
    check(wires.length > 0 && wires.every((o) => o.dash.length === 0), "客户端区域线框全部是实线");
    const halos = ctx.ops.filter((o) => o.op === "stroke" && isDarkHalo(o.style) && o.rects.length === 9);
    check(halos.length === 1, "客户端线框共用一层深色底衬（9 块一笔）—— 实际 " + halos.length);
    const rl = els.get("regionLegend").innerHTML;
    check(/主机地形（只填充、无框，来源 <code>chunks\[\]<\/code>）/.test(rl),
      "图例第 1 组标题 =「主机地形（只填充、无框，来源 chunks[]）」");
    check(/客户端区域线框（当前内存区块）/.test(rl), "图例第 2 组标题 =「客户端区域线框（当前内存区块）」");
    check(/粗实线 \+ 深色底衬/.test(rl) && /区块边界/.test(rl), "客户端线框说明：粗实线 + 深色底衬、画在区块边界");
    check(/几何都画在同一条区块边界上/.test(rl) && /第 i 个客户端线更宽/.test(rl),
      "保留多客户端重叠规则说明（几何贴同一条区块边界、只用线宽分层）");
    check(!/验证框/.test(rl), "图例里不再出现「验证框」字样");
  }

  section("L. 主机侧没有区域边界框：主机视图 / 客户端视图都不画任何主机侧框");
  {
    const host = await runPage(baseFeed(), "");
    host.ctx.ops.length = 0;
    host.els.get("centerSel").fire("change", { target: { value: "1234" } });
    const dashedClient = host.ctx.ops.filter((o) => o.op === "stroke" && o.dash.length > 0 && o.rects.length);
    check(dashedClient.length === 0, "客户端视图没有任何主机侧虚线框 / 区域边界 —— 实际 " + dashedClient.length);
    check(wireRectsAll(host.ctx).length === 9, "客户端视图仍只画它自己的 9 圈区域线框");
    const host2 = await runPage(baseFeed(), "");
    check(host2.ctx.ops.filter((o) => o.op === "stroke" && o.dash.length > 0 && o.rects.length).length === 0,
      "主机视图也没有任何主机侧框（原先的浅蓝区域边界已删除）");
  }

  section("M. 缺状态 / 非法状态的条目：逐块不画线框并明确计数（绝不猜一个状态）");
  {
    const feed = baseFeed();
    // 9 条里：3 条正常 [cx,cz,s]、3 条旧形状 [cx,cz]、3 条状态非法（9 / 2.5 / true）
    feed.clients[0].chunks = CLIENT_CHUNKS.map((e, i) => {
      if (i < 3) return [e[0], e[1], e[2]];
      if (i < 6) return [e[0], e[1]];
      return [e[0], e[1], i === 6 ? 9 : (i === 7 ? 2.5 : true)];
    });
    const { ctx, els } = await runPage(feed, "");
    check(wireRectsAll(ctx).length === 3, "只有 3 条带合法状态的条目画出线框（其余 6 条一块都不画）——实际 " +
      wireRectsAll(ctx).length + " 圈");
    check(wireRects(ctx, "#17a273").length === 3 && wireRects(ctx, "#2f6fd0").length === 0,
      "3 圈全是合法状态 2（绿），没有一条被猜成蓝 / 黄 / 红");
    const rl = els.get("regionLegend").innerHTML;
    check(/3 个 <code>clients\[\]\.chunks<\/code> 条目只有 <code>\[cx,cz\]<\/code>/.test(rl) &&
      /这些区块<b>不画线框<\/b>/.test(rl), "图例明说 3 条旧形状条目（[cx,cz]）不画线框");
    check(/3 个 <code>clients\[\]\.chunks<\/code> 条目的状态不认识（不是 0\.\.4）/.test(rl),
      "图例明说 3 条状态不认识的条目不画线框（并注明 0 是正常值）");
    const sb = els.get("statBody").innerHTML;
    check(/缺状态 3 块不画（旧版 feed，绝不猜）/.test(sb) && /状态不认识 3 块不画/.test(sb),
      "统计面板同样给出缺状态 / 状态不认识的块数");
    check(/线框 3 块（绿 3 \/ 蓝 0 \/ 黄 0 \/ 红 0）/.test(sb), "统计面板的线框计数只算真正画出来的 3 块");
  }

  section("N. 多客户端重叠：几何一律贴区块边界（不内缩）；只用线宽分层（最多 5 档）");
  {
    // 40 个客户端全部覆盖同一块 (-5,8)：几何必须完全相同，只在线宽上分层
    function manyClientsFeed(count) {
      const f = baseFeed();
      f.players = [{ id: -1, name: "Host", isHost: true, cell: HOST_CELL, cellReason: "", chunk: [-5, 8] }];
      f.clients = [];
      for (let i = 0; i < count; i++) {
        const cid = 1000 + i;
        f.players.push({ id: cid, name: "P" + i, isHost: false, cell: [-75 + i, 70, 140], cellReason: "", chunk: [-5, 8] });
        f.clients.push({ id: cid, key: String(cid), name: "P" + i, reported: 1, currentLoaded: 1, merged: 1, complete: true,
          chunks: [[-5, 8, 1 + (i % 4)]] });
      }
      f.chunks = [];
      f.counts = { chunks: 0, columns: 0, players: count + 1, clientChunks: count, currentLoadedChunks: count,
        animals: 0, pendingMarks: 0, pendingClients: 0, clientsComplete: true, clientsIncompleteReason: "" };
      return f;
    }
    const keyOf = (r) => [r[0], r[1], r[2], r[3]].map((v) => round3(v)).join(",");
    const r40 = await runPage(manyClientsFeed(40), "");
    const rects40 = wireRectsAll(r40.ctx);
    check(rects40.length === 40, "40 个客户端各画一圈线框（40 圈）——实际 " + rects40.length);
    check(new Set(rects40.map(keyOf)).size === 1,
      "40 个客户端的线框**几何完全相同**（同一条区块边界，没有任何内缩 / 偏移）");
    check(rects40.every((r) => Math.abs(r[2] - SIDE_124) < 0.01 && Math.abs(r[3] - SIDE_124) < 0.01),
      "线框宽 / 高 = 16 × 12.4 = " + SIDE_124 + " px（与地形 16×16 区块同一映射）");
    const baseLw = Math.max(1.5, Math.min(3, 12.4 / 5));
    const lwSet = [...new Set(solidColorStrokes(r40.ctx).map((o) => round3(o.lw)))].sort((a, b) => a - b);
    check(lwSet.length === 5 && Math.abs(lwSet[0] - baseLw) < 0.01,
      "不同客户端只用线宽区分：最多 5 档（" + JSON.stringify(lwSet) + "），最细 = " + baseLw.toFixed(2) + " px");
    check(lwSet[lwSet.length - 1] <= baseLw + 4 * 2 + 2.01,
      "最宽一档仍有限（≤ " + (baseLw + 4 * 2 + 2).toFixed(2) + " px），几十个客户端也不会把线画粗到糊住地形");
    // 缩放后：几何仍贴区块边界，尺寸随缩放线性变化
    const rHalf = await runPage(manyClientsFeed(40), "?scale=0.5");
    const rectsHalf = wireRectsAll(rHalf.ctx);
    check(rectsHalf.length === 40 && new Set(rectsHalf.map(keyOf)).size === 1,
      "缩放后 40 圈几何仍完全相同（都贴区块边界，不偏移）");
    check(rectsHalf.every((r) => Math.abs(r[2] - 8) < 0.01),
      "缩放后线框宽 = 16 × 0.5 = 8 px（仍与地形同边界）");
    check(new Set(solidColorStrokes(rHalf.ctx).map((o) => round3(o.lw))).size === 5, "缩放后仍按 5 档线宽分层");
  }

  section("O. v8：clients[].chunks = 客户端当前内存区块 —— 卸载的块立刻不画（reported 只是会话累计参考值）");
  {
    // 同一身份：先是 9 块 → 下一次样本只剩 3 块（其余 6 块被客户端卸载）
    const first = baseFeed();
    const page = await runPage(first, "");
    const sizeBefore = frameCssOf(page.els);
    check(wireRectsAll(page.ctx).length === 9, "第一次样本：客户端当前内存 9 块 → 9 圈线框（实际 " + wireRectsAll(page.ctx).length + "）");

    const unloaded = baseFeed();
    unloaded.clients[0].chunks = CLIENT_CHUNKS.slice(0, 3);      // 只留 3 块（模拟客户端卸载了另外 6 块）
    unloaded.clients[0].currentLoaded = 3;
    unloaded.clients[0].merged = 3;
    unloaded.clients[0].reported = 9;                            // 会话累计仍是 9（不能因此判不完整）
    unloaded.counts.clientChunks = 3;
    unloaded.counts.currentLoadedChunks = 3;
    const rUn = await runPage(unloaded, "");
    check(wireRectsAll(rUn.ctx).length === 3,
      "卸载后：线框立刻只剩 3 圈（卸载的 6 块不再画）——实际 " + wireRectsAll(rUn.ctx).length);
    const wgU = wireRects(rUn.ctx, "#17a273"), wbU = wireRects(rUn.ctx, "#2f6fd0"),
      wyU = wireRects(rUn.ctx, "#e0a800"), wrU = wireRects(rUn.ctx, "#d1495f");
    check(wgU.length === 3 && wbU.length === 0 && wyU.length === 0 && wrU.length === 0,
      "卸载的蓝 / 黄 / 红块全部消失（只留下 3 个绿块）——实际 绿 " + wgU.length + " / 蓝 " + wbU.length +
      " / 黄 " + wyU.length + " / 红 " + wrU.length);
    const rlU = rUn.els.get("regionLegend").innerHTML;
    check(!/⚠/.test(rlU) && !/不完整/.test(rlU),
      "reported=9 但 currentLoaded=3 不被判「不完整」（完整性按 currentLoaded 判定，图例无 ⚠）");
    check(/客户端 1 个 · 线框 3 块/.test(rlU), "图例线框计数跟着当前内存收缩（只剩 3 块）");
    const sizeAfter = frameCssOf(rUn.els);
    // 视图作用域（主机 ∪ 当前内存区块）跟着收缩：主机 10 块 + 剩下 3 块（互不重叠）→ 共 13 块
    check(/本视图区块 2 个集合 · 共 13 区块/.test(rlU),
      "本视图区块集合跟着当前内存收缩（18 块 → 13 块）——实际：" + ((rlU.match(/本视图区块[^<]*/) || ["无"])[0]));
    check(!!sizeBefore && !!sizeAfter, "卸载前后都拿到了画布尺寸（" + (sizeBefore ? sizeBefore[0] : "—") + " → " +
      (sizeAfter ? sizeAfter[0] : "—") + " px；画布由「当前内存区块 ∪ 角色位置」决定，角色位置没变所以不一定变窄）");
    const sbU = rUn.els.get("statBody").innerHTML;
    check(/当前内存 3 块（绿 3 \/ 蓝 0 \/ 黄 0 \/ 红 0）· 会话累计上报 9/.test(sbU),
      "统计面板：当前内存 3 块 + 会话累计上报 9（两个数分开写）");
  }

  section("P. v8：s=0（刚加载、尚未上报/裁决）= 正常值，不画线框但不报警；缺 currentLoaded 才退回 reported");
  {
    const f = baseFeed();
    f.clients[0].chunks = CLIENT_CHUNKS.slice(0, 4).concat([[-12, 13, 0], [-11, 13, 0]]);   // 4 条有状态 + 2 条 s=0
    f.clients[0].currentLoaded = 6;
    f.clients[0].merged = 6;
    f.clients[0].reported = 9;
    f.counts.clientChunks = 6;
    f.counts.currentLoadedChunks = 6;
    const r = await runPage(f, "");
    const rl = r.els.get("regionLegend").innerHTML;
    const zeroStrokes = r.ctx.ops.filter((o) => o.op === "stroke" && o.style === "rgba(146,155,168,.95)" && o.rects.length);
    const zeroRects = zeroStrokes.reduce((n, o) => n + o.rects.length, 0);
    check(wireRectsAll(r.ctx).length === 4 && zeroRects === 2,
      "带状态的 4 块画四色线框、s=0 的 2 块画**中性灰**线框（当前内存 6 块全部画出）——实际 彩 " +
      wireRectsAll(r.ctx).length + " / 灰 " + zeroRects);
    // 线框层的 halo = 一次 stroke 覆盖全部 6 块（彩色 4 + 中性灰 2）；主机侧没有任何框
    const wireHalo = r.ctx.ops.find((o) => o.op === "stroke" && isDarkHalo(o.style) && o.rects.length === 6) || null;
    const zeroCovered = !!wireHalo && zeroStrokes.every((o) => o.rects.every((rr) =>
      wireHalo.rects.some((h) => Math.abs(h[0] - rr[0]) < 0.01 && Math.abs(h[1] - rr[1]) < 0.01 &&
        Math.abs(h[2] - rr[2]) < 0.01 && Math.abs(h[3] - rr[3]) < 0.01)));
    check(zeroCovered,
      "中性灰线框与四色线框共用同一层深色底衬（halo 覆盖当前内存 6 块）——实际：halo " +
      (wireHalo ? wireHalo.rects.length : 0) + " 块 / 灰覆盖 " + (zeroCovered ? "是" : "否"));
    check(/中性灰 = 已加载、尚未上报\/裁决/.test(rl), "图例新增「中性灰 = 已加载、尚未上报/裁决」一行");
    check(/灰 s=0 已加载未裁决 2 块（中性灰线框）/.test(rl), "图例如实写 s=0 块数并注明「中性灰线框」");
    check(!/⚠/.test(rl), "s=0 不报警（整个图例无 ⚠）——实际：" + ((rl.match(/⚠[^<]*/) || ["无"])[0]));
    check(/当前内存 6 块/.test(r.els.get("statBody").innerHTML),
      "统计面板按 currentLoaded 报当前内存 6 块（s=0 的块也算在「当前内存」里）");

    // 旧口径兜底：v8 但缺 currentLoaded → 完整性退回 reported，并在图例里明说
    const legacy = baseFeed();
    legacy.clients[0].chunks = CLIENT_CHUNKS.slice(0, 3);
    delete legacy.clients[0].currentLoaded;
    delete legacy.counts.currentLoadedChunks;
    legacy.clients[0].merged = 3;
    legacy.clients[0].reported = 3;
    const r2 = await runPage(legacy, "");
    check(wireRectsAll(r2.ctx).length === 3,
      "缺 currentLoaded（旧版 v7 feed）时仍能画线框（完整性顺利退回 reported）——实际 " + wireRectsAll(r2.ctx).length);
    check(/没有 <code>currentLoaded<\/code>/.test(r2.els.get("regionLegend").innerHTML) &&
      /按 reported 判完整性/.test(r2.els.get("statBody").innerHTML),
      "图例 / 统计面板都明说这是「缺 currentLoaded → 按 reported」的旧口径兜底");
  }

  section("Q. 主机地形配色：方块 id 按游戏源码校正（雪 61 / 水域 18 / 未收录 id 中性灰，不再哈希色相兜底）");
  {
    // 三块主机 terrain：id 61 = SnowBlock（雪，曾经没收录 → hsl 兜底成青色，这是用户看到的「大片均匀青色」根因）、
    // id 18 = WaterBlock（水）、id 232 = 未收录 id（游戏源码里没有常量 → 动态/Mod 方块）。
    const f = baseFeed();
    f.chunks = [
      { x: -5, z: 8, s: 2, n: 0, d: chunkData(61, 65) },
      { x: -4, z: 8, s: 2, n: 0, d: chunkData(18, 65) },
      { x: -3, z: 8, s: 2, n: 0, d: chunkData(232, 65) }
    ];
    f.bounds = { chunkMin: [-5, 8], chunkMax: [-3, 8], cellMin: [-80, 128], cellMax: [-32, 143] };
    f.counts = { chunks: 3, columns: 768, players: 2, clientChunks: 0, currentLoadedChunks: 0, pendingMarks: 0, pendingClients: 0, clientsComplete: true, clientsIncompleteReason: "" };
    const r = await runPage(f, "");
    const fillOps = r.ctx.ops.filter((o) => o.op === "fill" && o.rects.length >= 256);
    const styles = fillOps.map((o) => String(o.style));
    const parseRgb = (v) => {
      const m = /^rgb\((\d+),(\d+),(\d+)\)$/.exec(v);
      return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
    };
    const snow = styles.map(parseRgb).find((c) => c && c[0] > 225 && c[1] > 225 && c[2] > 225);
    const water = styles.map(parseRgb).find((c) => c && c[2] > c[0] + 40 && c[2] > c[1] + 20);
    const unknown = styles.map(parseRgb).find((c) => c && Math.max(...c) - Math.min(...c) < 24 && c[0] < 200);
    check(styles.length === 3 && styles.every((v) => v.indexOf("hsl(") < 0),
      "三块地形各自按 id 批量上色，且全部是 rgb(...)（没有任何 hsl 哈希兜底色）——实际 " + JSON.stringify(styles));
    check(!!snow, "id 61 = 雪 → 近白色 (#f0f5fa 提亮后)，不再是青色兜底（实际：" + JSON.stringify(snow) + "）");
    check(!!water, "id 18 = 水域 → 蓝色系（单独水域色阶，实际：" + JSON.stringify(water) + "）");
    check(!!unknown, "未收录 id 232 → 中性灰（不冒充水/地形，实际：" + JSON.stringify(unknown) + "）");
    const lg = r.els.get("legend").innerHTML;
    check(/雪/.test(lg), "地形图例出现「雪」（id 61 已收录）");
    check(/水域（水）/.test(lg), "地形图例出现「水域（水）」（id 18）");
    check(/未知方块 id232/.test(lg), "地形图例把未收录 id 明确标成「未知方块 id232」");
    check(/种方块 id 不在颜色表里（用中性灰画/.test(lg), "地形图例明确提示「N 种方块 id 不在颜色表里（用中性灰画）」——实际：" +
      ((lg.match(/⚠[^<]*/) || ["无"])[0]));
    check(!/·兜底色/.test(lg), "图例不再出现旧的「兜底色」措辞");
  }

  section("R. 图上只有一种框：客户端区域线框（实线 + halo）；主机地形只填充、无任何框");
  {
    const { ctx, els } = await runPage(baseFeed(), "");
    const boxLike = ctx.ops.filter((o) => o.op === "stroke" && o.dash.length > 0 && o.rects.length);
    check(boxLike.length === 0, "没有任何主机侧框（虚线 / 内缩内框都不存在）—— 实际 " + boxLike.length);
    const wires = wireRectsAll(ctx);
    check(wires.length === 9, "客户端区域线框 9 圈（实线）");
    check(wires.every((r) => Math.abs(r[2] - SIDE_124) < 0.01), "线框画在区块边界上（宽 = 16×scale，与地形 16×16 同边界）");
    const halo = ctx.ops.filter((o) => o.op === "stroke" && isDarkHalo(o.style) && o.rects.length === 9).length;
    check(halo === 1, "客户端线框有深色 halo（唯一框层的底衬）");
    const rl = els.get("regionLegend").innerHTML;
    check(/主机地形（只填充、无框/.test(rl) && /客户端区域线框（当前内存区块）/.test(rl),
      "图例只有两组：主机地形 / 客户端区域线框");
    check(/无框/.test(rl) && !/验证框/.test(rl), "图例写明主机侧无框、且不再提验证框");
  }

  section("S. 客户端 currentLoaded=0：醒目说明「不是画错位置」并写明原因");
  {
    const f = baseFeed();
    f.clients[0].chunks = [];
    f.clients[0].currentLoaded = 0;
    f.clients[0].merged = 0;
    f.clients[0].reported = 9;
    f.counts.clientChunks = 0;
    f.counts.currentLoadedChunks = 0;
    const { ctx, els } = await runPage(f, "");
    check(wireRectsAll(ctx).length === 0, "客户端当前内存为空 → 一圈客户端区域线框都不画");
    const rl = els.get("regionLegend").innerHTML;
    check(/客户端区域线框为 <b>0<\/b>/.test(rl), "图例出现醒目提示「客户端区域线框为 0」");
    check(/Android User（键 1234）/.test(rl), "提示里带客户端名字与身份稳定键");
    check(/不画任何客户端区域线框/.test(rl) && /不是画错位置/.test(rl), "提示写明「不画任何客户端区域线框（不是画错位置）」");
    check(/客户端未加载区块/.test(rl) && /在世界外 \/ 空中 \/ 刚加载完还没上报/.test(rl),
      "提示写明原因：客户端未加载区块（在世界外 / 空中 / 刚加载完还没上报）");
    check(/font-weight:600/.test(rl), "该提示是醒目样式（加粗）");
    const sb = els.get("statBody").innerHTML;
    check(/线框 0 块 · 客户端 1 个（当前内存均为空）→ 没有客户端区域线框/.test(sb),
      "统计面板写清「客户端线框数 = 0」");
    check(/原因：客户端未加载区块（在世界外 \/ 空中 \/ 刚加载完还没上报）/.test(sb), "统计面板同样给出原因");
  }

  section("T. 动物（v9 animals[]）：只画黑色实心圆点（无名字/坐标标签、不可选中、仅主机端）");
  {
    const feed = baseFeed();
    const { ctx, els } = await runPage(feed, "");
    const dotFills = ctx.ops.filter((o) => o.op === "fill" && o.style === "#000");
    const dots = dotFills.flatMap((o) => (o.lines || []).filter((l) => l[0] === "arc"));
    check(dots.length === feed.animals.length, "黑点数量 == animals[] 条数（" + feed.animals.length + "）——实际 " + dots.length);
    const rr = dots.length ? dots[0][3] : 0;
    check(dots.length > 0 && dots.every((d) => Math.abs(d[3] - rr) < 1e-6) && rr >= 2 && rr <= 3,
      "黑点是半径 2–3 px 的实心圆（实际 " + rr + " px）");
    check(dotFills.length === feed.animals.length, "每只动物各画一个点（fill 次数 == 条数，" + dotFills.length + "）");

    const textsWith = texts(ctx);
    const frame = frameCssOf(els);
    const scale = Number(els.get("scaleChip").textContent.split(" ")[0]);
    const x0 = HOST_CELL[0] + 0.5 - (frame[0] / 2) / scale;
    const z0 = HOST_CELL[2] + 0.5 - (frame[1] / 2) / scale;
    const notAtPos = feed.animals.filter((a) => !dots.some((d) =>
      Math.abs(d[1] - (a.x - x0) * scale) < 0.01 && Math.abs(d[2] - (a.z - z0) * scale) < 0.01));
    check(notAtPos.length === 0, "每只动物都画在它自己的世界坐标上（位置不符 " + notAtPos.length + " 个）");

    const noAnimals = baseFeed();
    delete noAnimals.animals;
    delete noAnimals.counts.animals;
    const r2 = await runPage(noAnimals, "");
    check(JSON.stringify(texts(r2.ctx)) === JSON.stringify(textsWith),
      "动物不产生任何文字标签（有 / 无 animals 的 canvas 文字集合完全一致）");
    check(textsWith.filter((t) => t.indexOf("chunk(") >= 0).length === 2, "两个角色的「名字 + 坐标 + 区块」标签仍在（动物不增加标签）");

    const strokeArcs = ctx.ops.filter((o) => o.op === "stroke" && o.lines).flatMap((o) => o.lines.filter((l) => l[0] === "arc"));
    check(strokeArcs.some((a) => a[3] === 4) && strokeArcs.some((a) => a[3] === 5), "角色标记仍是原有彩色圆点（选中 r=4 / 普通 r=5）");
    check(els.get("centerSel").innerHTML === r2.els.get("centerSel").innerHTML, "动物不进入「居中角色」下拉框（不可选中）");

    const rl = els.get("regionLegend").innerHTML;
    const sb = els.get("statBody").innerHTML;
    check(rl.indexOf("动物（主机端，仅位置）") >= 0 && rl.indexOf("黑点") >= 0, "图例新增「动物（主机端，仅位置）· 黑点」");
    check(/动物 3 \/ 3 个（本视图画出 \/ feed 列出）/.test(rl), "图例给出动物画出 / 列出数量（3 / 3）");
    check(/动物（animals\[\]）<\/td><td class='mono'>主机端动物 3 个（<code>counts\.animals<\/code> 3）· 本视图画出 3 个黑点/.test(sb),
      "统计面板列出动物数量 / counts.animals / 本视图画出数");
    check(sb.indexOf("animals 3") >= 0, "统计面板 counts 行包含 animals 3");
    check(rl.indexOf("⚠") < 0, "动物字段正常 → 图例不报警");

    check(r2.ctx.ops.filter((o) => o.op === "fill" && o.style === "#000").length === 0, "缺 animals 字段 → 一个黑点都不画");
    check(r2.els.get("regionLegend").innerHTML.indexOf("没有 <code>animals</code> 字段") >= 0, "缺 animals 字段 → 图例明说「不画动物」");
    const bad = baseFeed();
    bad.animals = [{ x: -100, z: 120 }, { x: "abc", z: 1 }, { z: 5 }, { y: 70 }];
    bad.counts.animals = 4;
    const r3 = await runPage(bad, "");
    const badDots = r3.ctx.ops.filter((o) => o.op === "fill" && o.style === "#000");
    check(badDots.length === 1, "x/z 非法的条目一律不画（4 条里只有 1 条可定位 → 1 个黑点）——实际 " + badDots.length);
    check(r3.els.get("regionLegend").innerHTML.indexOf("3 条 <code>animals[]</code> 条目没有可用的 x / z") >= 0,
      "图例如实报出坐标非法的条目数（3 条）");

    const far = baseFeed();
    far.animals = [{ x: 99999, y: 70, z: 99999 }, { x: -100, y: 70, z: 120 }];
    far.counts.animals = 2;
    const r4 = await runPage(far, "");
    check(r4.ctx.ops.filter((o) => o.op === "fill" && o.style === "#000").length === 1,
      "画布外的动物不画（只画画布内那 1 只）");
    check(/动物 1 \/ 2 个/.test(r4.els.get("regionLegend").innerHTML) && r4.els.get("regionLegend").innerHTML.indexOf("画布外 1") >= 0,
      "图例写明画出 1 / 2 个 · 画布外 1 个");

    const arcR = (r) => {
      const a = r.ctx.ops.filter((o) => o.op === "fill" && o.style === "#000")
        .flatMap((o) => (o.lines || []).filter((l) => l[0] === "arc"));
      return a.length ? Math.round(a[0][3] * 100) / 100 : 0;
    };
    const r40 = await runPage(baseFeed(), "?scale=40");
    const r6 = await runPage(baseFeed(), "?scale=6");
    check(arcR(r40) === 3 && arcR(r6) === 2, "黑点半径随缩放轻微变化（40 px/格 → 3 px；6 px/格 → 2 px）——实际 " + arcR(r40) + " / " + arcR(r6));
  }

  section("U. 对齐回归：客户端线框边界 == 地形 16×16 区块边界（容差 ≤0.5px）；客户端独有区块无地形（不是错位）");
  {
    const feed = baseFeed();
    const { ctx, els } = await runPage(feed, "");
    const map = worldToCss(els, HOST_CELL, 12.4);
    const side = 16 * 12.4;
    const terrainRects = ctx.ops.filter((o) => o.op === "fill" && o.rects.length > 50).flatMap((o) => o.rects);
    const hostSet = new Set(feed.chunks.map((c) => c.x + "," + c.z));
    const overlap = CLIENT_CHUNKS.filter(([cx, cz]) => hostSet.has(cx + "," + cz));
    check(overlap.length >= 1, "feed 里有同时属于主机 chunks[] 与客户端 chunks 的区块（用于对齐比对）");
    let checked = 0, worst = 0, bad = [];
    for (const [cx, cz] of overlap) {
      const bx = map.px(cx * 16), bz = map.pz(cz * 16);
      let mnx = 1e9, mnz = 1e9, mxx = -1e9, mxz = -1e9, n = 0;
      for (const r of terrainRects) {
        if (r[0] >= bx - 0.01 && r[0] < bx + side - 0.01 && r[1] >= bz - 0.01 && r[1] < bz + side - 0.01) {
          mnx = Math.min(mnx, r[0]); mnz = Math.min(mnz, r[1]);
          mxx = Math.max(mxx, r[0] + r[2]); mxz = Math.max(mxz, r[1] + r[3]); n++;
        }
      }
      if (!n) continue;
      const w = wireRectsAll(ctx).find((r) => Math.abs(r[0] - bx) < 0.01 && Math.abs(r[1] - bz) < 0.01);
      if (!w) continue;
      const d = Math.max(Math.abs(w[0] - mnx), Math.abs(w[1] - mnz), Math.abs(w[0] + w[2] - mxx), Math.abs(w[1] + w[3] - mxz));
      worst = Math.max(worst, d);
      checked++;
      if (d > 0.5) bad.push([cx, cz, round3(d)]);
    }
    check(checked >= 1 && bad.length === 0,
      "对齐回归：" + checked + " 个「主机 + 客户端重合区块」的线框边界 vs 地形 16×16 边界最大偏差 " +
      worst.toFixed(4) + " px（要求 ≤0.5）" + (bad.length ? " 超差：" + JSON.stringify(bad) : ""));
    // 客户端独有区块：没有主机地形（无底图可比 → 不是错位），但仍要画线框
    const clientOnly = CLIENT_CHUNKS.filter(([cx, cz]) => !hostSet.has(cx + "," + cz));
    let noTerrain = 0, wireOk = 0;
    for (const [cx, cz] of clientOnly) {
      const bx = map.px(cx * 16), bz = map.pz(cz * 16);
      const hasTerrain = terrainRects.some((r) => r[0] >= bx - 0.01 && r[0] < bx + side - 0.01 && r[1] >= bz - 0.01 && r[1] < bz + side - 0.01);
      if (!hasTerrain) noTerrain++;
      if (wireRectsAll(ctx).some((r) => Math.abs(r[0] - bx) < 0.01 && Math.abs(r[1] - bz) < 0.01)) wireOk++;
    }
    check(noTerrain === clientOnly.length, "客户端独有区块 " + clientOnly.length + " 块没有主机地形（没有底图可比，不是错位）");
    check(wireOk === clientOnly.length, "客户端独有区块仍照常画出线框（" + wireOk + "/" + clientOnly.length + "）");
  }

  console.log("页面文件：" + htmlPath + "（" + htmlBytes + " 字节）");
  console.log("结果：" + pass + "/" + (pass + fail) + " 通过" + (fail ? "，" + fail + " 失败" : ""));
  if (fail) { console.log("失败项："); failures.forEach((f) => console.log("  - " + f)); process.exit(1); }
  console.log("RESULT livemap page cases passed");
}

main().catch((e) => { console.error("检查脚本自身异常：" + (e && e.stack ? e.stack : e)); process.exit(2); });
