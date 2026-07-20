// app.js — UI wiring for the hardpoint viewer.
//
// Loads a GLB (from the served library list, a file picker, or drag-and-drop),
// parses it with GLTF, drives the WebGL Viewer, and builds the sidebar:
// mesh stats, a per-kind legend with visibility toggles, a clickable
// hardpoint table, and a projected on-canvas name overlay.
(function () {
  "use strict";

  // HardpointKind palette. Cyan (#37E0FF) is reserved chrome, so it maps to
  // DockingExit only; team-agnostic accents everywhere else.
  const KIND_COLORS = {
    Weapon: "#FF5A6A",
    Turret: "#FF7AC6",
    MainEngine: "#FF9D4D",
    Booster: "#FFB347",
    Thruster: "#B98BFF",
    DockingEntrance: "#4DFFA6",
    DockingExit: "#37E0FF",
    Cockpit: "#9FD6FF",
    Light: "#FFEE99",
  };
  const KIND_ORDER = ["Weapon", "Turret", "MainEngine", "Booster", "Thruster", "DockingEntrance", "DockingExit", "Cockpit", "Light"];
  const DEFAULT_COLOR = "#7FA6C8";

  const $ = (sel) => document.querySelector(sel);
  const el = (tag, cls, text) => { const n = document.createElement(tag); if (cls) n.className = cls; if (text != null) n.textContent = text; return n; };
  const hexToRgb = (hex) => [parseInt(hex.slice(1, 3), 16) / 255, parseInt(hex.slice(3, 5), 16) / 255, parseInt(hex.slice(5, 7), 16) / 255];
  const kindColor = (k) => KIND_COLORS[k] || DEFAULT_COLOR;
  const fmt = (v) => (v >= 0 ? "+" : "") + v.toFixed(3);

  let canvas = $("#gl");
  const overlay = $("#overlay");
  const octx = overlay.getContext("2d");

  let viewer = null;
  let doc = null;
  const visibleKinds = new Set();
  let selectedRow = null;

  // ---- loading -------------------------------------------------------------

  function setStatus(text, tone) {
    const pill = $("#status");
    pill.textContent = text;
    pill.dataset.tone = tone || "idle";
  }

  async function loadArrayBuffer(name, buffer) {
    try {
      const parsed = GLTF.parseGLTFDocument(buffer);
      doc = parsed;
      if (viewer) {
        // A lost WebGL context can't be re-acquired on the same canvas, so
        // swap in a fresh element for every load.
        viewer.dispose();
        const fresh = canvas.cloneNode(false);
        canvas.replaceWith(fresh);
        canvas = fresh;
      }
      visibleKinds.clear();
      for (const hp of parsed.hardpoints) visibleKinds.add(hp.kind);
      viewer = Viewer.createViewer(canvas, parsed, { colorFor: (k) => hexToRgb(kindColor(k)), onFrame: drawOverlay });
      viewer.setVisibleKinds(visibleKinds);
      $("#empty").style.display = "none";
      $("#viewport").classList.add("loaded");
      buildSidebar(name, parsed);
      setStatus(name, "ok");
    } catch (err) {
      console.error(err);
      setStatus("Failed: " + err.message, "danger");
    }
  }

  function loadFile(file) {
    ++loadToken; // supersede any in-flight library fetch
    setStatus("Loading " + file.name + "…", "idle");
    const reader = new FileReader();
    reader.onload = () => loadArrayBuffer(file.name, reader.result);
    reader.onerror = () => setStatus("Could not read file", "danger");
    reader.readAsArrayBuffer(file);
  }

  async function loadFromLibrary(entry) {
    const token = ++loadToken;
    setStatus("Loading " + entry.name + "…", "idle");
    try {
      const res = await fetch("asset/" + entry.path.split("/").map(encodeURIComponent).join("/"));
      if (token !== loadToken) return; // superseded by a newer selection
      if (!res.ok) throw new Error("HTTP " + res.status);
      const ab = await res.arrayBuffer();
      if (token !== loadToken) return;
      await loadArrayBuffer(entry.name, ab);
    } catch (err) {
      if (token === loadToken) setStatus("Failed: " + err.message, "danger");
    }
  }

  let libraryEntries = [];
  let libraryOk = false;
  let loadedPath = null;
  let currentMatches = []; // entries currently shown (post-filter), in row order
  let loadToken = 0;       // monotonic guard: only the newest fetch applies
  let loadTimer = null;    // debounces arrow-key auditioning

  async function refreshLibrary() {
    try {
      const res = await fetch("list");
      if (!res.ok) throw new Error("HTTP " + res.status);
      const data = await res.json();
      $("#library-root").textContent = data.root;
      $("#crumb").textContent = data.root;
      libraryEntries = data.files;
      libraryOk = true;
    } catch (err) {
      libraryEntries = [];
      libraryOk = false;
    }
    renderLibrary();
  }

  function renderLibrary() {
    const list = $("#library");
    const query = $("#lib-search").value.trim().toLowerCase();
    list.innerHTML = "";
    if (!libraryOk) {
      list.appendChild(el("div", "hint", "Library unavailable (open a file instead)."));
      $("#lib-count").textContent = "";
      return;
    }
    currentMatches = query
      ? libraryEntries.filter((e) => e.path.toLowerCase().includes(query))
      : libraryEntries;
    if (!libraryEntries.length) {
      list.appendChild(el("div", "hint", "No .glb files here."));
    } else if (!currentMatches.length) {
      list.appendChild(el("div", "hint", "No models match “" + query + "”."));
    }
    currentMatches.forEach((entry, i) => {
      const row = el("button", "lib-row");
      if (entry.path === loadedPath) row.classList.add("active");
      row.title = entry.path;
      row.appendChild(el("span", "lib-name", entry.name));
      row.appendChild(el("span", "lib-size", (entry.size / 1024).toFixed(0) + " KB"));
      row.addEventListener("click", () => selectIndex(i, true));
      list.appendChild(row);
    });
    $("#lib-count").textContent = query ? currentMatches.length + "/" + libraryEntries.length : String(libraryEntries.length);
  }

  // Highlight + audition the entry at `idx` in the filtered list. `immediate`
  // loads now (clicks); arrow-key steps debounce so holding the key skims
  // without a fetch per row, while the highlight tracks instantly.
  function selectIndex(idx, immediate) {
    if (idx < 0 || idx >= currentMatches.length) return;
    const entry = currentMatches[idx];
    loadedPath = entry.path;
    const rows = $("#library").querySelectorAll(".lib-row");
    rows.forEach((r, i) => r.classList.toggle("active", i === idx));
    if (rows[idx]) rows[idx].scrollIntoView({ block: "nearest" });
    if (loadTimer) { clearTimeout(loadTimer); loadTimer = null; }
    if (immediate) loadFromLibrary(entry);
    else loadTimer = setTimeout(() => { loadTimer = null; loadFromLibrary(entry); }, 90);
  }

  function selectRelative(delta) {
    if (!currentMatches.length) return;
    let idx = currentMatches.findIndex((e) => e.path === loadedPath);
    if (idx < 0) idx = delta > 0 ? 0 : currentMatches.length - 1;
    else idx = Math.max(0, Math.min(currentMatches.length - 1, idx + delta));
    selectIndex(idx, false);
  }

  // ---- sidebar -------------------------------------------------------------

  function buildSidebar(name, parsed) {
    const s = parsed.stats;
    const b = parsed.meshAabb;
    const statTable = $("#stats");
    statTable.innerHTML = "";
    const rows = [
      ["file", name],
      ["hardpoints", String(parsed.hardpoints.length)],
      ["vertices", s.vertexCount.toLocaleString()],
      ["triangles", s.triangleCount.toLocaleString()],
      ["longest axis", s.longestAxis.toFixed(4)],
      ["AABB min", `${fmt(b.min[0])} ${fmt(b.min[1])} ${fmt(b.min[2])}`],
      ["AABB max", `${fmt(b.max[0])} ${fmt(b.max[1])} ${fmt(b.max[2])}`],
    ];
    for (const [k, v] of rows) {
      const r = el("div", "stat");
      r.appendChild(el("span", "stat-k", k));
      r.appendChild(el("span", "stat-v", v));
      statTable.appendChild(r);
    }

    // legend
    const legend = $("#legend");
    legend.innerHTML = "";
    const counts = {};
    for (const hp of parsed.hardpoints) counts[hp.kind] = (counts[hp.kind] || 0) + 1;
    const kinds = Object.keys(counts).sort((a, b2) => {
      const ia = KIND_ORDER.indexOf(a), ib = KIND_ORDER.indexOf(b2);
      return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib);
    });
    for (const kind of kinds) {
      const row = el("label", "legend-row");
      const cb = el("input");
      cb.type = "checkbox";
      cb.checked = true;
      cb.addEventListener("change", () => {
        if (cb.checked) visibleKinds.add(kind); else visibleKinds.delete(kind);
        viewer.setVisibleKinds(visibleKinds);
        drawOverlay();
      });
      const sw = el("span", "swatch");
      sw.style.background = kindColor(kind);
      row.appendChild(cb);
      row.appendChild(sw);
      row.appendChild(el("span", "legend-name", kind));
      row.appendChild(el("span", "legend-count", String(counts[kind])));
      legend.appendChild(row);
    }

    // table
    const tbody = $("#hp-body");
    tbody.innerHTML = "";
    selectedRow = null;
    for (const hp of parsed.hardpoints) {
      const tr = el("tr", "hp-row");
      const nameCell = el("td");
      const dot = el("span", "dot");
      dot.style.background = kindColor(hp.kind);
      nameCell.appendChild(dot);
      nameCell.appendChild(el("span", null, hp.name));
      tr.appendChild(nameCell);
      tr.appendChild(el("td", "num", `${fmt(hp.pos[0])} ${fmt(hp.pos[1])} ${fmt(hp.pos[2])}`));
      tr.appendChild(el("td", "num", `${fmt(hp.fwd[0])} ${fmt(hp.fwd[1])} ${fmt(hp.fwd[2])}`));
      tr.addEventListener("click", () => {
        if (selectedRow) selectedRow.classList.remove("selected");
        tr.classList.add("selected");
        selectedRow = tr;
        if (!visibleKinds.has(hp.kind)) return;
        viewer.focus(hp.kind, hp.index);
      });
      tbody.appendChild(tr);
    }
  }

  // ---- projected name overlay ---------------------------------------------

  function drawOverlay() {
    if (!viewer || !doc) return;
    const w = overlay.clientWidth, h = overlay.clientHeight;
    if (overlay.width !== w || overlay.height !== h) { overlay.width = w; overlay.height = h; }
    octx.clearRect(0, 0, w, h);
    octx.font = "11px 'JetBrains Mono', monospace";
    octx.textBaseline = "middle";
    for (const hp of doc.hardpoints) {
      if (!visibleKinds.has(hp.kind)) continue;
      const p = viewer.project(hp.pos);
      if (!p.visible || p.x < -40 || p.x > w + 40 || p.y < -20 || p.y > h + 20) continue;
      const label = hp.name.replace(/^HP_/, "");
      const tw = octx.measureText(label).width;
      const lx = p.x + 9, ly = p.y - 9;
      octx.fillStyle = "rgba(5,7,15,0.72)";
      octx.fillRect(lx - 3, ly - 8, tw + 6, 16);
      octx.fillStyle = kindColor(hp.kind);
      octx.fillText(label, lx, ly);
    }
  }

  // ---- input plumbing ------------------------------------------------------

  $("#open-file").addEventListener("click", () => $("#file-input").click());
  $("#file-input").addEventListener("change", (e) => { if (e.target.files[0]) loadFile(e.target.files[0]); });
  $("#refresh").addEventListener("click", refreshLibrary);
  $("#reset-view").addEventListener("click", () => viewer && viewer.resetView());
  $("#lib-search").addEventListener("input", renderLibrary);
  $("#lib-search").addEventListener("keydown", (e) => {
    if (e.key === "Escape") { e.target.value = ""; renderLibrary(); }
  });
  // Global so stepping works regardless of what has focus (clicking a row or
  // the 3D canvas can leave the list unfocused). The search box is the only
  // text field, and arrow-stepping is wanted there too.
  document.addEventListener("keydown", (e) => {
    if (e.key !== "ArrowUp" && e.key !== "ArrowDown") return;
    if (!currentMatches.length) return;
    if (e.metaKey || e.ctrlKey || e.altKey) return;
    const ae = document.activeElement;
    if (ae && ae.tagName === "TEXTAREA") return;
    e.preventDefault();
    selectRelative(e.key === "ArrowDown" ? 1 : -1);
  });

  const vp = $("#viewport");
  vp.addEventListener("dragover", (e) => { e.preventDefault(); vp.classList.add("drop"); });
  vp.addEventListener("dragleave", () => vp.classList.remove("drop"));
  vp.addEventListener("drop", (e) => {
    e.preventDefault();
    vp.classList.remove("drop");
    const f = e.dataTransfer.files[0];
    if (f && /\.glb$/i.test(f.name)) loadFile(f);
    else if (f) setStatus("Not a .glb file", "warn");
  });

  refreshLibrary();
})();
