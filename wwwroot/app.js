"use strict";
const state = {
  familyId: null,
  draftRevision: null,
  versions: [],
  runs: [],
  currentRunId: null
};

const $ = (id) => document.getElementById(id);

async function api(method, url, body) {
  const opts = { method, headers: {} };
  if (body !== undefined) {
    opts.headers["Content-Type"] = "application/json";
    opts.body = JSON.stringify(body);
  }
  const res = await fetch(url, opts);
  let data = null;
  try { data = await res.json(); } catch { /* no body */ }
  if (!res.ok) {
    const err = new Error((data && (data.error || data.code)) || ("HTTP " + res.status));
    err.code = data && data.code;
    err.payload = data;
    throw err;
  }
  return data;
}

function setMsg(id, text, kind) {
  const el = $(id);
  el.textContent = text || "";
  el.className = "msg" + (kind ? " " + kind : "");
}

// ---------- families ----------
async function loadFamilies(selectFirst) {
  const fams = await api("GET", "/api/families");
  const ul = $("familyList");
  ul.innerHTML = "";
  fams.forEach((f) => {
    const li = document.createElement("li");
    li.className = f.id === state.familyId ? "active" : "";
    li.innerHTML = `<div>${f.name}</div><span class="fid">${f.id}</span>`;
    li.onclick = () => selectFamily(f.id);
    ul.appendChild(li);
  });
  if (selectFirst && !state.familyId && fams.length) await selectFamily(fams[0].id);
}

$("createFamilyBtn").onclick = async () => {
  const name = $("newFamilyName").value.trim();
  if (!name) return;
  const f = await api("POST", "/api/families", { name });
  $("newFamilyName").value = "";
  await selectFamily(f.id);
};

async function selectFamily(id) {
  state.familyId = id;
  state.draftRevision = null;
  state.versions = [];
  await loadFamilies(false);
  await Promise.all([loadDraft(), loadVersions(), loadSamplesView(), loadRuns()]);
}

// ---------- tabs ----------
document.querySelectorAll(".tab").forEach((t) => {
  t.onclick = () => {
    document.querySelectorAll(".tab").forEach((x) => x.classList.remove("active"));
    document.querySelectorAll(".panel").forEach((x) => x.classList.add("hidden"));
    t.classList.add("active");
    $("tab-" + t.dataset.tab).classList.remove("hidden");
  };
});

// ---------- draft / versions ----------
async function loadDraft() {
  try {
    const d = await api("GET", `/api/families/${state.familyId}/draft`);
    state.draftRevision = d.revision;
    $("draftRev").textContent = "revision " + d.revision;
    $("draftEditor").value = JSON.stringify(d.schema, null, 2);
    setMsg("draftMsg", "");
  } catch (e) {
    state.draftRevision = null;
    $("draftRev").textContent = "无 draft";
  }
}

$("saveDraftBtn").onclick = async () => {
  try {
    const r = await api("PUT", `/api/families/${state.familyId}/draft`, {
      schemaJson: $("draftEditor").value,
      expectedRevision: state.draftRevision
    });
    state.draftRevision = r.revision;
    $("draftRev").textContent = "revision " + r.revision;
    setMsg("draftMsg", "draft 已保存（revision " + r.revision + "）", "ok");
  } catch (e) {
    if (e.code === "DRAFT_REVISION_CONFLICT") {
      setMsg("draftMsg", e.message + " 已重新加载服务器版本。", "err");
      await loadDraft();
    } else setMsg("draftMsg", e.message, "err");
  }
};

$("publishBtn").onclick = async () => {
  const version = $("publishVersion").value.trim();
  if (!version) return setMsg("draftMsg", "请填写版本号", "err");
  try {
    await api("POST", `/api/families/${state.familyId}/publish`, {
      version,
      expectedDraftRevision: state.draftRevision
    });
    $("publishVersion").value = "";
    setMsg("draftMsg", `版本 ${version} 已发布且冻结`, "ok");
    await loadVersions();
    await loadSamplesView();
  } catch (e) {
    if (e.code === "DRAFT_REVISION_CONFLICT") { setMsg("draftMsg", e.message, "err"); await loadDraft(); }
    else setMsg("draftMsg", e.message, "err");
  }
};

async function loadVersions() {
  state.versions = await api("GET", `/api/families/${state.familyId}/versions`);
  const wrap = $("versionList");
  wrap.innerHTML = "";
  const sel = $("sampleVersion");
  sel.innerHTML = "";
  state.versions.forEach((v) => {
    const card = document.createElement("div");
    card.className = "card";
    card.innerHTML = `<div class="v-head">
        <strong>${v.version}</strong>
        <span class="pill ${v.unknownPolicy}">unknown: ${v.unknownPolicy}</span>
      </div>
      <div class="muted">revision ${v.revision} · sha ${v.schemaSha.slice(0, 16)}… · ${v.publishedAt.slice(0, 19).replace("T", " ")}</div>
      <pre class="schema">${escapeHtml(JSON.stringify(v.schema, null, 2))}</pre>`;
    wrap.appendChild(card);
    const o = document.createElement("option");
    o.value = v.version; o.textContent = v.version;
    sel.appendChild(o);
  });
}

// ---------- samples ----------
$("sampleVersion")?.addEventListener("change", loadOneSample);
async function loadOneSample() {
  const v = $("sampleVersion").value;
  if (!v) return;
  try {
    const s = await api("GET", `/api/families/${state.familyId}/versions/${encodeURIComponent(v)}/sample`);
    $("sampleEditor").value = JSON.stringify(s.redactedSample, null, 2);
    setMsg("sampleMsg", `sha ${s.sha.slice(0, 16)}… · ${s.byteLength} 字节 · secret ${s.secretCount} 个（已 token 化）`);
  } catch { $("sampleEditor").value = ""; setMsg("sampleMsg", ""); }
}

$("uploadSampleBtn").onclick = async () => {
  const v = $("sampleVersion").value;
  if (!v) return setMsg("sampleMsg", "请先发布并选择版本", "err");
  try {
    const s = await api("PUT", `/api/families/${state.familyId}/versions/${encodeURIComponent(v)}/sample`, {
      sampleJson: $("sampleEditor").value
    });
    $("sampleEditor").value = JSON.stringify(s.redactedSample, null, 2);
    setMsg("sampleMsg", "已保存。" + s.note, "ok");
    await loadSamplesView();
  } catch (e) { setMsg("sampleMsg", e.message, "err"); }
};

async function loadSamplesView() {
  const wrap = $("sampleList");
  if (!wrap) return;
  wrap.innerHTML = "";
  for (const v of state.versions) {
    try {
      const s = await api("GET", `/api/families/${state.familyId}/versions/${encodeURIComponent(v.version)}/sample`);
      const d = document.createElement("div");
      d.className = "card";
      d.innerHTML = `<strong>${v.version}</strong>
        <div class="muted">sha ${s.sha.slice(0, 16)}… · ${s.byteLength} 字节 · secret ${s.secretCount}（仅存在性+摘要）</div>`;
      wrap.appendChild(d);
    } catch { /* no sample yet */ }
  }
}

function escapeHtml(s) {
  return s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
}

// ---------- runs ----------
$("createRunBtn").onclick = async () => {
  const key = $("idempotencyKey").value.trim() || ("run-" + Date.now());
  const policy = $("policyPreserve").checked ? "preserve" : "drop";
  try {
    const r = await api("POST", `/api/families/${state.familyId}/runs`, {
      idempotencyKey: key, unknownPolicy: policy
    });
    state.currentRunId = r.runId;
    setMsg("runMsg", `${r.outcome === "reused" ? "复用原运行" : "已创建运行"} #${r.runId} · 模式 ${policy}（该选择进入矩阵身份）`, "ok");
    await loadRuns();
  } catch (e) {
    if (e.code === "IDEMPOTENCY_CONTENT_CONFLICT")
      setMsg("runMsg", "冲突：幂等键相同但冻结内容不同（schema/样例/unknownPolicy），已拒绝。", "err");
    else setMsg("runMsg", e.message, "err");
  }
};

$("refreshRunBtn").onclick = loadRuns;

async function loadRuns() {
  if (!state.familyId) return;
  state.runs = await api("GET", `/api/families/${state.familyId}/runs`);
  const wrap = $("runList");
  wrap.innerHTML = "";
  for (const r of state.runs) {
    const detail = await api("GET", `/api/families/${state.familyId}/runs/${r.runId}`);
    wrap.appendChild(renderRun(detail));
  }
}

function renderRun(run) {
  const card = document.createElement("div");
  card.className = "card run-card";
  const versions = [...new Set(run.cells.map((c) => c.writerVersion))];
  let html = `<div class="v-head"><strong>运行 #${run.runId}</strong>
      <span class="pill ${run.unknownPolicy}">模式：${run.unknownPolicy === "preserve" ? "保留未知字段" : "丢弃未知字段"}</span></div>
    <div class="run-meta">
      <span>幂等键：${run.idempotencyKey}</span>
      <span>状态：${run.status}</span>
      <span>进度：${run.cursor}/${run.total}</span>
      <span class="muted">contentHash ${run.contentHash.slice(0, 16)}…</span>
    </div>
    <table class="matrix"><thead><tr><th>writer \\ reader</th>`;
  versions.forEach((rv) => { html += `<th>${rv}</th>`; });
  html += "</tr></thead><tbody>";
  versions.forEach((wv) => {
    html += `<tr><th>${wv}</th>`;
    versions.forEach((rv) => {
      const c = run.cells.find((x) => x.writerVersion === wv && x.readerVersion === rv);
      html += `<td>${cellHtml(c, run.runId)}</td>`;
    });
    html += "</tr>";
  });
  html += "</tbody></table>";
  card.innerHTML = html;

  card.querySelectorAll(".cell[data-cell]").forEach((el) => {
    const [w, rr] = el.dataset.cell.split("/");
    el.onclick = () => openCell(run.runId, w, rr);
  });
  return card;
}

function cellHtml(c, runId) {
  if (!c || c.status === "pending")
    return `<div class="cell pending"><div class="ctitle">…</div><div class="muted">排队中</div></div>`;
  const tags = [];
  tags.push(`<span class="tag policy">${c.effectiveUnknownPolicy}</span>`);
  tags.push(c.reversible
    ? `<span class="tag rev">可逆</span>`
    : `<span class="tag lost">不可逆</span>`);
  (c.errorCodes || []).forEach((e) => { if (hardError(e)) tags.push(`<span class="tag err">${e}</span>`); });
  return `<div class="cell ${c.status}" data-cell="${c.writerVersion}/${c.readerVersion}">
      <div class="ctitle">${c.writerVersion} → ${c.readerVersion}</div>
      <div class="tags">${tags.join("")}</div>
    </div>`;
}

function hardError(code) {
  return ["REQUIRED_MISSING", "TYPE_MISMATCH", "ENUM_UNKNOWN_VALUE", "UNION_UNKNOWN_BRANCH",
    "INVALID_JSON", "ROOT_NOT_OBJECT", "SECRET_NOT_TOKENIZED", "CELL_COMPUTE_FAULT"].includes(code);
}

// ---------- cell drilldown ----------
async function openCell(runId, writer, reader) {
  const data = await api("GET",
    `/api/families/${state.familyId}/runs/${runId}/cells/${encodeURIComponent(writer)}/${encodeURIComponent(reader)}`);
  const c = data.result;
  $("modalTitle").textContent = `${writer}（writer）→ ${reader}（reader）`;
  const body = $("modalBody");

  const errors = (c.errorCodes || []).map((e) => `<code class="code">${e}</code>`).join(" ") || "<span class='muted'>无错误代码</span>";
  let html = `<div class="section"><h4>结论</h4>
    <dl class="kv">
      <dt>状态</dt><dd>${c.status} · ${c.compatible ? "兼容" : "存在硬错误"} · ${c.reversible ? "<b style='color:var(--rev)'>可逆</b>" : "<b style='color:var(--lost)'>不可逆</b>"}</dd>
      <dt>未知字段模式</dt><dd>${c.effectiveUnknownPolicy === "preserve" ? "保留未知字段（进入矩阵身份）" : "丢弃未知字段（进入矩阵身份）"}</dd>
      <dt>可逆性解释</dt><dd>${escapeHtml(c.reversibilityNote || "")}</dd>
      <dt>错误代码</dt><dd>${errors}</dd>
      <dt>默认值使用</dt><dd>${(c.defaultsUsed || []).map(escapeHtml).join(", ") || "—"}</dd>
      <dt>编码摘要</dt><dd>${c.encodeByteLength} 字节 · sha ${c.encodeSha256}<br/>再写出：${c.reEncodedByteLength} 字节 · sha ${c.reEncodedSha256}</dd>
    </dl></div>`;

  html += `<div class="section"><h4>演变解释（不只是布尔值）</h4><table class="detail">
    <tr><th>路径</th><th>类别</th><th>代码</th><th>解释</th></tr>`;
  (c.changes || []).forEach((ch) => {
    html += `<tr><td>${escapeHtml(ch.path)}</td><td>${ch.kind}</td><td><code class="code">${ch.code}</code></td><td>${escapeHtml(ch.explanation)}</td></tr>`;
  });
  html += `</table></div>`;

  html += `<div class="section"><h4>解码字段 & 字段来源</h4><table class="detail">
    <tr><th>路径</th><th>来源</th><th>经由</th><th>说明</th></tr>`;
  (c.fieldSources || []).forEach((s) => {
    html += `<tr><td>${escapeHtml(s.path)}</td><td>${s.origin}</td><td>${escapeHtml(s.viaName || "—")}</td><td>${escapeHtml(s.note || "")}</td></tr>`;
  });
  html += `</table></div>`;

  html += `<div class="section"><h4>未知字段 sidecar</h4>${
    (c.unknownSidecar || []).length === 0 ? "<span class='muted'>无未知字段</span>" :
    "<table class='detail'><tr><th>路径</th><th>字段</th><th>secretLike</th></tr>" +
    c.unknownSidecar.map((u) => `<tr><td>${escapeHtml(u.path)}</td><td>${escapeHtml(u.field)}</td><td>${u.secretLike ? "是" : ""}</td></tr>`).join("") +
    "</table>"
  }</div>`;

  html += `<div class="section"><h4>Secret（仅存在性 + 摘要）</h4>${
    (c.secrets || []).map((sc) => `<div>${escapeHtml(sc.path)} · present=${sc.present} · digest=${sc.digest || "—"}</div>`).join("") || "<span class='muted'>无</span>"
  }</div>`;

  html += `<div class="section"><h4>编码字节（点击字段高亮其字节区间）</h4>`;
  html += renderBytes(c.encodedJson, c.encodeFields, "encBytes");
  html += `</div>`;

  if (c.reEncodedJson) {
    html += `<div class="section"><h4>中间 reader 再写出字节</h4>`;
    html += renderBytes(c.reEncodedJson, [], "reBytes");
    html += `</div>`;
  }

  body.innerHTML = html;
  body.querySelectorAll("[data-off]").forEach((el) => {
    el.onclick = () => {
      const off = +el.dataset.off, len = +el.dataset.len;
      const target = document.getElementById(el.dataset.target);
      highlightBytes(target, off, len);
    };
  });
  $("modal").classList.remove("hidden");
}

function renderBytes(json, fields, targetId) {
  const fieldRows = fields.map((f) =>
    `<div data-off="${f.byteOffset}" data-len="${f.byteLength}" data-target="${targetId}"
      style="cursor:pointer">${escapeHtml(f.path)} · [${f.byteOffset}, ${f.byteOffset + f.byteLength}) · ${f.source}${f.secret ? " · secret 已脱敏" : ""}</div>`
  ).join("");
  return `<div id="${targetId}" class="bytes">${escapeHtml(json)}</div>
    <div class="detail" style="margin-top:8px;font-size:11px">${fieldRows}</div>`;
}

function highlightBytes(el, off, len) {
  const text = el.textContent;
  const before = text.slice(0, off), mid = text.slice(off, off + len), after = text.slice(off + len);
  el.innerHTML = `${escapeHtml(before)}<mark>${escapeHtml(mid)}</mark>${escapeHtml(after)}`;
}

$("modalClose").onclick = () => $("modal").classList.add("hidden");
$("modal").onclick = (e) => { if (e.target.id === "modal") $("modal").classList.add("hidden"); };

// ---------- export ----------
$("exportBtn").onclick = async () => {
  const data = await api("GET", `/api/families/${state.familyId}/export`);
  $("exportView").value = JSON.stringify(data, null, 2);
};

// ---------- boot ----------
(async function boot() {
  await loadFamilies(true);
})();

// poll running matrices
setInterval(() => {
  if (state.runs.some((r) => r.status !== "complete")) loadRuns();
}, 1500);
