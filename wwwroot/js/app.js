"use strict";

/* ================= helpers ================= */

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const num = (v) => Number(v ?? 0);
const cls = (v) => (num(v) > 0 ? "pos" : num(v) < 0 ? "neg" : "");
const moneyFmt = new Intl.NumberFormat("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

function money(v, { sign = true, cur = "$" } = {}) {
  const n = num(v);
  const s = cur + moneyFmt.format(Math.abs(n));
  if (n < 0) return "−" + s;
  return (sign && n > 0 ? "+" : "") + s;
}
const pct = (v) => `${num(v).toFixed(1)}%`;

function ymd(d) {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}
const parseDay = (s) => { const [y, m, d] = s.split("-").map(Number); return new Date(y, m - 1, d); };
const fmtDate = (s) => (s ? new Date(s).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" }) : "");
const fmtDateTime = (s) => (s ? new Date(s).toLocaleString("en-GB", { day: "numeric", month: "short", year: "2-digit", hour: "2-digit", minute: "2-digit" }) : "");
const utc = (s) => (s && !/[zZ]|[+-]\d\d:\d\d$/.test(s) ? s + "Z" : s);
const fmtDayLong = (s) => parseDay(s).toLocaleDateString("en-GB", { weekday: "long", day: "numeric", month: "long", year: "numeric" });

function holdText(open, close) {
  if (!close) return "";
  const mins = Math.max(0, Math.round((new Date(close) - new Date(open)) / 60000));
  if (mins < 60) return `${mins}m`;
  if (mins < 60 * 24) return `${Math.floor(mins / 60)}h ${mins % 60}m`;
  const days = Math.floor(mins / 1440);
  return `${days}d ${Math.floor((mins % 1440) / 60)}h`;
}

function legsText(t) {
  if (t.assetCategory === "STK") return `${num(t.quantity)} shares`;
  const exp = [...new Set(t.legs.map((l) => l.expiry && fmtDate(l.expiry)))];
  const legs = t.legs.map((l) => `${l.side === "Short" ? "−" : "+"}${num(l.quantity)} ${num(l.strike)}${l.putCall ?? ""}`).join(" / ");
  return exp.length === 1 ? `${legs}, ${exp[0]}` : legs;
}

async function api(path, opts = {}) {
  const headers = opts.body && !(opts.body instanceof FormData) ? { "Content-Type": "application/json" } : {};
  const res = await fetch(path, { ...opts, headers });
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) throw new Error(data?.message || data?.title || `Request failed (${res.status})`);
  return data;
}

let toastTimer;
function toast(msg, bad = false) {
  const t = $("#toast");
  t.textContent = msg;
  t.className = "show" + (bad ? " bad" : "");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.className = ""), 3200);
}

function cssVar(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }

/* ================= global filter ================= */

const filter = Object.assign({ range: "30", from: "", to: "", asset: "" }, JSON.parse(localStorage.getItem("tj.filter") || "{}"));

function applyRange(range) {
  filter.range = range;
  const today = new Date();
  if (range === "all") { filter.from = ""; filter.to = ""; }
  else if (range === "ytd") { filter.from = `${today.getFullYear()}-01-01`; filter.to = ymd(today); }
  else if (range !== "custom") {
    const from = new Date(today);
    from.setDate(from.getDate() - Number(range) + 1);
    filter.from = ymd(from);
    filter.to = ymd(today);
  }
}

function qs(extra = {}) {
  const p = new URLSearchParams();
  if (filter.from) p.set("from", filter.from);
  if (filter.to) p.set("to", filter.to);
  if (filter.asset) p.set("asset", filter.asset);
  for (const [k, v] of Object.entries(extra)) if (v !== "" && v != null) p.set(k, v);
  return p.toString();
}

function syncFilterBar() {
  $$("#rangeSeg button").forEach((b) => b.classList.toggle("on", b.dataset.range === filter.range));
  $$("#assetSeg button").forEach((b) => b.classList.toggle("on", b.dataset.asset === filter.asset));
  $("#fFrom").value = filter.from;
  $("#fTo").value = filter.to;
  localStorage.setItem("tj.filter", JSON.stringify(filter));
}

function initFilterBar() {
  if (filter.range !== "custom") applyRange(filter.range);
  $("#rangeSeg").addEventListener("click", (e) => {
    const b = e.target.closest("button"); if (!b) return;
    applyRange(b.dataset.range); syncFilterBar(); render();
  });
  $("#assetSeg").addEventListener("click", (e) => {
    const b = e.target.closest("button"); if (!b) return;
    filter.asset = b.dataset.asset; syncFilterBar(); render();
  });
  for (const id of ["#fFrom", "#fTo"]) {
    $(id).addEventListener("change", () => {
      filter.range = "custom";
      filter.from = $("#fFrom").value;
      filter.to = $("#fTo").value;
      syncFilterBar(); render();
    });
  }
  syncFilterBar();
}

/* ================= router ================= */

let dirty = false;
let suppressNext = false;
let currentHash = location.hash;
let charts = [];
let lastRoute = null;

window.addEventListener("hashchange", () => {
  if (suppressNext) { suppressNext = false; return; }
  if (dirty && !confirm("You have unsaved journal notes. Leave this page without saving?")) {
    suppressNext = true;
    location.hash = currentHash;
    return;
  }
  dirty = false;
  currentHash = location.hash;
  render();
});
window.addEventListener("beforeunload", (e) => { if (dirty) e.preventDefault(); });

async function render() {
  const [route, ...rest] = (location.hash.replace(/^#\/?/, "") || "dashboard").split("/");
  const arg = rest.map(decodeURIComponent).join("/");
  $$(".sidebar a[data-nav]").forEach((a) => a.classList.toggle("active", a.dataset.nav === route || (route === "trade" && a.dataset.nav === "trades")));
  $("#filterbar").classList.toggle("hidden", ["trade", "sync"].includes(route));

  charts.forEach((c) => c.destroy());
  charts = [];
  const view = $("#view");
  // Keep the current content while moving within the same page (e.g. day to day),
  // so the page height and scroll position don't jump.
  if (route !== lastRoute) view.innerHTML = `<p class="muted">Loading…</p>`;
  lastRoute = route;
  try {
    switch (route) {
      case "trades": await renderTrades(view); break;
      case "trade": await renderTrade(view, arg); break;
      case "daily": await renderDaily(view, arg); break;
      case "reports": await renderReports(view); break;
      case "sync": await renderSync(view); break;
      default: await renderDashboard(view);
    }
  } catch (err) {
    view.innerHTML = `<div class="notice bad">${esc(err.message)}</div>`;
  }
  view.focus({ preventScroll: true });
}

async function refreshLastSync() {
  try {
    const logs = await api("/api/sync/logs");
    const last = logs.find((l) => l.status === "Success");
    $("#lastSync").textContent = last ? `Last sync ${fmtDateTime(last.finishedAt)}` : "Not synced yet";
  } catch { /* ignore */ }
}

/* ================= dashboard ================= */

let calMonth = null; // Date for the first day of the shown month

async function renderDashboard(view) {
  const d = await api("/api/dashboard?" + qs());
  const s = d.summary;

  if (s.trades === 0 && d.open.length === 0) {
    view.innerHTML = `
      <div class="panel empty">
        <h2>No closed trades in this date range</h2>
        <p>Pick a wider range above, or sync your IBKR account to bring in your trades.</p>
        <a class="btn" href="#/sync">Go to sync</a>
      </div>`;
    return;
  }

  const pf = s.profitFactor == null ? "No losses" : num(s.profitFactor).toFixed(2);
  view.innerHTML = `
    <div class="headline">
      <div>
        <div class="big ${cls(s.netPnl)}">${money(s.netPnl)}</div>
        <div class="sub">Net P&amp;L from ${s.trades} closed trade${s.trades === 1 ? "" : "s"}, after ${money(Math.abs(s.commission), { sign: false })} in commissions</div>
      </div>
    </div>
    <div class="metrics">
      ${metric(pct(s.winRate), `Win rate (${s.wins} won, ${s.losses} lost)`)}
      ${metric(pf, "Profit factor")}
      ${metric(money(s.expectancy), "Average per trade", cls(s.expectancy))}
      ${metric(money(s.avgWin), "Average win", "pos")}
      ${metric(money(s.avgLoss), "Average loss", "neg")}
      ${metric(money(-s.maxDrawdown), "Max drawdown", s.maxDrawdown > 0 ? "neg" : "")}
      ${metric(money(s.largestWin), "Largest win", "pos")}
      ${metric(money(s.largestLoss), "Largest loss", "neg")}
      ${metric(`${s.maxWinStreak} / ${s.maxLossStreak}`, "Longest win / loss streak")}
      ${metric(`${s.avgHoldHoursWin}h / ${s.avgHoldHoursLoss}h`, "Average hold, wins / losses")}
      ${metric(s.avgR == null ? "Not set" : `${num(s.avgR).toFixed(2)}R`, "Average R multiple")}
      ${metric(`${s.journaled} of ${s.trades}`, "Trades journaled")}
    </div>

    <div class="panel" style="margin-top:20px">
      <div class="cal-head">
        <div class="month" id="calTitle"></div>
        <div class="cal-nav">
          <button id="calPrev" aria-label="Previous month">‹ Prev</button>
          <button id="calToday">This month</button>
          <button id="calNext" aria-label="Next month">Next ›</button>
        </div>
      </div>
      <div class="calendar" id="calendar"></div>
      <div class="legend"><span><i></i>Daily journal written</span><span>Select a day to open its journal</span></div>
    </div>

    <div class="grid-2">
      <div class="panel">
        <div class="panel-head"><h2>Cumulative P&amp;L</h2></div>
        <div class="chart-box"><canvas id="equityChart"></canvas></div>
      </div>
      <div class="panel">
        <div class="panel-head"><h2>Daily P&amp;L</h2></div>
        <div class="chart-box"><canvas id="dailyChart"></canvas></div>
      </div>
    </div>

    <div class="grid-2">
      <div class="panel">
        <div class="panel-head"><h2>Recent closed trades</h2><a href="#/trades" class="small muted">All trades</a></div>
        ${miniTable(d.recent)}
      </div>
      <div class="panel">
        <div class="panel-head"><h2>Open positions</h2><span class="small muted">${d.open.length}</span></div>
        ${d.open.length ? miniTable(d.open, true) : `<p class="muted">No open positions.</p>`}
      </div>
    </div>`;

  if (!calMonth) {
    const lastDay = d.days.length ? parseDay(d.days[d.days.length - 1].date) : new Date();
    calMonth = new Date(lastDay.getFullYear(), lastDay.getMonth(), 1);
  }
  $("#calPrev").onclick = () => { calMonth = new Date(calMonth.getFullYear(), calMonth.getMonth() - 1, 1); drawCalendar(); };
  $("#calNext").onclick = () => { calMonth = new Date(calMonth.getFullYear(), calMonth.getMonth() + 1, 1); drawCalendar(); };
  $("#calToday").onclick = () => { const t = new Date(); calMonth = new Date(t.getFullYear(), t.getMonth(), 1); drawCalendar(); };
  drawCalendar();
  bindRows(view);
  drawDashboardCharts(d);
}

function metric(v, k, c = "") {
  return `<div class="metric"><div class="v ${c}">${esc(v)}</div><div class="k">${esc(k)}</div></div>`;
}

function miniTable(rows, open = false) {
  if (!rows.length) return `<p class="muted">Nothing here yet.</p>`;
  return `<div class="table-wrap"><table><thead><tr>
      <th>${open ? "Opened" : "Closed"}</th><th>Symbol</th><th>Strategy</th><th class="num">${open ? "Entry cash" : "Net P&amp;L"}</th>
    </tr></thead><tbody>
    ${rows.map(({ trade: t, note }) => `
      <tr class="click" data-key="${esc(t.tradeKey)}">
        <td>${fmtDate(open ? t.openTime : t.closeTime)}</td>
        <td><b>${esc(t.underlying)}</b></td>
        <td>${esc(t.strategy)} ${note ? `<span class="journaled" title="Journaled">✎</span>` : ""}</td>
        <td class="num ${open ? cls(t.entryCash) : cls(t.netPnl)}">${open ? money(t.entryCash) : pnlCell(t)}</td>
      </tr>`).join("")}
    </tbody></table></div>`;
}

function pnlCell(t) {
  if (t.incompleteHistory) return `<span class="badge warn" title="The opening fills are older than the synced range">Partial history</span>`;
  if (t.status === "Open") return `<span class="badge">Open</span>`;
  return money(t.netPnl);
}

function bindRows(root) {
  $$("tr.click", root).forEach((tr) => {
    tr.tabIndex = 0;
    const go = () => (location.hash = `#/trade/${encodeURIComponent(tr.dataset.key)}`);
    tr.addEventListener("click", go);
    tr.addEventListener("keydown", (e) => { if (e.key === "Enter") go(); });
  });
}

async function drawCalendar() {
  const box = $("#calendar");
  if (!box) return;
  const first = calMonth;
  const last = new Date(first.getFullYear(), first.getMonth() + 1, 0);
  $("#calTitle").textContent = first.toLocaleDateString("en-GB", { month: "long", year: "numeric" });

  const params = new URLSearchParams({ from: ymd(first), to: ymd(last) });
  if (filter.asset) params.set("asset", filter.asset);
  const days = await api("/api/daily?" + params);
  const map = Object.fromEntries(days.map((d) => [d.date, d]));
  const maxAbs = Math.max(1, ...days.map((d) => Math.abs(num(d.pnl))));
  const todayKey = ymd(new Date());

  const start = new Date(first);
  start.setDate(start.getDate() - ((start.getDay() + 6) % 7)); // back to Monday

  let html = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"].map((d) => `<div class="dow">${d}</div>`).join("") + `<div class="dow">Week</div>`;
  const cur = new Date(start);
  while (cur <= last || cur.getDay() !== 1) {
    let weekPnl = 0, weekTrades = 0;
    for (let i = 0; i < 7; i++) {
      const key = ymd(cur);
      const inMonth = cur.getMonth() === first.getMonth();
      const d = map[key];
      if (!inMonth) {
        html += `<div class="day out" aria-hidden="true"></div>`;
      } else {
        let style = "";
        if (d && d.trades) {
          const strength = 12 + Math.round((Math.abs(num(d.pnl)) / maxAbs) * 33);
          const color = num(d.pnl) >= 0 ? "--profit" : "--loss";
          style = `style="background: color-mix(in srgb, var(${color}) ${strength}%, var(--surface))"`;
          weekPnl += num(d.pnl);
          weekTrades += d.trades;
        }
        html += `<a class="day ${key === todayKey ? "today" : ""}" href="#/daily/${key}" ${style}
                   aria-label="${esc(fmtDayLong(key))}${d && d.trades ? `, ${money(d.pnl)}` : ""}">
            <span class="d">${cur.getDate()}</span>
            ${d && d.trades ? `<span><div class="p ${cls(d.pnl)}">${money(d.pnl)}</div><div class="t">${d.trades} trade${d.trades === 1 ? "" : "s"}</div></span>` : "<span></span>"}
            ${d && d.hasNote ? `<span class="dot" title="Daily journal written"></span>` : ""}
          </a>`;
      }
      cur.setDate(cur.getDate() + 1);
    }
    html += `<div class="week-total">${weekTrades ? `<b class="${cls(weekPnl)}">${money(weekPnl)}</b>${weekTrades} trades` : "No trades"}</div>`;
  }
  box.innerHTML = html;
}

function drawDashboardCharts(d) {
  if (!window.Chart) {
    for (const id of ["#equityChart", "#dailyChart"]) $(id).parentElement.innerHTML = `<p class="muted">Charts need an internet connection to load Chart.js.</p>`;
    return;
  }
  const ink = cssVar("--muted"), line = cssVar("--line"), profit = cssVar("--profit"), loss = cssVar("--loss");
  Chart.defaults.font.family = cssVar("--sans");
  Chart.defaults.color = ink;
  const labels = d.equity.map((p) => fmtDate(p.date));
  const scales = {
    x: { grid: { display: false }, ticks: { maxTicksLimit: 8 } },
    y: { grid: { color: line }, ticks: { callback: (v) => money(v, { sign: false }).replace(".00", "") } },
  };
  const tooltip = { callbacks: { label: (c) => money(c.parsed.y) } };

  charts.push(new Chart($("#equityChart"), {
    type: "line",
    data: { labels, datasets: [{ data: d.equity.map((p) => num(p.cumulative)), borderColor: cssVar("--ink"), borderWidth: 2, pointRadius: 0, tension: 0.2, fill: { target: "origin", above: profit + "22", below: loss + "22" } }] },
    options: { maintainAspectRatio: false, plugins: { legend: { display: false }, tooltip }, scales, interaction: { mode: "index", intersect: false } },
  }));
  charts.push(new Chart($("#dailyChart"), {
    type: "bar",
    data: { labels: d.days.map((p) => fmtDate(p.date)), datasets: [{ data: d.days.map((p) => num(p.pnl)), backgroundColor: d.days.map((p) => (num(p.pnl) >= 0 ? profit : loss)), borderRadius: 2 }] },
    options: { maintainAspectRatio: false, plugins: { legend: { display: false }, tooltip }, scales },
  }));
}

/* ================= trades list ================= */

const tradeFilter = { q: "", result: "", status: "", strategy: "", underlying: "", tag: "", journaled: "" };

async function renderTrades(view) {
  const [rows, meta] = await Promise.all([api("/api/trades?" + qs(tradeFilter)), api("/api/meta")]);
  const allTags = [...meta.tags.setup, ...meta.tags.mistake, ...meta.tags.emotion];
  const opt = (v, label, cur) => `<option value="${esc(v)}" ${v === cur ? "selected" : ""}>${esc(label)}</option>`;
  const closed = rows.filter((r) => r.trade.status === "Closed" && !r.trade.incompleteHistory);
  const total = closed.reduce((a, r) => a + num(r.trade.netPnl), 0);

  view.innerHTML = `
    <div class="page-head">
      <div><h1>Trades</h1><p class="muted">${rows.length} trades, ${closed.length} closed, net <span class="${cls(total)}">${money(total)}</span></p></div>
      <button id="exportCsv">Export CSV</button>
    </div>
    <div class="trade-filters">
      <input type="search" id="tq" placeholder="Search symbol, strategy or notes" value="${esc(tradeFilter.q)}" aria-label="Search">
      <select id="tresult" aria-label="Result">${opt("", "Wins and losses", tradeFilter.result)}${opt("win", "Wins", tradeFilter.result)}${opt("loss", "Losses", tradeFilter.result)}</select>
      <select id="tstatus" aria-label="Status">${opt("", "Open and closed", tradeFilter.status)}${opt("Closed", "Closed", tradeFilter.status)}${opt("Open", "Open", tradeFilter.status)}</select>
      <select id="tstrategy" aria-label="Strategy">${opt("", "All strategies", tradeFilter.strategy)}${meta.strategies.map((s) => opt(s, s, tradeFilter.strategy)).join("")}</select>
      <select id="tunderlying" aria-label="Symbol">${opt("", "All symbols", tradeFilter.underlying)}${meta.underlyings.map((s) => opt(s, s, tradeFilter.underlying)).join("")}</select>
      <select id="ttag" aria-label="Tag">${opt("", "All tags", tradeFilter.tag)}${[...new Set(allTags)].map((s) => opt(s, s, tradeFilter.tag)).join("")}</select>
      <select id="tjournaled" aria-label="Journal">${opt("", "Journaled or not", tradeFilter.journaled)}${opt("yes", "Journaled", tradeFilter.journaled)}${opt("no", "Not journaled yet", tradeFilter.journaled)}</select>
    </div>
    ${rows.length ? `
    <div class="table-wrap"><table>
      <thead><tr>
        <th>Date</th><th>Symbol</th><th>Strategy</th><th>Legs</th><th class="num">DTE</th><th>Held</th><th>How it closed</th><th class="num">Net P&amp;L</th><th>Journal</th>
      </tr></thead>
      <tbody>${rows.map(tradeRow).join("")}</tbody>
    </table></div>` : `<div class="panel empty"><h2>No trades match these filters</h2><p>Clear a filter or widen the date range.</p></div>`}`;

  const map = { tq: "q", tresult: "result", tstatus: "status", tstrategy: "strategy", tunderlying: "underlying", ttag: "tag", tjournaled: "journaled" };
  for (const [id, key] of Object.entries(map)) {
    const el = $("#" + id);
    const handler = () => { tradeFilter[key] = el.value; render(); };
    if (id === "tq") el.addEventListener("keydown", (e) => { if (e.key === "Enter") handler(); });
    else el.addEventListener("change", handler);
  }
  $("#exportCsv").onclick = () => exportCsv(rows);
  bindRows(view);
}

function tradeRow({ trade: t, note }) {
  const tags = note ? [
    ...note.setupTags.map((x) => `<span class="chip">${esc(x)}</span>`),
    ...note.mistakeTags.map((x) => `<span class="chip mistake">${esc(x)}</span>`),
    ...note.emotionTags.map((x) => `<span class="chip emotion">${esc(x)}</span>`),
  ].join("") : "";
  return `
    <tr class="click" data-key="${esc(t.tradeKey)}">
      <td>${fmtDate(t.closeTime || t.openTime)}</td>
      <td><b>${esc(t.underlying)}</b></td>
      <td>${esc(t.strategy)}<div class="legs">${esc(t.direction)}</div></td>
      <td class="legs">${esc(legsText(t))}</td>
      <td class="num">${t.dteAtOpen ?? ""}</td>
      <td>${holdText(t.openTime, t.closeTime)}</td>
      <td>${esc(t.closeReason)}</td>
      <td class="num ${cls(t.netPnl)}">${pnlCell(t)}</td>
      <td class="wrap">${note ? tags || `<span class="journaled">Notes written</span>` : `<span class="muted small">Add notes</span>`}</td>
    </tr>`;
}

function exportCsv(rows) {
  const head = ["Opened", "Closed", "Symbol", "Strategy", "Direction", "Legs", "DTE", "Status", "How it closed", "Net P&L", "Commission", "Setup", "Mistakes", "Emotions", "Stress before", "Stress after", "Followed plan", "Lesson"];
  const q = (v) => `"${String(v ?? "").replace(/"/g, '""')}"`;
  const lines = rows.map(({ trade: t, note: n }) => [
    t.openTime, t.closeTime, t.underlying, t.strategy, t.direction, legsText(t), t.dteAtOpen, t.status, t.closeReason, t.netPnl, t.commission,
    n?.setupTags.join("; "), n?.mistakeTags.join("; "), n?.emotionTags.join("; "), n?.stressBefore, n?.stressAfter,
    n?.followedPlan == null ? "" : n.followedPlan ? "Yes" : "No", n?.lesson,
  ].map(q).join(","));
  const blob = new Blob([[head.map(q).join(","), ...lines].join("\r\n")], { type: "text/csv" });
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = `trades-${ymd(new Date())}.csv`;
  a.click();
  URL.revokeObjectURL(a.href);
}

/* ================= reusable inputs ================= */

function tagPicker(el, kind, options, selected, onChange) {
  const chosen = new Set(selected);
  const all = [...new Set([...options, ...selected])];
  const draw = () => {
    el.className = `tagpick ${kind}`;
    el.innerHTML = all.map((t) => `<button type="button" aria-pressed="${chosen.has(t)}" data-t="${esc(t)}">${esc(t)}</button>`).join("")
      + `<input type="text" placeholder="Add your own" aria-label="Add a ${kind} tag">`;
    $$("button", el).forEach((b) => b.onclick = () => {
      const t = b.dataset.t;
      chosen.has(t) ? chosen.delete(t) : chosen.add(t);
      b.setAttribute("aria-pressed", chosen.has(t));
      onChange([...chosen]);
    });
    $("input", el).addEventListener("keydown", (e) => {
      if (e.key !== "Enter") return;
      e.preventDefault();
      const v = e.target.value.trim();
      if (!v) return;
      if (!all.some((x) => x.toLowerCase() === v.toLowerCase())) all.push(v);
      chosen.add(all.find((x) => x.toLowerCase() === v.toLowerCase()));
      onChange([...chosen]);
      draw();
      $("input", el).focus();
    });
  };
  draw();
}

function scale(el, value, max, ends, onChange) {
  let cur = value ?? null;
  el.className = "scale";
  el.innerHTML = `<div class="row" role="group">${Array.from({ length: max }, (_, i) => i + 1)
    .map((n) => `<button type="button" aria-pressed="${cur === n}" data-n="${n}">${n}</button>`).join("")}</div>
    ${ends ? `<div class="ends" style="max-width:${max * 38}px"><span>${esc(ends[0])}</span><span>${esc(ends[1])}</span></div>` : ""}`;
  $$("button", el).forEach((b) => b.onclick = () => {
    const n = Number(b.dataset.n);
    cur = cur === n ? null : n;
    $$("button", el).forEach((x) => x.setAttribute("aria-pressed", Number(x.dataset.n) === cur));
    onChange(cur);
  });
}

function yesNo(el, value, onChange) {
  let cur = value ?? null;
  el.className = "seg";
  el.innerHTML = `<button type="button" data-v="true">Yes</button><button type="button" data-v="false">No</button>`;
  const draw = () => $$("button", el).forEach((b) => b.classList.toggle("on", String(cur) === b.dataset.v));
  $$("button", el).forEach((b) => b.onclick = () => {
    const v = b.dataset.v === "true";
    cur = cur === v ? null : v;
    draw(); onChange(cur);
  });
  draw();
}

/* ================= trade detail + journal ================= */

async function renderTrade(view, key) {
  const [data, meta] = await Promise.all([api(`/api/trades/${encodeURIComponent(key)}`), api("/api/meta")]);
  const t = data.trade;
  const n = data.note || {
    setupTags: [], mistakeTags: [], emotionTags: [], stressBefore: null, stressAfter: null, confidence: null, executionRating: null,
    followedPlan: null, plannedRisk: null, entryReason: "", exitReason: "", wentWell: "", wentWrong: "", lesson: "", notes: "", screenshots: [],
  };
  const r = n.plannedRisk ? num(t.netPnl) / num(n.plannedRisk) : null;
  const cur = t.currency === "USD" ? "$" : t.currency + " ";

  view.innerHTML = `
    <a class="back" href="#/trades">‹ All trades</a>
    <div class="trade-head">
      <div>
        <h1>${esc(t.underlying)} ${esc(t.strategy)}</h1>
        <div class="facts">
          <span>${esc(t.direction)}</span>
          <span>Opened <b>${fmtDateTime(t.openTime)}</b></span>
          ${t.closeTime ? `<span>Closed <b>${fmtDateTime(t.closeTime)}</b></span><span>Held <b>${holdText(t.openTime, t.closeTime)}</b></span>` : `<span><b>Still open</b></span>`}
          ${t.dteAtOpen != null ? `<span>DTE at entry <b>${t.dteAtOpen}</b></span>` : ""}
          ${t.incompleteHistory ? "" : `<span>Entry ${num(t.entryCash) >= 0 ? "credit" : "debit"} <b>${money(Math.abs(t.entryCash), { sign: false, cur })}</b></span>`}
          <span>Commission <b>${money(t.commission, { cur })}</b></span>
          ${t.closeReason ? `<span>How it closed <b>${esc(t.closeReason)}</b></span>` : ""}
          ${r != null ? `<span>Result <b class="${cls(r)}">${r.toFixed(2)}R</b></span>` : ""}
        </div>
      </div>
      <div class="pnl ${t.status === "Closed" && !t.incompleteHistory ? cls(t.netPnl) : ""}">${t.incompleteHistory ? "" : t.status === "Open" ? "Open" : money(t.netPnl, { cur })}</div>
    </div>
    ${t.incompleteHistory ? `<div class="notice" style="margin-bottom:16px">This position was opened before the synced date range, so its P&amp;L is not complete and it is left out of your statistics. Sync a longer range to fix this.</div>` : ""}

    <div class="table-wrap">
      <table><thead><tr><th>Side</th><th class="num">Qty</th><th>Contract</th><th class="num">Avg open</th><th class="num">Avg close</th><th>How it closed</th><th class="num">Net P&amp;L</th></tr></thead>
      <tbody>${t.legs.map((l) => `
        <tr><td>${esc(l.side)}</td><td class="num">${num(l.quantity)}</td><td>${esc(l.description || l.symbol)}</td>
        <td class="num">${num(l.avgOpenPrice).toFixed(2)}</td><td class="num">${l.avgClosePrice == null ? "" : num(l.avgClosePrice).toFixed(2)}</td>
        <td>${esc(l.closeReason || "Open")}</td><td class="num ${cls(l.netPnl)}">${l.closeTime ? money(l.netPnl, { cur }) : ""}</td></tr>`).join("")}
      </tbody></table>
    </div>
    <details style="margin-top:10px"><summary class="small muted" style="cursor:pointer">Show ${data.fills.length} fills</summary>
      <div class="table-wrap" style="margin-top:8px"><table>
        <thead><tr><th>Time</th><th>Contract</th><th class="num">Qty</th><th class="num">Price</th><th class="num">Proceeds</th><th class="num">Commission</th><th>Codes</th></tr></thead>
        <tbody>${data.fills.map((f) => `<tr><td>${fmtDateTime(f.tradeTime)}</td><td>${esc(f.description || f.symbol)}</td>
          <td class="num ${cls(f.quantity)}">${num(f.quantity)}</td><td class="num">${num(f.price).toFixed(2)}</td>
          <td class="num">${money(f.proceeds, { cur })}</td><td class="num">${money(f.commission, { cur })}</td><td>${esc([f.openClose, f.notes].filter(Boolean).join(" "))}</td></tr>`).join("")}
        </tbody></table></div>
    </details>

    <form class="journal" id="journal" style="margin-top:24px">
      <section>
        <h2>Before the trade <span class="muted">Why you took it and how you felt</span></h2>
        <div class="field">Setup<div id="setupTags"></div></div>
        <label class="field">Why did you enter? <span class="hint">The signal, the thesis, what you expected to happen.</span>
          <textarea name="entryReason">${esc(n.entryReason)}</textarea></label>
        <div class="form-grid">
          <label class="field">Planned risk <span class="hint">The most you were willing to lose, in dollars. Used for the R multiple.</span>
            <input type="number" step="0.01" min="0" name="plannedRisk" value="${n.plannedRisk ?? ""}"></label>
          <div class="field">Confidence in the setup<div id="confidence"></div></div>
        </div>
        <div class="field">Stress level before entering<div id="stressBefore"></div></div>
        <div class="field">Emotions at entry<div id="emotionTags"></div></div>
      </section>

      <section>
        <h2>Closing the trade</h2>
        <label class="field">Why did you exit? <span class="hint">Target hit, stop hit, time-based exit, panic, news...</span>
          <textarea name="exitReason">${esc(n.exitReason)}</textarea></label>
        <div class="field">Stress level during and after the trade<div id="stressAfter"></div></div>
      </section>

      <section>
        <h2>Review <span class="muted">What to repeat and what to avoid</span></h2>
        <div class="form-grid">
          <div class="field">Did you follow your plan?<div id="followedPlan"></div></div>
          <div class="field">How well did you execute? <div id="executionRating"></div></div>
        </div>
        <div class="field">Mistakes<div id="mistakeTags"></div></div>
        <div class="form-grid">
          <label class="field">What went well<textarea name="wentWell">${esc(n.wentWell)}</textarea></label>
          <label class="field">What went wrong<textarea name="wentWrong">${esc(n.wentWrong)}</textarea></label>
        </div>
        <label class="field">Lesson for next time<textarea name="lesson">${esc(n.lesson)}</textarea></label>
      </section>

      <section>
        <h2>Notes and screenshots</h2>
        <label class="field">Other notes<textarea name="notes">${esc(n.notes)}</textarea></label>
        <div class="shots" id="shots"></div>
        <div class="drop" id="drop">Drop chart screenshots here, paste with Ctrl+V, or <button type="button" class="link" id="pickShot">choose a file</button>
          <input type="file" id="shotFile" accept="image/png,image/jpeg,image/gif,image/webp" multiple hidden></div>
      </section>

      <div class="savebar"><span class="small muted" id="saveState">${data.note ? `Last saved ${fmtDateTime(utc(n.updatedAt))}` : "Not journaled yet"}</span>
        <button type="submit" class="primary">Save journal</button></div>
    </form>
    <dialog id="viewer"><img alt="Screenshot"></dialog>`;

  const state = { ...n };
  const markDirty = () => { dirty = true; $("#saveState").textContent = "Unsaved changes"; };
  const set = (k) => (v) => { state[k] = v; markDirty(); };

  tagPicker($("#setupTags"), "setup", meta.tags.setup, n.setupTags, set("setupTags"));
  tagPicker($("#emotionTags"), "emotion", meta.tags.emotion, n.emotionTags, set("emotionTags"));
  tagPicker($("#mistakeTags"), "mistake", meta.tags.mistake, n.mistakeTags, set("mistakeTags"));
  scale($("#confidence"), n.confidence, 10, ["Low", "High"], set("confidence"));
  scale($("#stressBefore"), n.stressBefore, 10, ["Relaxed", "Very stressed"], set("stressBefore"));
  scale($("#stressAfter"), n.stressAfter, 10, ["Relaxed", "Very stressed"], set("stressAfter"));
  scale($("#executionRating"), n.executionRating, 5, ["Poor", "Excellent"], set("executionRating"));
  yesNo($("#followedPlan"), n.followedPlan, set("followedPlan"));

  const form = $("#journal");
  form.addEventListener("input", (e) => { if (e.target.name) markDirty(); });

  const save = async () => {
    const fd = new FormData(form);
    const body = { ...state };
    for (const k of ["entryReason", "exitReason", "wentWell", "wentWrong", "lesson", "notes"]) body[k] = fd.get(k) || "";
    body.plannedRisk = fd.get("plannedRisk") ? Number(fd.get("plannedRisk")) : null;
    try {
      const saved = await api(`/api/trades/${encodeURIComponent(key)}/note`, { method: "PUT", body: JSON.stringify(body) });
      dirty = false;
      $("#saveState").textContent = `Saved ${fmtDateTime(utc(saved.updatedAt))}`;
      toast("Journal saved");
    } catch (err) { toast(err.message, true); }
  };
  form.addEventListener("submit", (e) => { e.preventDefault(); save(); });
  bindSaveShortcut(save);

  // screenshots
  const drawShots = (list) => {
    $("#shots").innerHTML = list.map((s) => `<div class="shot"><img src="/api/screenshots/${encodeURIComponent(s)}" alt="Trade screenshot" loading="lazy">
      <button type="button" data-del="${esc(s)}">Remove</button></div>`).join("");
    $$("#shots img").forEach((img) => img.onclick = () => { $("#viewer img").src = img.src; $("#viewer").showModal(); });
    $$("#shots [data-del]").forEach((b) => b.onclick = async () => {
      if (!confirm("Remove this screenshot?")) return;
      try { drawShots(await api(`/api/trades/${encodeURIComponent(key)}/screenshots/${encodeURIComponent(b.dataset.del)}`, { method: "DELETE" })); }
      catch (err) { toast(err.message, true); }
    });
  };
  const upload = async (files) => {
    for (const f of files) {
      const fd = new FormData();
      fd.append("file", f, f.name || "screenshot.png");
      try { drawShots(await api(`/api/trades/${encodeURIComponent(key)}/screenshots`, { method: "POST", body: fd })); toast("Screenshot added"); }
      catch (err) { toast(err.message, true); }
    }
  };
  drawShots(n.screenshots || []);
  $("#viewer").onclick = () => $("#viewer").close();
  $("#pickShot").onclick = () => $("#shotFile").click();
  $("#shotFile").onchange = (e) => upload([...e.target.files]);
  const drop = $("#drop");
  drop.addEventListener("dragover", (e) => { e.preventDefault(); drop.classList.add("over"); });
  drop.addEventListener("dragleave", () => drop.classList.remove("over"));
  drop.addEventListener("drop", (e) => { e.preventDefault(); drop.classList.remove("over"); upload([...e.dataTransfer.files].filter((f) => f.type.startsWith("image/"))); });
  form.addEventListener("paste", (e) => {
    const imgs = [...e.clipboardData.items].filter((i) => i.type.startsWith("image/")).map((i) => i.getAsFile());
    if (imgs.length) { e.preventDefault(); upload(imgs); }
  });
}

let saveShortcut = null;
function bindSaveShortcut(fn) { saveShortcut = fn; }
document.addEventListener("keydown", (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s" && saveShortcut && $("#journal, #dailyForm")) {
    e.preventDefault();
    saveShortcut();
  }
});

/* ================= daily journal ================= */

let daylistScroll = 0;

async function renderDaily(view, dateArg) {
  const today = ymd(new Date());
  const date = /^\d{4}-\d{2}-\d{2}$/.test(dateArg) ? dateArg : today;
  const [days, data] = await Promise.all([api("/api/daily?" + qs()), api(`/api/daily/${date}`)]);
  if (!days.some((d) => d.date === today)) days.unshift({ date: today, pnl: 0, trades: 0, hasNote: false });
  if (!days.some((d) => d.date === date)) days.push({ date, pnl: 0, trades: 0, hasNote: !!data.note });
  days.sort((a, b) => b.date.localeCompare(a.date));

  const n = data.note || { mood: null, stress: null, focus: null, sleepHours: null, plan: "", marketNotes: "", review: "", lessons: "" };
  const closedToday = data.trades.filter((x) => x.trade.closeTime && ymd(new Date(x.trade.closeTime)) === date && !x.trade.incompleteHistory);
  const dayPnl = closedToday.reduce((a, x) => a + num(x.trade.netPnl), 0);

  view.innerHTML = `
    <div class="page-head">
      <div><h1>Daily journal</h1><p class="muted">Your mindset, plan and review for each trading day.</p></div>
      <label class="date-field">Open a date <input type="date" id="pickDay" value="${date}"></label>
    </div>
    <div class="daily">
      <nav class="daylist" aria-label="Days">
        ${days.map((d) => `<a href="#/daily/${d.date}" class="${d.date === date ? "on" : ""}">
          <span class="dn">${parseDay(d.date).toLocaleDateString("en-GB", { weekday: "short", day: "numeric", month: "short" })}</span>
          <span class="${cls(d.pnl)}">${d.trades ? money(d.pnl) : ""}</span>
          <span class="dm">${d.trades ? `${d.trades} trade${d.trades === 1 ? "" : "s"}` : "No trades"}${d.hasNote ? ", journal written" : ""}</span></a>`).join("")}
      </nav>
      <form class="journal" id="dailyForm">
        <section>
          <h2>${esc(fmtDayLong(date))} <span class="${cls(dayPnl)}">${closedToday.length ? money(dayPnl) : ""}</span></h2>
          <div class="form-grid">
            <div class="field">Mood<div id="mood"></div></div>
            <div class="field">Stress<div id="stress"></div></div>
            <div class="field">Focus<div id="focus"></div></div>
            <label class="field">Hours of sleep<input type="number" name="sleepHours" min="0" max="24" step="0.5" value="${n.sleepHours ?? ""}" style="max-width:120px"></label>
          </div>
          <label class="field">Plan before the market opens <span class="hint">Levels to watch, trades you are considering, rules for today.</span>
            <textarea name="plan">${esc(n.plan)}</textarea></label>
          <label class="field">Market conditions<textarea name="marketNotes">${esc(n.marketNotes)}</textarea></label>
          <label class="field">End-of-day review <span class="hint">How did you trade? Did you stick to the plan? How did you feel?</span>
            <textarea name="review">${esc(n.review)}</textarea></label>
          <label class="field">Lessons<textarea name="lessons">${esc(n.lessons)}</textarea></label>
        </section>
        <section>
          <h2>Trades on this day</h2>
          ${data.trades.length ? `<div class="table-wrap"><table><thead><tr><th>Symbol</th><th>Strategy</th><th>Opened</th><th>Closed</th><th class="num">Net P&amp;L</th><th>Journal</th></tr></thead><tbody>
            ${data.trades.map(({ trade: t, note }) => `<tr class="click" data-key="${esc(t.tradeKey)}"><td><b>${esc(t.underlying)}</b></td><td>${esc(t.strategy)}</td>
              <td>${fmtDateTime(t.openTime)}</td><td>${fmtDateTime(t.closeTime)}</td><td class="num ${cls(t.netPnl)}">${pnlCell(t)}</td>
              <td>${note ? `<span class="journaled">Journaled</span>` : `<span class="muted small">Add notes</span>`}</td></tr>`).join("")}
          </tbody></table></div>` : `<p class="muted">No trades opened or closed on this day.</p>`}
        </section>
        <div class="savebar"><span class="small muted" id="saveState">${data.note ? `Last saved ${fmtDateTime(utc(n.updatedAt))}` : "Not written yet"}</span>
          <button type="submit" class="primary">Save day</button></div>
      </form>
    </div>`;

  const state = { mood: n.mood, stress: n.stress, focus: n.focus };
  const markDirty = () => { dirty = true; $("#saveState").textContent = "Unsaved changes"; };
  scale($("#mood"), n.mood, 10, ["Low", "Great"], (v) => { state.mood = v; markDirty(); });
  scale($("#stress"), n.stress, 10, ["Relaxed", "Very stressed"], (v) => { state.stress = v; markDirty(); });
  scale($("#focus"), n.focus, 10, ["Scattered", "Sharp"], (v) => { state.focus = v; markDirty(); });
  const form = $("#dailyForm");
  form.addEventListener("input", (e) => { if (e.target.name) markDirty(); });
  const save = async () => {
    const fd = new FormData(form);
    const body = { ...state, plan: fd.get("plan"), marketNotes: fd.get("marketNotes"), review: fd.get("review"), lessons: fd.get("lessons"),
      sleepHours: fd.get("sleepHours") ? Number(fd.get("sleepHours")) : null };
    try {
      const saved = await api(`/api/daily/${date}`, { method: "PUT", body: JSON.stringify(body) });
      dirty = false;
      $("#saveState").textContent = `Saved ${fmtDateTime(utc(saved.updatedAt))}`;
      toast("Day saved");
    } catch (err) { toast(err.message, true); }
  };
  form.addEventListener("submit", (e) => { e.preventDefault(); save(); });
  bindSaveShortcut(save);
  $("#pickDay").onchange = (e) => { if (e.target.value) location.hash = `#/daily/${e.target.value}`; };

  // Keep the day list where it was, and make sure the selected day is in view
  // (it may be off-screen when opened from the date picker or the calendar).
  const list = $(".daylist");
  list.scrollTop = daylistScroll;
  const selected = $("a.on", list);
  if (selected) {
    const lr = list.getBoundingClientRect(), r = selected.getBoundingClientRect();
    if (r.top < lr.top) list.scrollTop -= lr.top - r.top;
    else if (r.bottom > lr.bottom) list.scrollTop += r.bottom - lr.bottom;
  }
  daylistScroll = list.scrollTop;
  list.addEventListener("scroll", () => (daylistScroll = list.scrollTop), { passive: true });
  bindRows(view);
}

/* ================= reports ================= */

let reportTab = "Mistakes";

async function renderReports(view) {
  const groups = await api("/api/reports?" + qs());
  const total = groups[0]?.buckets.reduce((a, b) => a + b.trades, 0) ?? 0;
  if (!total) {
    view.innerHTML = `<h1>Reports</h1><div class="panel empty" style="margin-top:20px"><h2>No closed trades in this date range</h2><p>Widen the date range to see your patterns.</p></div>`;
    return;
  }
  const g = (name) => groups.find((x) => x.name === name)?.buckets ?? [];
  const real = (list) => list.filter((b) => !/^(Not journaled|No .* tagged|Not rated|Not answered)$/.test(b.key));

  const insights = [];
  const mistakes = real(g("Mistakes")).filter((b) => b.netPnl < 0).sort((a, b) => a.netPnl - b.netPnl);
  if (mistakes[0]) insights.push(["bad", "Most costly mistake", `${mistakes[0].key}: ${money(mistakes[0].netPnl)} over ${mistakes[0].trades} trade${mistakes[0].trades === 1 ? "" : "s"}`]);
  const setups = real(g("Setup")).sort((a, b) => b.netPnl - a.netPnl);
  if (setups[0]) insights.push([setups[0].netPnl >= 0 ? "good" : "bad", "Best setup", `${setups[0].key}: ${money(setups[0].netPnl)}, ${pct(setups[0].winRate)} win rate`]);
  const emotions = real(g("Emotions")).sort((a, b) => a.avgPnl - b.avgPnl);
  if (emotions[0] && emotions[0].avgPnl < 0) insights.push(["bad", "Emotion that costs you most", `${emotions[0].key}: ${money(emotions[0].avgPnl)} per trade on average`]);
  const stress = g("Stress before entry");
  const hi = stress.find((b) => b.key.startsWith("High")), lo = stress.find((b) => b.key.startsWith("Low"));
  if (hi && lo) insights.push([hi.avgPnl < lo.avgPnl ? "bad" : "good", "Stress and results", `High stress averages ${money(hi.avgPnl)} per trade, low stress ${money(lo.avgPnl)}`]);
  const plan = g("Followed plan");
  const yes = plan.find((b) => b.key === "Yes"), no = plan.find((b) => b.key === "No");
  if (yes && no) insights.push([yes.avgPnl >= no.avgPnl ? "good" : "bad", "Following your plan", `With plan ${money(yes.avgPnl)} per trade, without ${money(no.avgPnl)}`]);
  const journaled = total - (g("Setup").find((b) => b.key === "Not journaled")?.trades ?? 0);

  if (!groups.some((x) => x.name === reportTab)) reportTab = groups[0].name;

  view.innerHTML = `
    <div class="page-head"><div><h1>Reports</h1><p class="muted">${total} closed trades, ${journaled} journaled. Journal more trades to make the tag and mindset reports more reliable.</p></div></div>
    ${insights.length ? `<div class="insights">${insights.map(([c, k, v]) => `<div class="insight ${c}"><div class="k">${esc(k)}</div><div class="v">${esc(v)}</div></div>`).join("")}</div>`
      : `<div class="notice" style="margin-bottom:20px">Tag your trades with setups, mistakes, emotions and stress levels to see which habits help or hurt your results.</div>`}
    <div class="report-tabs" role="tablist">${groups.map((x) => `<button role="tab" aria-selected="${x.name === reportTab}" class="${x.name === reportTab ? "on" : ""}" data-tab="${esc(x.name)}">${esc(x.name)}</button>`).join("")}</div>
    <div class="panel" id="reportBody"></div>`;

  const draw = () => {
    const buckets = g(reportTab);
    const max = Math.max(1, ...buckets.map((b) => Math.abs(b.netPnl)));
    $("#reportBody").innerHTML = `
      <div class="bars" style="margin-bottom:20px">${buckets.map((b) => `
        <div class="bar-row"><span>${esc(b.key)}</span>
          <div class="bar-track"><div class="axis"></div><div class="bar ${b.netPnl >= 0 ? "pos" : "neg"}" style="width:${(Math.abs(b.netPnl) / max) * 50}%"></div></div>
          <span class="num ${cls(b.netPnl)}" style="text-align:right">${money(b.netPnl)}</span></div>`).join("")}
      </div>
      <div class="table-wrap"><table><thead><tr><th>${esc(reportTab)}</th><th class="num">Trades</th><th class="num">Win rate</th><th class="num">Average</th><th class="num">Net P&amp;L</th></tr></thead>
      <tbody>${buckets.map((b) => `<tr><td>${esc(b.key)}</td><td class="num">${b.trades}</td><td class="num">${pct(b.winRate)}</td>
        <td class="num ${cls(b.avgPnl)}">${money(b.avgPnl)}</td><td class="num ${cls(b.netPnl)}">${money(b.netPnl)}</td></tr>`).join("")}</tbody></table></div>
      ${["Setup", "Mistakes", "Emotions"].includes(reportTab) ? `<p class="small muted" style="margin-top:10px">A trade with several tags counts once in each of its tags, so these rows can add up to more than your total.</p>` : ""}`;
  };
  $$(".report-tabs button").forEach((b) => b.onclick = () => {
    reportTab = b.dataset.tab;
    $$(".report-tabs button").forEach((x) => { x.classList.toggle("on", x === b); x.setAttribute("aria-selected", x === b); });
    draw();
  });
  draw();
}

/* ================= sync and settings ================= */

async function renderSync(view) {
  const [s, logs] = await Promise.all([api("/api/settings"), api("/api/sync/logs")]);
  view.innerHTML = `
    <div class="page-head"><div><h1>Sync and settings</h1><p class="muted">Bring your trades in from Interactive Brokers through the Flex Web Service.</p></div></div>

    <div class="panel">
      <div class="panel-head"><h2>Sync trades</h2></div>
      ${s.hasToken && s.queryId ? "" : `<div class="notice" style="margin-bottom:14px">Add your Flex token and query ID below before syncing. The setup guide at the bottom of this page shows where to find them.</div>`}
      <div class="row">
        <label class="date-field">Last <input type="number" id="syncDays" min="1" max="3650" value="${s.defaultDays}" style="width:90px"> days</label>
        <span class="muted small">or</span>
        <label class="date-field">From <input type="date" id="syncFrom"></label>
        <label class="date-field">To <input type="date" id="syncTo"></label>
        <button class="primary" id="syncBtn" ${s.hasToken && s.queryId ? "" : "disabled"}>Sync now</button>
      </div>
      <p class="small muted" style="margin-top:8px">Already-stored fills are skipped, so syncing the same range twice is safe. Ranges longer than 365 days are fetched in yearly parts. IBKR dates statements in New York time and publishes each day after its end-of-day processing, so the most recent day may only appear on a later sync.</p>
      <div id="syncResult" style="margin-top:12px"></div>
    </div>

    <div class="panel">
      <div class="panel-head"><h2>IBKR connection</h2><span class="small muted">Stored only on this computer, token encrypted</span></div>
      <form id="settingsForm" class="form-grid">
        <label class="field">Flex Web Service token <span class="hint">${s.hasToken ? `Saved (${esc(s.tokenHint)}). Leave empty to keep it.` : "Not set"}</span>
          <input type="password" name="token" autocomplete="off" placeholder="${s.hasToken ? "Enter a new token to replace it" : "Paste your token"}"></label>
        <label class="field">Flex Query ID<span class="hint">The number shown next to your Activity Flex Query.</span>
          <input type="text" name="queryId" value="${esc(s.queryId)}" inputmode="numeric"></label>
        <label class="field">Default sync length (days)<span class="hint">Used when you open this page.</span>
          <input type="number" name="defaultDays" min="1" max="3650" value="${s.defaultDays}"></label>
        <label class="field"><span><input type="checkbox" name="useDateRange" ${s.useDateRange ? "checked" : ""}> Send the date range to IBKR</span>
          <span class="hint">When off, IBKR uses the period saved in the Flex Query itself (for example Last 365 calendar days).</span></label>
        <div class="row"><button class="primary" type="submit">Save connection</button>
          ${s.hasToken ? `<button type="button" id="clearToken">Remove token</button>` : ""}</div>
      </form>
    </div>

    <div class="panel">
      <div class="panel-head"><h2>Import a Flex XML file</h2></div>
      <p class="muted small" style="margin-bottom:10px">If you ran the Flex Query in the IBKR portal and downloaded the XML, you can import it here instead. You can also try the app with <code>samples/sample-flex.xml</code>.</p>
      <div class="row"><input type="file" id="xmlFile" accept=".xml,text/xml"><button id="importBtn">Import file</button></div>
      <div id="importResult" style="margin-top:12px"></div>
    </div>

    <div class="panel">
      <div class="panel-head"><h2>Journal tags</h2><span class="small muted">One tag per line</span></div>
      <form id="tagsForm">
        <div class="form-grid">
          <label class="field">Setups<textarea name="setup" rows="9" style="font-family:var(--sans);font-size:.9rem">${esc(s.tags.setup.join("\n"))}</textarea></label>
          <label class="field">Mistakes<textarea name="mistake" rows="9" style="font-family:var(--sans);font-size:.9rem">${esc(s.tags.mistake.join("\n"))}</textarea></label>
          <label class="field">Emotions<textarea name="emotion" rows="9" style="font-family:var(--sans);font-size:.9rem">${esc(s.tags.emotion.join("\n"))}</textarea></label>
        </div>
        <div class="row" style="margin-top:12px"><button class="primary" type="submit">Save tags</button>
          <span class="small muted">Tags you already used on trades stay available even if you remove them here.</span></div>
      </form>
    </div>

    <div class="panel">
      <div class="panel-head"><h2>Sync history</h2><button id="rebuildBtn">Rebuild trades from stored fills</button></div>
      ${logs.length ? `<div class="table-wrap"><table><thead><tr><th>Started</th><th>Source</th><th>Range</th><th>Status</th><th class="num">Fills found</th><th class="num">New</th><th>Details</th></tr></thead><tbody>
        ${logs.map((l) => `<tr><td>${fmtDateTime(l.startedAt)}</td><td>${esc(l.source)}</td><td>${esc(l.range)}</td><td class="status-${esc(l.status)}">${esc(l.status)}</td>
          <td class="num">${l.executionsFound}</td><td class="num">${l.executionsAdded}</td><td class="wrap">${esc(l.message)}</td></tr>`).join("")}
      </tbody></table></div>` : `<p class="muted">No syncs yet.</p>`}
    </div>

    <div class="panel">
      <div class="panel-head"><h2>Setup guide: connect IBKR</h2></div>
      <ol class="steps">
        <li>Log in to the IBKR Portal and open <b>Performance &amp; Reports</b>, then <b>Flex Queries</b>. Menu names can differ slightly between portal versions.</li>
        <li>Create a new <b>Activity Flex Query</b>, for example named "Trade Journal".</li>
        <li>In <b>Sections</b>, select <b>Trades</b>. In its options choose <b>Execution</b> and select all fields. At minimum the app needs: Account ID, Currency, Asset Class, Symbol, Description, Conid, Underlying Symbol, Multiplier, Strike, Expiry, Put/Call, Trade ID, IB Order ID, Date/Time, Quantity, Trade Price, Proceeds, IB Commission, Open/Close Indicator, Notes/Codes, Buy/Sell, Level of Detail and Transaction Type.</li>
        <li>In the delivery settings, set <b>Format</b> to <b>XML</b> and <b>Period</b> to <b>Last 365 Calendar Days</b>. Keep the default date format (yyyyMMdd), time format (HHmmss) and the semicolon separator.</li>
        <li>Save the query and copy its <b>Query ID</b>.</li>
        <li>Open <b>Settings</b>, then <b>Flex Web Service Configuration</b> (under Reporting). Turn the service on and generate a <b>token</b>. Note its expiry date; you will need a new token when it runs out.</li>
        <li>Paste the token and Query ID into <b>IBKR connection</b> above, save, and select <b>Sync now</b>.</li>
      </ol>
      <p class="small muted" style="margin-top:12px">If IBKR rejects the date range, turn off "Send the date range to IBKR" and the period saved in the query is used instead.</p>
    </div>`;

  $("#syncBtn").onclick = async () => {
    const btn = $("#syncBtn");
    const from = $("#syncFrom").value, to = $("#syncTo").value;
    const body = from || to ? { from: from || null, to: to || null } : { days: Number($("#syncDays").value) || s.defaultDays };
    btn.disabled = true;
    btn.textContent = "Syncing…";
    $("#syncResult").innerHTML = `<div class="notice">Waiting for IBKR to generate the statement. This usually takes 10 to 60 seconds.</div>`;
    try {
      const log = await api("/api/sync", { method: "POST", body: JSON.stringify(body) });
      $("#syncResult").innerHTML = `<div class="notice ${log.status === "Success" ? "good" : "bad"}">${esc(log.message)}</div>`;
      refreshLastSync();
    } catch (err) {
      $("#syncResult").innerHTML = `<div class="notice bad">${esc(err.message)}</div>`;
    }
    btn.disabled = false;
    btn.textContent = "Sync now";
  };

  $("#settingsForm").onsubmit = async (e) => {
    e.preventDefault();
    const fd = new FormData(e.target);
    const body = { queryId: fd.get("queryId"), defaultDays: Number(fd.get("defaultDays")) || 30, useDateRange: fd.get("useDateRange") === "on" };
    if (fd.get("token")) body.token = fd.get("token");
    try { await api("/api/settings", { method: "PUT", body: JSON.stringify(body) }); toast("Connection saved"); render(); }
    catch (err) { toast(err.message, true); }
  };
  const clear = $("#clearToken");
  if (clear) clear.onclick = async () => {
    if (!confirm("Remove the saved Flex token from this computer?")) return;
    await api("/api/settings", { method: "PUT", body: JSON.stringify({ token: "" }) });
    toast("Token removed"); render();
  };

  $("#importBtn").onclick = async () => {
    const f = $("#xmlFile").files[0];
    if (!f) { toast("Choose an XML file first", true); return; }
    const fd = new FormData();
    fd.append("file", f);
    try {
      const log = await api("/api/import", { method: "POST", body: fd });
      $("#importResult").innerHTML = `<div class="notice ${log.status === "Success" ? "good" : "bad"}">${esc(log.message)}</div>`;
      refreshLastSync();
    } catch (err) { $("#importResult").innerHTML = `<div class="notice bad">${esc(err.message)}</div>`; }
  };

  $("#tagsForm").onsubmit = async (e) => {
    e.preventDefault();
    const fd = new FormData(e.target);
    const lines = (k) => String(fd.get(k) || "").split("\n").map((x) => x.trim()).filter(Boolean);
    try {
      await api("/api/settings", { method: "PUT", body: JSON.stringify({ tags: { setup: lines("setup"), mistake: lines("mistake"), emotion: lines("emotion") } }) });
      toast("Tags saved");
    } catch (err) { toast(err.message, true); }
  };

  $("#rebuildBtn").onclick = async () => {
    try { const r = await api("/api/rebuild", { method: "POST" }); toast(r.message); }
    catch (err) { toast(err.message, true); }
  };
}

/* ================= start ================= */

initFilterBar();
refreshLastSync();
render();
