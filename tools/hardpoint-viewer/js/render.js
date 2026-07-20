// render.js — WebGL1 viewer for a parsed glTF document (window.Viewer).
//
// Draws the hull (textured, cyan rim), a forward tick per hardpoint, and a
// screen-stable point marker per hardpoint. Orbit controls follow the
// three.js OrbitControls sign convention exactly, so drag-up looks over the
// top and drag-right orbits right — the natural, non-inverted feel.
(function (root) {
  "use strict";

  const ROTATE_SPEED = 1.0; // three.js-equivalent: angle = 2π · dpx / height
  const ZOOM_STEP = 1.1;
  const PULSE_MS = 1400;
  const RIM = [0.216, 0.878, 1.0]; // TeamAccent #37E0FF

  const HULL_VS = `
    attribute vec3 aPos; attribute vec3 aNormal; attribute vec2 aUv;
    uniform mat4 uVP;
    varying vec3 vN; varying vec2 vUv; varying vec3 vWorld;
    void main(){ vN = aNormal; vUv = aUv; vWorld = aPos; gl_Position = uVP * vec4(aPos, 1.0); }`;
  const HULL_FS = `
    precision mediump float;
    varying vec3 vN; varying vec2 vUv; varying vec3 vWorld;
    uniform vec3 uCam; uniform sampler2D uTex; uniform float uHasTex;
    uniform vec4 uBaseColor; uniform vec3 uRim;
    void main(){
      vec3 N = normalize(vN);
      vec3 V = normalize(uCam - vWorld);
      if (dot(N, V) < 0.0) N = -N;            // treat hull as double-sided
      vec3 L = normalize(vec3(0.35, 0.85, 0.55));
      float diff = clamp(dot(N, L) * 0.5 + 0.5, 0.0, 1.0);
      float rim = pow(1.0 - max(dot(N, V), 0.0), 2.4);
      vec4 base = uBaseColor;
      if (uHasTex > 0.5) base *= texture2D(uTex, vUv);
      vec3 col = base.rgb * (0.32 + 0.78 * diff);
      col += uRim * rim * 0.4;
      gl_FragColor = vec4(col, 1.0);
    }`;
  const POINT_VS = `
    attribute vec3 aPos; attribute vec3 aColor; attribute float aSize;
    uniform mat4 uVP; uniform float uScale;
    varying vec3 vColor;
    void main(){ vColor = aColor; gl_Position = uVP * vec4(aPos, 1.0); gl_PointSize = aSize * uScale; }`;
  const POINT_FS = `
    precision mediump float;
    varying vec3 vColor;
    void main(){
      vec2 c = gl_PointCoord - vec2(0.5);
      float d = length(c);
      if (d > 0.5) discard;
      float edge = smoothstep(0.5, 0.44, d);
      float core = smoothstep(0.34, 0.24, d);
      vec3 col = mix(vColor, vec3(1.0), core * 0.55);
      float ring = smoothstep(0.5, 0.46, d) - smoothstep(0.42, 0.38, d);
      col = mix(col, vec3(1.0), ring * 0.7);
      gl_FragColor = vec4(col, edge);
    }`;
  const LINE_VS = `
    attribute vec3 aPos; attribute vec3 aColor;
    uniform mat4 uVP; varying vec3 vColor;
    void main(){ vColor = aColor; gl_Position = uVP * vec4(aPos, 1.0); }`;
  const LINE_FS = `
    precision mediump float; varying vec3 vColor;
    void main(){ gl_FragColor = vec4(vColor, 0.9); }`;

  // ---- small mat4 helpers (column-major, row-vector-on-right) --------------

  function perspective(fovy, aspect, near, far) {
    const f = 1 / Math.tan(fovy / 2);
    const nf = 1 / (near - far);
    return [f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) * nf, -1, 0, 0, 2 * far * near * nf, 0];
  }
  function lookAt(eye, target, up) {
    const z = norm(sub(eye, target));
    const x = norm(cross(up, z));
    const y = cross(z, x);
    return [
      x[0], y[0], z[0], 0,
      x[1], y[1], z[1], 0,
      x[2], y[2], z[2], 0,
      -dot(x, eye), -dot(y, eye), -dot(z, eye), 1,
    ];
  }
  function mul(a, b) {
    const o = new Array(16);
    for (let c = 0; c < 4; c++)
      for (let r = 0; r < 4; r++)
        o[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
    return o;
  }
  const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
  const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
  const len = (a) => Math.hypot(a[0], a[1], a[2]);
  const norm = (a) => { const l = len(a) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };

  // ---- GL utilities --------------------------------------------------------

  function compile(gl, type, src) {
    const s = gl.createShader(type);
    gl.shaderSource(s, src);
    gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error("Shader: " + gl.getShaderInfoLog(s));
    return s;
  }
  function program(gl, vs, fs) {
    const p = gl.createProgram();
    gl.attachShader(p, compile(gl, gl.VERTEX_SHADER, vs));
    gl.attachShader(p, compile(gl, gl.FRAGMENT_SHADER, fs));
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error("Link: " + gl.getProgramInfoLog(p));
    return p;
  }
  function buffer(gl, data) {
    const b = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, b);
    gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
    return b;
  }

  // ---- public entry --------------------------------------------------------

  function createViewer(canvas, doc, opts) {
    opts = opts || {};
    const gl = canvas.getContext("webgl", { antialias: true, alpha: false, preserveDrawingBuffer: false });
    if (!gl) throw new Error("WebGL is not available in this browser.");
    const uintExt = gl.getExtension("OES_element_index_uint");

    const hullProg = program(gl, HULL_VS, HULL_FS);
    const pointProg = program(gl, POINT_VS, POINT_FS);
    const lineProg = program(gl, LINE_VS, LINE_FS);

    // 1x1 white fallback so the hull sampler is always bound.
    const whiteTex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, whiteTex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([255, 255, 255, 255]));

    // Bounds → camera framing.
    const b = doc.bounds;
    const center = [(b.min[0] + b.max[0]) / 2, (b.min[1] + b.max[1]) / 2, (b.min[2] + b.max[2]) / 2];
    const radiusModel = 0.5 * Math.max(1e-3, len(sub(b.max, b.min)));
    const tickLen = 0.16 * (doc.stats.longestAxis || radiusModel * 2);

    // Hull mesh(es).
    const meshes = doc.drawables.map((d) => {
      const idx = d.indices || makeSeq(d.positions.length / 3);
      const useUint = d.indices ? d.indices.some((v) => v > 65535) : false;
      let indexArray = idx;
      let indexType = gl.UNSIGNED_SHORT;
      if (useUint && uintExt) { indexArray = idx; indexType = gl.UNSIGNED_INT; }
      else if (useUint) { indexArray = new Uint16Array(idx); } // >65535 without ext: clamp (rare)
      else indexArray = idx instanceof Uint16Array ? idx : new Uint16Array(idx);
      const ib = gl.createBuffer();
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ib);
      gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indexArray, gl.STATIC_DRAW);
      const m = {
        pos: buffer(gl, d.positions), norm: buffer(gl, d.normals), uv: buffer(gl, d.uvs),
        ib, count: idx.length, indexType, factor: d.material.factor, tex: whiteTex, hasTex: 0,
      };
      if (d.material.image) loadTexture(gl, d.material.image).then((t) => { if (t) { m.tex = t; m.hasTex = 1; requestRender(); } });
      return m;
    });

    // Hardpoint markers + forward ticks grouped by kind (toggleable).
    const byKind = {};
    const colorFor = opts.colorFor || (() => [1, 1, 1]);
    for (const hp of doc.hardpoints) {
      const g = (byKind[hp.kind] = byKind[hp.kind] || { hps: [] });
      g.hps.push(hp);
    }
    for (const kind of Object.keys(byKind)) {
      const g = byKind[kind];
      const col = colorFor(kind);
      const mPos = [], mCol = [], mSize = [];
      const lPos = [], lCol = [];
      for (const hp of g.hps) {
        mPos.push(hp.pos[0], hp.pos[1], hp.pos[2]);
        mCol.push(col[0], col[1], col[2]);
        mSize.push(1.0);
        lPos.push(hp.pos[0], hp.pos[1], hp.pos[2]);
        lPos.push(hp.pos[0] + hp.fwd[0] * tickLen, hp.pos[1] + hp.fwd[1] * tickLen, hp.pos[2] + hp.fwd[2] * tickLen);
        lCol.push(col[0], col[1], col[2], col[0], col[1], col[2]);
      }
      g.color = col;
      g.markerPos = buffer(gl, new Float32Array(mPos));
      g.markerCol = buffer(gl, new Float32Array(mCol));
      g.markerSize = buffer(gl, new Float32Array(mSize));
      g.markerCount = g.hps.length;
      g.linePos = buffer(gl, new Float32Array(lPos));
      g.lineCol = buffer(gl, new Float32Array(lCol));
      g.lineCount = g.hps.length * 2;
    }

    // Camera state (spherical, three.js polar-from-+Y convention).
    const cam = { theta: 0.7, phi: 1.12, radius: radiusModel / Math.sin(Math.PI / 5) };
    const minR = radiusModel * 0.15;
    const maxR = radiusModel * 12;
    let visibleKinds = null; // null = all
    let focus = null;        // { kind, index, start }
    let vp = null, camPos = [0, 0, 0], viewport = [0, 0];
    let dpr = 1, rafPending = false, disposed = false;

    function requestRender() {
      if (rafPending || disposed) return;
      rafPending = true;
      requestAnimationFrame(frame);
    }

    function resize() {
      dpr = Math.min(window.devicePixelRatio || 1, 2);
      const w = canvas.clientWidth, h = canvas.clientHeight;
      canvas.width = Math.max(1, Math.round(w * dpr));
      canvas.height = Math.max(1, Math.round(h * dpr));
      viewport = [w, h];
      requestRender();
    }

    function camEye() {
      const sp = Math.sin(cam.phi), cp = Math.cos(cam.phi);
      const st = Math.sin(cam.theta), ct = Math.cos(cam.theta);
      return [center[0] + cam.radius * sp * st, center[1] + cam.radius * cp, center[2] + cam.radius * sp * ct];
    }

    function frame(now) {
      rafPending = false;
      if (disposed) return;
      gl.viewport(0, 0, canvas.width, canvas.height);
      gl.clearColor(0.02, 0.027, 0.059, 1); // Void #05070F
      gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
      gl.enable(gl.DEPTH_TEST);
      gl.disable(gl.CULL_FACE);

      camPos = camEye();
      const aspect = canvas.width / Math.max(1, canvas.height);
      const proj = perspective(Math.PI / 4, aspect, Math.max(minR * 0.05, cam.radius * 0.01), maxR + radiusModel * 6);
      vp = mul(proj, lookAt(camPos, center, [0, 1, 0]));

      // Hull.
      gl.useProgram(hullProg);
      setMat(gl, hullProg, "uVP", vp);
      gl.uniform3fv(gl.getUniformLocation(hullProg, "uCam"), camPos);
      gl.uniform3fv(gl.getUniformLocation(hullProg, "uRim"), RIM);
      for (const m of meshes) {
        bindAttrib(gl, hullProg, "aPos", m.pos, 3);
        bindAttrib(gl, hullProg, "aNormal", m.norm, 3);
        bindAttrib(gl, hullProg, "aUv", m.uv, 2);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, m.tex);
        gl.uniform1i(gl.getUniformLocation(hullProg, "uTex"), 0);
        gl.uniform1f(gl.getUniformLocation(hullProg, "uHasTex"), m.hasTex);
        gl.uniform4fv(gl.getUniformLocation(hullProg, "uBaseColor"), m.factor);
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, m.ib);
        gl.drawElements(gl.TRIANGLES, m.count, m.indexType, 0);
      }

      // Forward ticks + markers, always visible (depth test off).
      gl.disable(gl.DEPTH_TEST);
      gl.enable(gl.BLEND);
      gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

      gl.useProgram(lineProg);
      setMat(gl, lineProg, "uVP", vp);
      gl.lineWidth(1);
      for (const kind of Object.keys(byKind)) {
        if (!kindVisible(kind)) continue;
        const g = byKind[kind];
        bindAttrib(gl, lineProg, "aPos", g.linePos, 3);
        bindAttrib(gl, lineProg, "aColor", g.lineCol, 3);
        gl.drawArrays(gl.LINES, 0, g.lineCount);
      }

      gl.useProgram(pointProg);
      setMat(gl, pointProg, "uVP", vp);
      for (const kind of Object.keys(byKind)) {
        if (!kindVisible(kind)) continue;
        const g = byKind[kind];
        // per-marker size: focused one pulses larger
        const sizes = new Float32Array(g.markerCount);
        for (let i = 0; i < g.markerCount; i++) {
          let s = 11;
          if (focus && focus.kind === kind && focus.index === g.hps[i].index) {
            const t = Math.min(1, (now - focus.start) / PULSE_MS);
            s = 20 + 6 * Math.sin((now - focus.start) * 0.012) * (1 - t);
          }
          sizes[i] = s;
        }
        gl.bindBuffer(gl.ARRAY_BUFFER, g.markerSize);
        gl.bufferData(gl.ARRAY_BUFFER, sizes, gl.DYNAMIC_DRAW);
        bindAttrib(gl, pointProg, "aPos", g.markerPos, 3);
        bindAttrib(gl, pointProg, "aColor", g.markerCol, 3);
        bindAttrib(gl, pointProg, "aSize", g.markerSize, 1);
        gl.uniform1f(gl.getUniformLocation(pointProg, "uScale"), dpr);
        gl.drawArrays(gl.POINTS, 0, g.markerCount);
      }
      gl.disable(gl.BLEND);

      if (opts.onFrame) opts.onFrame();
      if (focus && now - focus.start < PULSE_MS) requestRender(); // keep pulsing
    }

    function kindVisible(kind) {
      return !visibleKinds || visibleKinds.has(kind);
    }

    // ---- interaction -------------------------------------------------------

    let dragging = false, lastX = 0, lastY = 0;
    canvas.addEventListener("pointerdown", (e) => {
      dragging = true; lastX = e.clientX; lastY = e.clientY;
      canvas.setPointerCapture(e.pointerId);
    });
    canvas.addEventListener("pointermove", (e) => {
      if (!dragging) return;
      const dx = e.clientX - lastX, dy = e.clientY - lastY;
      lastX = e.clientX; lastY = e.clientY;
      const h = Math.max(1, viewport[1]);
      // Orbit (three.js OrbitControls signs): drag right turns the hull to
      // follow the cursor; drag up orbits the camera down so you see the
      // underside. phi is polar from +Y. Flip either sign to invert that axis.
      cam.theta -= (2 * Math.PI * dx / h) * ROTATE_SPEED;
      cam.phi -= (2 * Math.PI * dy / h) * ROTATE_SPEED;
      const eps = 0.001;
      cam.phi = Math.max(eps, Math.min(Math.PI - eps, cam.phi));
      requestRender();
    });
    const endDrag = (e) => { dragging = false; try { canvas.releasePointerCapture(e.pointerId); } catch (_) {} };
    canvas.addEventListener("pointerup", endDrag);
    canvas.addEventListener("pointercancel", endDrag);
    canvas.addEventListener("wheel", (e) => {
      e.preventDefault();
      cam.radius *= e.deltaY > 0 ? ZOOM_STEP : 1 / ZOOM_STEP;
      cam.radius = Math.max(minR, Math.min(maxR, cam.radius));
      requestRender();
    }, { passive: false });

    const onResize = () => resize();
    window.addEventListener("resize", onResize);
    resize();

    // ---- API ---------------------------------------------------------------

    return {
      setVisibleKinds(set) { visibleKinds = set; requestRender(); },
      focus(kind, index) { focus = { kind, index, start: performance.now() }; requestRender(); },
      clearFocus() { focus = null; requestRender(); },
      resetView() { cam.theta = 0.7; cam.phi = 1.12; cam.radius = radiusModel / Math.sin(Math.PI / 5); requestRender(); },
      project(worldPos) {
        if (!vp) return { x: 0, y: 0, visible: false };
        const x = worldPos[0], y = worldPos[1], z = worldPos[2];
        const cx = vp[0] * x + vp[4] * y + vp[8] * z + vp[12];
        const cy = vp[1] * x + vp[5] * y + vp[9] * z + vp[13];
        const cw = vp[3] * x + vp[7] * y + vp[11] * z + vp[15];
        if (cw <= 0) return { x: 0, y: 0, visible: false };
        return { x: ((cx / cw) * 0.5 + 0.5) * viewport[0], y: (1 - ((cy / cw) * 0.5 + 0.5)) * viewport[1], visible: true };
      },
      requestRender,
      dispose() {
        disposed = true;
        window.removeEventListener("resize", onResize);
        const ext = gl.getExtension("WEBGL_lose_context");
        if (ext) ext.loseContext();
      },
    };

    function makeSeq(n) { const a = new Uint16Array(n); for (let i = 0; i < n; i++) a[i] = i; return a; }
  }

  function bindAttrib(gl, prog, name, buf, size) {
    const loc = gl.getAttribLocation(prog, name);
    if (loc < 0) return;
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, size, gl.FLOAT, false, 0, 0);
  }
  function setMat(gl, prog, name, m) {
    gl.uniformMatrix4fv(gl.getUniformLocation(prog, name), false, new Float32Array(m));
  }
  function loadTexture(gl, image) {
    // glTF UV origin is top-left → NO flipY (matches three.js GLTFLoader).
    const blob = new Blob([image.bytes], { type: image.mimeType });
    return createImageBitmap(blob).then((bmp) => {
      const t = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, t);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, bmp);
      const pot = (bmp.width & (bmp.width - 1)) === 0 && (bmp.height & (bmp.height - 1)) === 0;
      if (pot) { gl.generateMipmap(gl.TEXTURE_2D); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR); }
      else {
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      }
      return t;
    }).catch(() => null);
  }

  root.Viewer = { createViewer };
})(typeof self !== "undefined" ? self : this);
