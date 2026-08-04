// Opt-in diagnostics ingest for the Seapower Multiplayer mod.
//
// Deliberately a SEPARATE worker from seapower-feedback: different bindings
// (R2 + Analytics Engine vs KV + Discord webhook), a couple of orders of
// magnitude more traffic, and a bug in here must not be able to take down the
// launcher's feedback form or sit in the same script scope as the Discord
// webhook secret.
//
// Raw batches land in R2 as received (already gzipped). Metric lines are also
// written to Analytics Engine so the aggregate questions ("did 0.3.6 reduce
// drift?") can be answered in SQL without downloading blobs.

const MAX_BODY = 512 * 1024;          // matches AnalyticsUploader.MaxBatchBytes
const VERSION_RE = /^\d+\.\d+\.\d+$/;
const HEX_RE = /^[0-9a-f]{8,64}$/;

// No CORS headers anywhere, on purpose. This is not a browser client, and
// omitting Access-Control-Allow-Origin means a hostile page cannot drive-by
// abuse the endpoint from a visitor's browser.
const noContent = () => new Response(null, { status: 204 });
const fail = (status, extra) => new Response(null, { status, headers: extra || {} });

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);

    if (request.method === 'GET' && url.pathname === '/v1/health') {
      return new Response(JSON.stringify({ ok: true }), {
        headers: { 'Content-Type': 'application/json' },
      });
    }

    if (request.method !== 'POST' || url.pathname !== '/v1/ingest') {
      return fail(405);
    }

    // A way to stop ingestion without shipping a new client build.
    if (env.KILL_SWITCH === '1') return noContent();

    // ── Cheap rejections first, before any CPU is spent on the body ──────
    // There is no shared key. One compiled into a public DLL authenticates
    // nobody, and Steam ticket validation needs a publisher API key for an
    // appid we do not own. What a key was really doing was bouncing scanner
    // traffic, and the request *shape* does that for free: a gzip body posted
    // to this exact path with well-formed X-SPMP-* headers is not a scanner.
    // Everything past that point is bounded by rate limits, not by identity.
    if (!(request.headers.get('content-encoding') || '').includes('gzip')) {
      return fail(400);
    }

    const installId = request.headers.get('x-spmp-install') || '';
    const sessionId = request.headers.get('x-spmp-session') || '';
    const seq       = request.headers.get('x-spmp-seq') || '0';
    const version   = request.headers.get('x-spmp-version') || '';

    // ── Rate limits, BEFORE any rejection that isn't a bare string compare ─
    // The feedback worker's KV limiter only increments after a successful
    // Discord post, so every one of its error paths is free to spam. Ordering
    // matters more than the limit itself: malformed and oversize batches have
    // to cost the caller quota too, or the cheapest attack is the unmetered
    // one. Native rate-limit bindings also avoid KV's 1000-writes/day free
    // ceiling, which a per-request write would exhaust within minutes.
    const rl = await checkLimits(env, request, installId);
    if (rl) return rl;

    if (!HEX_RE.test(installId) || !HEX_RE.test(sessionId) || !VERSION_RE.test(version)) {
      return fail(400);
    }

    const declared = parseInt(request.headers.get('content-length') || '0', 10);
    if (declared > MAX_BODY) return fail(413);

    // ── Body ─────────────────────────────────────────────────────────────
    let raw;
    try {
      raw = await request.arrayBuffer();
    } catch {
      return fail(400);
    }
    // Content-Length can lie; enforce against what actually arrived.
    if (raw.byteLength === 0 || raw.byteLength > MAX_BODY) return fail(413);

    const now = new Date();
    const day = now.toISOString().slice(0, 10);
    const key = `raw/${day}/${installId}/${sessionId}/${String(seq).padStart(6, '0')}.ndjson.gz`;

    // Decompress for the metric extraction only. The blob is stored exactly as
    // received, so a parse failure here never costs us the raw data.
    let text = null;
    try {
      text = await gunzipToText(raw);
    } catch {
      text = null;
    }

    const header = text ? firstJsonLine(text) : null;

    try {
      await env.LOGS.put(key, raw, {
        httpMetadata: { contentType: 'application/x-ndjson', contentEncoding: 'gzip' },
        customMetadata: {
          installId, sessionId, seq: String(seq), version,
          role: header?.role || '', transport: header?.tr || '',
          mode: header?.mode || '', trigger: header?.trig || '',
          country: request.headers.get('cf-ipcountry') || '',
        },
      });
    } catch (err) {
      // Storage failure is ours, not the client's. 500 makes the client retry
      // with backoff rather than discard the batch.
      return fail(500);
    }

    if (env.AE && text && header) {
      try { writeMetrics(env, request, header, text); } catch { /* never fail ingest on AE */ }
    }

    // Fatal lines are worth a Discord ping so they surface without anyone
    // querying R2. Best-effort, and never blocks the response.
    if (env.DISCORD_WEBHOOK_URL && text && header) {
      ctx.waitUntil(notifyFatal(env, header, text).catch(() => {}));
    }

    return noContent();
  },
};

async function checkLimits(env, request, installId) {
  const ip = request.headers.get('cf-connecting-ip') || 'unknown';
  try {
    if (env.INGEST_LIMIT) {
      const { success } = await env.INGEST_LIMIT.limit({ key: installId });
      if (!success) return fail(429, { 'Retry-After': '120' });
    }
    if (env.IP_LIMIT) {
      // Second, looser limit so one host cannot forge many install ids.
      const { success } = await env.IP_LIMIT.limit({ key: ip });
      if (!success) return fail(429, { 'Retry-After': '120' });
    }
  } catch {
    // A limiter outage must not take ingest down with it.
  }
  return null;
}

async function gunzipToText(arrayBuffer) {
  const stream = new Response(arrayBuffer).body.pipeThrough(new DecompressionStream('gzip'));
  return await new Response(stream).text();
}

function firstJsonLine(text) {
  const nl = text.indexOf('\n');
  try {
    return JSON.parse(nl < 0 ? text : text.slice(0, nl));
  } catch {
    return null;
  }
}

// Analytics Engine limits: 1 index (must be LOW cardinality - it is the
// sampling key), 20 blobs, 20 doubles.
function writeMetrics(env, request, header, text) {
  for (const line of text.split('\n')) {
    if (!line.startsWith('{"t":"m"')) continue;

    let m;
    try { m = JSON.parse(line); } catch { continue; }

    const rtt = m.rtt || {}, fps = m.fps || {}, drift = m.drift || {}, perr = m.perr || {};

    env.AE.writeDataPoint({
      // Plugin version, NOT installId: the index is the sampling and billing
      // key, and a high-cardinality index destroys the dataset.
      indexes: [String(header.pv || 'unknown')],
      blobs: [
        header.i || '', header.s || '', header.role || '', header.tr || '',
        header.mode || '', String(header.trig || ''), m.hs || '', m.sim || '',
        request.headers.get('cf-ipcountry') || '',
      ],
      doubles: [
        num(rtt.a), num(rtt.p95), num(rtt.mx), num(m.loss),
        num(m.bin), num(m.bout), num(m.sfmx),
        // Two of the 20 slots go to mission time, which is worth what it cost:
        //   mis   - shared sync clock (time of day). Host and client agree on
        //           it, so it is the key that joins the two sides of a session.
        //           Wall-clock ts only matches if both PCs' clocks do.
        //   misEl - mission elapsed. Per-machine baseline, so it does NOT align
        //           across players, but it is the only thing that says how far
        //           into a mission a row sits. Not derivable from real time at
        //           compression.
        // Displaced: `w` (constant 10 s) and `mem` (coarse GC number that has
        // never explained a bug). Both are still in the R2 blob.
        num(fps.a), num(fps.mn), num(m.hitch), num(m.mis),
        num(drift.sa), num(drift.sm), num(drift.aa),
        num(perr.sa), num(perr.aa),
        num(m.jit), num(m.cad), num(m.rep), num(m.misEl),
      ],
    });
  }
}

const num = (v) => (typeof v === 'number' && isFinite(v) ? v : 0);

async function notifyFatal(env, header, text) {
  const fatals = [];
  for (const line of text.split('\n')) {
    if (!line.startsWith('{"t":"l"') && !line.startsWith('{"t":"x"')) continue;
    let r;
    try { r = JSON.parse(line); } catch { continue; }
    if (r.lv === 'F' || r.t === 'x') fatals.push(r.m);
    if (fatals.length >= 3) break;
  }
  if (fatals.length === 0) return;

  await fetch(env.DISCORD_WEBHOOK_URL, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      username: 'Seapower MP Diagnostics',
      embeds: [{
        title: `Fatal in ${header.pv} (${header.role}/${header.tr})`,
        description: fatals.join('\n\n').substring(0, 4000),
        color: 0xE74C3C,
        footer: { text: `install ${String(header.i).slice(0, 8)} · session ${header.s}` },
        timestamp: new Date().toISOString(),
      }],
    }),
  });
}
