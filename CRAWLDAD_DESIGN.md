# Crawldad — Technical Design & Requirements

> Status: v1 design. Working name "Crawldad" is provisional.
> Companion: `CRAWLDAD_PLAN.md` (phased implementation plan).
> Acceptance criterion: the LJCMG enforcement scraper
> (`mrr.gg/src/US.KY.Jefferson.LJCMG.Worker`) must be expressible as Crawldad payloads and
> produce output identical to the current C# implementation.

---

## 1. Thesis and scope

Crawldad is a **declarative browser-automation API**. The caller POSTs one JSON payload describing
an entire browser session — navigation, waits, clicks, form fills, extraction, downloads, and
**control flow** (conditionals, loops, branching on page content) — and the service executes it in
Playwright for .NET against a customer-supplied browser backend, returning one structured result.

**Automation is data, not code.** A payload is a JSON document: composable, diffable, storable in
Postgres, generatable by an LLM, and safe to execute because it cannot call `fs`, cannot `eval`, and
cannot loop unboundedly. Every decision below protects that property. The differentiation over the
competitive field (§2) is **loops + content-aware conditions + programmatic composability**, not
"we have conditionals too."

### What this document delivers (Deliverable 1 checklist)
Payload schema (§4), action set → Playwright mapping (§5), control-flow constructs (§6), the
expression language and its deliberate limits (§7), execution model / scoping / error taxonomy /
timeout hierarchy (§8), backend adapter interface and credential modes (§9), result and error shapes
(§10), long-running execution — streaming, cancellation, resumability (§11), security (§12),
observability (§13), persistence & versioning on the Critter Stack (§14), the five open-tension
resolutions (§15), @puppeteer/replay interop (§16). Appendix A: capability coverage matrix. **Appendix
B: both LJCMG operations as complete payloads (the proof).** Appendix C: expression builtin → C#
mapping.

### Boundaries (given, not relitigated)
Hosted/SaaS only; customers bring their own browser compute (Browserbase / Browserless / self-hosted
tunnel); pricing attaches to *managed payloads* (drift, versioning, observability), so payloads are
first-class persistent entities; no 40–60 s execution cap (the reference legitimately runs minutes).
Out of scope entirely: proxy layer, browser fleet, billing, marketing site.

### House stack
.NET / C# + **Playwright for .NET**, built on the **Critter Stack** (WolverineFX + Marten on
Postgres), following the idioms in the reference `idiomatic-critter-stack` app. This is the
strong default: the reference scraper is C#, Playwright for .NET is first-class, and the foundation
repo gives us event sourcing (→ free run traces, §13), durable messaging (→ long-running
orchestration, §11), and a document/event store (→ payload versioning, §14) out of the box. No reason
to deviate. (Version note in §3.)

---

## 2. Competitive context (re-verified 2026-07-28)

| Product | Payload | Control flow | Hard limit |
|---|---|---|---|
| ScrapingBee `js_scenario` | JSON array | linear only | **40 s** whole-scenario cap (verbatim) |
| Firecrawl `actions` | JSON array, `type` discriminator | linear only | own docs: "not recommended … for complex interactions"; `/interact` for branching |
| Zyte API `actions` | JSON array | linear only | **60 s** total browser cap; non-linear ⇒ drop to a TypeScript "browser script" |
| Browserless **BrowserQL** | GraphQL document | `if`/`ifnot` | see below — the one to beat |
| Chrome DevTools Recorder / `@puppeteer/replay` | JSON step array | linear | open, free schema; good import on-ramp (§16) |

**BrowserQL, verified verbatim.** `if(operator: OperatorTypes = or, request: RequestInput, response:
ResponseInput, selector: String, visible: Boolean = false)`. Confirmed limits: (a) conditions test
**DOM structure + network only — never extracted text/content**; (b) **"`if` and `ifnot` cannot be
nested inside each other"**; (c) **no `else`** ("the branch returns `null` and execution continues");
(d) **no loops of any kind**; plus conditions are point-in-time ("Conditions evaluate immediately,
they don't wait"). It is a GraphQL string you author, not a structure you compose.

**Therefore Crawldad's headline features:** real loops (with mandatory safety caps), **content-aware
conditions** (branch on extracted text — the thing BrowserQL structurally cannot do), and a payload
that is a *composable JSON structure*, not an authored string. Corollary: **crawling is not an
endpoint, it is a payload** — a loop over a frontier with a branch — which is why every competitor
ships crawl as a separate hardcoded feature. That is a headline demo, not a code path in our engine.

---

## 3. Assumptions and corrections to the brief

The prompt asked us to flag anything wrong. Findings, each low-impact but worth stating:

1. **`.NET 8` vs `.NET 10`.** The prompt says the house stack is .NET 8. The foundation repo
   (`idiomatic-critter-stack`) targets **.NET 10 / Marten 9.11 / Wolverine 6.15 / Alba 8.5**, and
   several idioms we depend on (Marten 9 source-generated projections, Wolverine 6
   `RuntimeCompilation`) are those versions. **Assumption:** build on the foundation's .NET 10 +
   current Critter versions. Nothing in LJCMG needs a lower runtime. If .NET 8 is a hard constraint,
   the projection/saga APIs shift slightly but the design holds.

2. **`screenshot` is absent from the reference, not "commented out."** A full-project search finds no
   screenshot call anywhere in the Worker — not even commented. The capability is still required
   (§5, and screenshot-on-failure is core to observability §13), but the acceptance suite does not
   exercise it.

3. **The deep-scrape output is `RecordScrapedV1`, not `EnforcementCase`.** The prompt points at
   `Enforcement/Models/` for "output shapes," but `EnforcementScraper` assembles
   `MRR.Domain.US.KY.Jefferson.LJCMG.Enforcement.RecordScrapedV1` (a different, richer shape).
   `EnforcementCase.cs` is legacy/unused. The worked payload (Appendix B) targets `RecordScrapedV1`.

4. **XPath is never used** by the reference (CSS + `GetByTitle` only). We still support XPath
   selectors (§5) because it is cheap and real sites need it, but it is undemonstrated by the
   acceptance suite — not a v1 risk.

5. **Browserbase "high-security connectUrl" — live-primary re-verified 2026-08-08 (shape drifted).**
   The brief says the two-step flow lets the customer "hand us only the ephemeral, single-session
   `connectUrl`." The earlier docs-based check found the `connectUrl` embedded the account `apiKey`;
   a **live** session-create on 2026-08-08 shows it no longer does. The returned URL is now
   `wss://connect.<region>.browserbase.com/?signingKey=<JWT>` — a **per-session** signing key, not the
   account key (which travels only in the `X-BB-API-Key` header). So the brief's "ephemeral,
   single-session" framing now holds: a leaked `connectUrl` compromises that one session until it
   expires, not the account. It is still **not** "safe to leak" (session hijack) and is scrubbed as a
   secret (the `signingKey` param + exact-match, §12); we support both modes. See `SECURITY.md` for the
   confirmed shapes and scrub re-confirmation. (Was Confidence MED-HIGH; now **live-primary**.)

6. **Latent bug in the reference retry we must fix, not copy.** On `Page crashed`, the retry handler
   reopens a page (`page = await browserContext.NewPageAsync()`) but assigns it only to a local; the
   retried operation still references the closed page. The *intent* — "on crash, reopen a page on the
   same context and continue" — is what Crawldad implements; we bind the reopened page back into the
   interpreter's session (§8).

7. **`CLAUDE.md` "goal-driven format."** The only `CLAUDE.md`/`AGENTS.md` present is the Critter Stack
   agent map; it does not literally contain a "goal-driven phase format" or the words "simplicity
   first / surface tradeoffs." We interpret those as their plain meaning and adopt a Goal / Success
   Criteria / Delivers / De-risks phase format in the plan. Assumption flagged.

---

## 4. Payload schema (v1)

A payload is one JSON document. Dialect is pinned by `crawldad`. The structure is JSON (composable,
diffable); only **leaf expressions** are strings in the expression language (§7) — the same shape as
SQL-in-JSON or CEL-in-YAML, and unlike BrowserQL's whole-document string.

```jsonc
{
  "crawldad": "1",                    // dialect version — SemVer-major; v1 frozen at first ship
  "name": "ljcmg.enforcement.search", // logical identity of the managed payload (persistent, versioned)

  "inputs": {                         // typed parameters bound at run time
    "<name>": { "type": "string|number|boolean|date|array|object|backend|storageTarget|secretRef",
                "required": false, "default": <literal> }
    // a `secretRef` value is a vault REFERENCE only (never the secret); consumed solely by `fill.secret` (CD-6/§12)
  },

  "config": { /* session config — §8.1 */ },

  "vars":  { "<name>": <Expr> },      // optional initial variable bindings (evaluated once, in order)

  "steps": [ <Node> … ],             // the ordered program (§5, §6)

  "result": <Expr>                    // final expression that shapes the response body (§10, tension #4)
}
```

Field kinds used throughout:

- **`Expr`** — a string in the expression language (§7). Always an expression; string *literals*
  inside are quoted (`"'owner'"`). Total, pure, side-effect-free.
- **`Tmpl`** — a template string with `${<Expr>}` interpolation, for URLs and dynamic selectors
  (`"…table:nth-child(${i})"`, `"^${nextPageNumber}$"`). A `Tmpl` with no `${}` is a literal.
- **`Sel`** — a selector (§5).
- **`Node`** — one step: an object with exactly one recognised head key (§5/§6). Validated by JSON
  Schema `oneOf` on the head key.
- **`Target`** — a storage sink for bytes (§9.4): a caller presigned URL or a configured blob store.
- **`Failure`** — `{ "class": "terminal"|"retryable", "code": "<slug>", "message": Tmpl }`.

The full JSON Schema ships in `schema/crawldad-1.schema.json`; §4–§7 are its normative description.

---

## 5. Action set (each action → Playwright for .NET)

Every step is `{ "<head>": { … } }`. Actions (effectful) below; control-flow nodes in §6. `in?` on
any action/selector names a frame handle (§5.2); `timeoutMs?` overrides the timeout hierarchy (§8.4).

### 5.1 Actions

| Node | Fields | Playwright for .NET | LJCMG use |
|---|---|---|---|
| `goto` | `url:Tmpl, waitUntil?, timeoutMs?` | `page.GotoAsync` | `:92`, `:195` |
| `waitForLoadState` | `state:"load"\|"domcontentloaded"\|"networkidle", timeoutMs?` | `WaitForLoadStateAsync` | `:93,:197,:221` |
| `waitFor` | `selector:Sel, state:"visible"\|"hidden"\|"attached"\|"detached", timeoutMs?, in?` | `Locator.WaitForAsync` | `#divGlobalLoading` hidden `:116`; regex page wait `:612` |
| `waitForRequest` | `urlPrefix:Tmpl, method?, timeoutMs?, trigger:Node[]` | `RunAndWaitForRequestAsync(trigger, predicate)` | postback waits `:111,:158` |
| `frame` | `var, selector:Sel` | `page.FrameLocator` → handle | attachments iframe `:533` |
| `locate` | `var, selector:Sel, base?, in?` **or** `var, from, nth?:Expr, first?, filter?:{hasTextRegex:Tmpl}` | `page.Locator` / `.Nth/.First/.Filter` → lazy handle | rows `:125`, `.Nth(i)`, `.Filter` `:613` |
| `click` | `selector:Sel, in?, timeoutMs?` | `ClickAsync` | many |
| `fill` | `selector:Sel, value:Expr, in?` | `FillAsync` | `:100,:108` |
| `clear` | `selector:Sel, in?` | `ClearAsync` | `:99,:107` |
| `addStyleTag` | `content:Tmpl` | `AddStyleTagAsync` | force tabs visible `:209` |
| `screenshot` | `name?:Tmpl` (full-page → `IScreenshotStore`) | `page.ScreenshotAsync` | #8 shipped: full-page via the screenshot-on-failure seam; `to:Target`/`fullPage`/`selector`/`in` deferred until a workload needs them (§13) |
| `download` | `trigger:Node[], to:Target, timeoutMs?, idempotencyKey?:Expr, var` | `WaitForDownloadAsync` + trigger | per-row `:560` |
| `set` | `var, path?:Tmpl, value:Expr` | (interpreter state) | pervasive |
| `push` | `into:var, value:Expr` | (interpreter state) | accumulate output lists |
| `log` | `level:"info"\|"warning"\|"error", message:Tmpl` | emits a `LogEmitted` run event | all `_logger.LogWarning/Error` |
| `fail` | `Failure` | raise typed failure (§8.3) | terminal branches |
| `guard` | `cond:Expr, elseFail:Failure` | assert-or-fail | CapDetail guard `:203` |

### 5.2 Selectors (`Sel`) and frames

A `Sel` is either a **string** (CSS by default; `"xpath=…"` for XPath, Playwright-style) or a
structured object supporting the locator features the reference uses:

```jsonc
{ "css": Tmpl }                         // or "xpath", "text"
{ "role": "button", "name": Tmpl }      // getByRole
{ "title": Tmpl }                       // getByTitle  (":219,:220")
// refinements, chainable onto any of the above:
{ "css": "table tr", "nth": Expr,       // .Nth(expr)
  "first": true,                        // .First
  "filter": { "hasTextRegex": Tmpl },   // .Filter(HasTextRegex)  (":613")
  "base": "rowVar",                     // relative to a bound locator (child locator)
  "in": "attFrame" }                    // resolve inside a bound frame handle
```

Comma-union CSS (`"#a, #b"`) is passed through verbatim (`:233`). **Frames:** `frame` binds a
`FrameLocator` handle to a var; any action or selector may then set `"in": "<frameVar>"`. Locator
handles bound by `locate` are lazy (re-queried on use), matching Playwright semantics — the reference
relies on this when the grid re-renders after a postback.

`locate` has two forms, mirroring Playwright exactly:
- **from a selector:** `{ "locate": { "var": "rows", "selector": "#…gdvPermitList tr" } }` → `page.Locator(…)`
- **derived from a handle:** `{ "locate": { "var": "row", "from": "rows", "nth": "i" } }` → `rows.Nth(i)`
  (also `first: true`, `filter: {hasTextRegex}`).

Read-only DOM access from **expressions** goes through the enumerated builtins `count/exists/text/
innerText/innerHtml/attr` (§7), which accept either a page-scoped selector string or a bound locator
var, plus a relative form `text(baseVar, "css")`. That is the *only* page access an expression has
(reads, never effects) — the mechanism behind content-aware conditions.

---

## 6. Control-flow constructs

Control flow and state mutation are **structural nodes**, never expressions (§7 boundary). Every
loop carries a **mandatory `maxIterations`** — the "loop safety cap" is a first-class requirement,
not an afterthought.

### `if` / `switch`
```jsonc
{ "if": { "cond": Expr, "then": [ Node… ], "else": [ Node… ] } }

{ "switch": { "cases": [ { "when": Expr, "do": [ Node… ] } … ],  // boolean predicates; first true wins
              "default": [ Node… ] } }
```
`switch` is sugar over nested `if/else`; it directly serves the reference's many "heading/key
startsWith X → field Y" ladders (`:389-397`, `:297-353`).

### `loop`
```jsonc
// numeric for-loop
{ "loop": { "for": { "var": "i", "from": Expr, "to": Expr, "step": 1,
                     "inclusiveTo": false, "exclusiveTo": true },
            "do": [ Node… ], "maxIterations": <int> } }

// condition loop — body then test = do/while (matches the reference's do…while)
{ "loop": { "while": Expr, "do": [ Node… ],
            "maxIterations": <int>, "onMaxIterations": "fail"|"warn" } }

// collection loop
{ "forEach": { "in": Expr,                // a bound locator var (→ .AllAsync()) or an array value
               "as": "item", "index": "i",
               "do": [ Node… ], "maxIterations": <int> } }
```
- `for` reproduces `i=3 .. count-2` (`from:"3", to:"count(rows) - 2", exclusiveTo:true`), the
  `k*2+1`/`k*2+2` pair loop (via interpolation in child selectors), and `i*2`/`i*2+1` paired rows.
- `while` reproduces both do…while pagination loops. The attachment loop sets
  `maxIterations: 50, onMaxIterations: "warn"` — hitting the cap logs a warning and stops (the
  reference's exact behaviour), rather than failing.
- `onMaxIterations` defaults to `"fail"` (terminal) for `while`/`forEach`; the attachment loop opts
  into `"warn"`.

### `break` / `continue`
```jsonc
{ "break": { "when": Expr? } }      // Expr optional; bare = unconditional
{ "continue": { "when": Expr? } }
```
`continue` reproduces the row skips (`"No records found."` `:548`; no file link `:552`; empty owner
block `:322`). `break` reproduces pagination stop.

### `guard` / `fail` (typed abort)
```jsonc
{ "guard": { "cond": Expr, "elseFail": { "class": "terminal", "code": "record_not_accessible",
             "message": "Record not accessible (redirected to ${urlPath(pageUrl())})" } } }
```
`guard` = "cond must hold, else raise the failure." `fail` raises unconditionally (used in `else`
branches, e.g. unknown heading). `class:"terminal"` is **not retried** (§8.3) — this is the CapDetail
guard whose whole purpose is to avoid burning 15 retries (~30 min) on a redirected record.

### `comment`
`{ "comment": <string> }` is a no-op annotation node — ignored at execution and exempt from the
unknown-head-key validation check (§12). Used for readability in the worked payloads (Appendix B).

---

## 7. The expression / value language (and its deliberate limits)

This is the slipperiest part of the design (open tension #2). The boundary:

> **Expressions are pure, total, and side-effect-free. All state mutation and all iteration are
> structural nodes (§6). Expressions may perform only enumerated read-only DOM queries.**

Concretely the sublanguage is modelled on **Google CEL** (Common Expression Language): a real,
specified, non-Turing-complete expression grammar with no user-defined functions, no recursion, no
assignment, and evaluation that always terminates. We add exactly the string / collection / URL / DOM
builtins the reference needs — no more (Appendix C proves the fit is tight).

**Why this is the right boundary.** It is precisely strong enough to express LJCMG's ugly string
surgery and content-aware branching, and precisely weak enough to keep the safety argument: an
expression cannot loop (iteration is structural, capped), cannot recurse, cannot call `fs`, cannot
`eval`, cannot allocate unboundedly. The payload *structure* stays diffable/composable JSON; only
leaves are expression strings. This is the line every competitor either fails to reach (BrowserQL:
can't branch on text at all) or blows past (Zyte/Firecrawl: "drop to TypeScript/JS").

### 7.1 Grammar
Operators `+ - * / %`, `== != < <= > >=`, `&& || !`, ternary `?:`, member `.`, index `[]`.
References: `input.*`, declared vars, loop `var`/`index`, and `pageUrl()`. Literals: string
(`'…'`), number, boolean, null, array `[…]`, object `{ k: Expr }`.

Three rules the worked payloads rely on:
- **`+` also concatenates** when either operand is a string (`'' + urlScheme(pageUrl()) + '://' + …`);
  `- * / %` are numeric.
- **String/DOM builtins null-propagate** — a null primary argument yields null (like C# `?.`), so a
  missing element flows through `replace`/`split`/`trim` as null and `coalesce(…, default)` supplies
  the reference's `?? "…"`. `==`/`!=` compare against `null` directly (the processing-heading guard).
- **DOM builtins accept a `Sel`** (string *or* structured, §5.2) or a bound locator var, plus the
  relative form `text(baseVar, "css")`. So `exists({ base:'detail', css:'…' })` and
  `text({ css:'…', first:true })` are valid. (`count` is the exception: on a structured `Sel` map its
  value-model overload counts map entries, not DOM matches — use `exists` for existence and
  `locate`+`count(var)` for a DOM count; see B.2's fidelity notes.)

### 7.2 Builtins (the enumerated surface — the boundary)
- **String:** `trim, lower, upper, replace(s,old,new), replaceRegex(s,re,rep), split(s,sep)→[],
  substring(s,a,b?), substringAfterLast(s,sep), startsWith(s,p), endsWith(s,p), contains(s,x),
  indexOf(s,x), lastIndexOf(s,x), length(s), matches(s,re), equalsIgnoreCase(a,b),
  isNullOrWhitespace(s), string(x), join(list,sep)`
- **Collection:** `count(x), length(x), first(x), last(x), nth(x,i), slice(x,a,b?), reverse(x),
  distinct(x), filter(list,v,pred), map(list,v,expr), any(list,v,pred), all(list,v,pred), min(x),
  max(x), sortBy(list,v,key), keys(map), get(map,key), coalesce(a,b…), toInt(s), isInt(s)`
- **URL:** `urlScheme(u), urlHost(u), urlPath(u), pageUrl(), resolveUrl(base,rel)`
- **DOM (read-only, the only page access):** `count(Sel|loc), exists(Sel|loc), text(x[,css]),
  innerText(x[,css]), innerHtml(x[,css]), attr(x[,css],name)`

**Explicitly NOT in the language** (the far side of the boundary): user-defined functions; recursion;
assignment or mutation (that is `set`/`push`); arbitrary iteration (only `map/filter/any/all` bounded
by their input list; unbounded iteration is a capped `loop`); date arithmetic beyond formatting;
regex backreference-driven catastrophic patterns (patterns are size-limited and timeout-guarded); any
network, filesystem, clock, randomness, or `eval`. Determinism: an expression is a pure function of
(inputs, vars, current DOM reads); given the same page it always yields the same value.

**Access semantics (fidelity-critical).** An out-of-range list/array **index**, and a failed required
conversion, raise a **terminal** failure (matching C# `IndexOutOfRangeException`) — never null. So the
reference's split-then-index throws (parcel `:438`, processing `:489-493`) are reproduced as terminal,
non-retryable failures; a default is produced *only* by explicit `coalesce`/`?:`. (String/DOM builtins
still null-propagate per §7.1 — that models C# `?.`, distinct from indexing.)

### 7.3 Two URL strategies — reproduce both
The reference uses **two different** URL constructions; the language expresses each:
- **Search rows (`:130`)** concatenate naively: `{scheme}://{host}{href}` (no RFC resolution) →
  `"${urlScheme(pageUrl())}://${urlHost(pageUrl())}${href}"`.
- **Related records (`:672`)** resolve properly: `new Uri(new Uri(link.Id), href)` →
  `resolveUrl(input.link, href)`.

### 7.4 The hardest cases, proven expressible
- **Related-records "greatest indent strictly less than current"** (`parents.Where(k<indent)
  .OrderByDescending(k).Select(v).FirstOrDefault() ?? ""`, `:655`) — `parents` is a **map var**
  (`set` with computed key `path:"[${indent}]"`); the query is:
  `set candidateIndents = filter(map(keys(parents), k, toInt(k)), n, n < indent)`, then
  `count(candidateIndents) > 0 ? get(parents, string(max(candidateIndents))) : ''`.
- **`k*2+1` / `k*2+2`** → selector interpolation `…div:nth-child(${k*2+1})`.
- **Chained split/replace** (processing status `:489-493`) →
  `replace(trim(split(lines[0], ',')[0]), 'Due on ', '')`, etc.
- **3/4/5 `<br>` branch** → `set addressLines = length(split(innerHtml(addressBlock), '<br>'))` then
  `if addressLines == 3 …`, splitting `innerHtml` on `'<br>'` and again on `'<span'`.
- **Content-hash download identity** — provided natively by the engine (§9.4), identical to
  `AttachmentHashing`, so the payload never does byte-level GUID construction.

---

## 8. Execution model

### 8.1 Session config
```jsonc
"config": {
  "backend": <Expr>,                       // e.g. "input.backend" — adapter + credential mode (§9)
  "defaultTimeoutMs": 120000,              // DEFAULT_TIMEOUT
  "launch":  { "args": ["--disable-web-security"] },   // passed through where the backend allows (§9)
  "context": { "bypassCsp": true },        // BypassCSP
  "route": {                               // request interception (PlaywrightFactory)
    "blockHosts": [ "www.datadoghq-browser-agent.com", "cdn.walkme.com", "cdn.gtranslate.net",
                    "csp-report.browser-intake-datadoghq.com", "ec.walkme.com",
                    "rum.browser-intake-datadoghq.com", "connect.facebook.net",
                    "fonts.googleapis.com" ],
    "blockResourceTypes": [ "image", "media", "font" ],
    "cacheResourceTypes": [ "stylesheet", "script" ],
    "cacheUrlSuffixes":   [ ".html", ".js" ],
    "throttle": { "minIntervalMs": 2000 }  // global, serialized: one non-cached request per tick
  },
  "retry": {                               // operation-level (wraps the whole program), matching Polly
    "maxAttempts": 15, "delayMs": 5000, "backoff": "constant",
    "retryOn": [ "timeout", "pageCrashed" ],   // ONLY these; everything else terminal (§8.3)
    "onPageCrashed": "reopenPage"          // close + reopen page on the SAME context, rebind session (§3.6)
  }
}
```
The route/cache/throttle block reproduces `PlaywrightFactory` exactly: abort by host **or**
resource-type; else cache stylesheet/script/`.html`/`.js` (cross-run cache, fulfil from store); else
throttle through one global 2 s tick. The cache is a per-region shared store keyed by URL (the moat
telemetry — "which asset on which site" — accretes here for free).

### 8.2 State and variable scoping
- **One flat run scope** holds `input.*` (read-only), `vars`, and everything `set`/`push` creates.
  There are no expression-local bindings (keeps the language first-order; intermediate values become
  `set` vars). Values: string, number, bool, null, array, object (map), and opaque **locator/frame
  handles** (usable in reads, never serialised into output).
- **Loop variables** (`for.var`, `forEach.as`/`index`) are visible inside the loop body and shadow
  outer names for that scope; they leave scope on loop exit. All other `set`/`push` mutate the run
  scope and persist across steps (the reference accumulates `owners`, `violations`, `parents`, etc.
  across a whole operation — flat scope matches that).
- **No closures, no references shared into the browser.** State is plain JSON plus handles.

### 8.3 Error taxonomy — retryable vs terminal
The single most important operational distinction (the ~30-min-per-bad-record lesson).

- **Retryable:** `timeout`, `pageCrashed`. Retried per `config.retry` (15 × 5 s constant). On
  `pageCrashed`: close + reopen the page on the same context and **rebind it into the interpreter
  session** (fixing the reference's latent bug §3.6), then re-run the operation from the top (Polly
  wraps the whole program).
- **Terminal (never retried), surfaced as a typed failure:** anything a `guard`/`fail` raises with
  `class:"terminal"`, and by default any non-retryable engine error. The reference's terminal points:
  CapDetail-guard redirect (`:203`), empty record number/type (`:273,:276`), unknown owner heading
  (`:352`), unparseable processing heading (`:464`), and split-index-out-of-range in processing
  parsing (`:489-493`).
- **Warnings are not failures.** `log level:"warning"` emits a `LogEmitted` event and continues:
  exceptional address line count, MULTIPLE OWNERS/PARCELS, exceptional owner line count, unknown ASIT
  heading (note: ASIT is warn-only, asymmetric with the owner heading which is terminal), attachment
  safety cap, `handleDownload` reject, related-record indentation/class parse failures.
- **Host contract:** a terminal run failure returns a typed error (§10) the caller maps to its own
  "mark source failed and move on" (`EnforcementScraper` → `LinkFailed`). Retryable exhaustion (15
  attempts) becomes terminal.

Result contract for the *response*: `{ "status": "succeeded"|"failed"|"cancelled", … }` with a
`failure.class` of `terminal`|`retryable-exhausted` so callers branch exactly as the reference does.

### 8.4 Timeout hierarchy (most specific wins)
`config.defaultTimeoutMs` (120 s) < per-node `timeoutMs` < action-intrinsic long timeouts. The
reference's long timeouts are expressed per-node: attachments `waitForLoadState`/page-number
`waitFor` = `360000`; `download` = `180000`. There is also a **run-level wall clock** (a saga
timeout, §11) — deliberately *not* in the 40–60 s competitor range; default generous (e.g. 30 min),
configurable, enforced by the orchestrator, not by Playwright.

Distinct from both of the above is the **synchronous-response window** (CD-15,
`Crawldad:Limits:SyncUpgradeThresholdMs`, default **120 s** — a different 120 s from the per-action
`defaultTimeoutMs`): it bounds only how long a default `POST /runs` holds the caller's HTTP
connection, not how long the run may execute. A run still executing at the window is **auto-upgraded,
not failed** — it keeps executing on the durable executor exactly as an `"async": true` run would
(under the same run-level wall clock above), the caller gets `202 { runId, status:"running" }` and
follows the async surface (§10/§11), and a run finishing inside the window returns today's
synchronous body unchanged. The window exists because every viable Azure ingress kills a longer sync
request first (Front Door / Container Apps Envoy 240 s, App Service ~230 s; docs/PRODUCT.md §1.1/§2.2),
so the connection is always answered — result or upgrade — before ingress can. Deliberate trade-off: because
the run executes on its own cancellation source (so returning the 202 cannot cancel it), a client disconnect no
longer cancels an in-flight sync run (pre-CD-15 it did, via the request token) — a run is bounded by the sync
window and then the async wall-clock deadline above, not by the caller's connection.

---

## 9. Backend adapter interface

The service never owns browsers. `IBrowserBackend` is an injected seam (the `IEmailGateway` idiom
scaled up), with a `FakeBrowserBackend` for tests (§ testing). Adapters are **asymmetric** — that is
expected, not a leak.

```csharp
public interface IBrowserBackend
{
    // Establishes a live CDP/native connection; returns a Playwright IBrowser/IBrowserContext handle.
    Task<IBrowserSession> ConnectAsync(BackendBinding binding, CancellationToken ct);
}
// binding = adapter id + credential reference (never the raw secret) + backendOptions passthrough.
```

### 9.1 Backends and credential modes
| Backend | Connect | Credential mode(s) | Protocol |
|---|---|---|---|
| **Browserbase** | `sessions.create()` → `connectUrl` → `chromium.connectOverCDP(connectUrl)` | `apiKey` (we create the session) **or** `connectUrl` (caller pre-creates) | **CDP only** for Playwright (`connectOverCDP`); Selenium path exists but unused |
| **Browserless** | `wss://production-{sfo,lon,ams}.browserless.io/chromium/playwright?token=…` | `token` **is** the credential (account-scoped) | **prefer native** `playwright.chromium.connect` via `/chromium/playwright`; `/chromium` is CDP if needed |
| **Self-hosted** | customer Chrome reached via an **outbound tunnel** (thin connector agent) | tunnel identity | CDP behind the tunnel; **never** expose CDP to the internet |

Notes wired into the design:
- **Prefer Browserless native** (`/chromium/playwright` + `chromium.connect`) over raw CDP — less
  chatty for remote operation (verified). Browserbase is CDP-only, so the two adapters are not
  symmetric; the interface abstracts *connect*, not *protocol*.
- **`connectUrl` mode caveat (see §3.5; live-primary re-verified 2026-08-08):** Browserbase's returned
  `connectUrl` carries a **per-session `signingKey` JWT**, not the account `apiKey` (re-verified live —
  the earlier "embeds the apiKey" note is superseded). It buys ephemeral session lifetime and no
  long-lived key in our vault, and is **not** safe-to-leak; scrub it identically (§12). In `connectUrl`
  mode, Browserbase session recordings land in the *caller's* dashboard (observability follows the
  creating key), a feature for high-security customers. **Ship-blocker check: DONE** — the exact
  `connectUrl` shape was re-verified against a live primary Browserbase session (see `SECURITY.md`).
- **Browserless token is account-scoped** — a leaked token drains the account's unit balance. Same
  scrubbing rules.
- **`backendOptions` passthrough** carries provider capabilities we do not implement: Browserless
  `blockAds=true` (confirmed passthrough), proxy/`proxyCountry`, `launch` args, and its stealth
  *routes* (`/chromium/stealth`, BrowserQL `humanlike`). **`emulationOs` is unverified** — treat as
  opaque passthrough, do not document as supported until confirmed.
- **Region-match** the interpreter to the backend region (CDP round-trips aren't free, though page
  loads dominate). The run records its region for the cache-locality telemetry.

### 9.2 Where the reference's `PlaywrightFactory` maps
Launch (`--disable-web-security`), context (`BypassCSP`, 120 s default), and the route/cache/throttle
policy (§8.1) are applied by the interpreter **on top of** whatever context the backend hands back.
For self-hosted/native backends we set them directly; for CDP backends we apply routing via
`page.RouteAsync` post-connect, exactly as the reference does.

### 9.3 Downloads / byte sinks (`Target`)
`download.to` and `screenshot.to` take a `Target`: a caller **presigned upload URL** or a
Crawldad-configured **blob store**. The engine streams bytes **from the backend straight to the
Target** — bytes never buffer into an event, an aggregate, or the response (§14). The engine natively
computes the content identity used by the reference: `sha256` of the stream, `contentId` = first 16
bytes of the SHA-256 as a GUID (identical to `AttachmentHashing.AttachmentIdFromHash`), and
`internalFilename` = `"{contentId}.{ext}"` (identical to `BuildInternalFilename`). Upload is
idempotent (already-present ⇒ `stored:true`), reproducing `handleDownload`'s blob-exists short-circuit.

The `download` result var carries: `{ contentId, sha256, sizeBytes, storedAs, stored }`.
`internalFilename` follows `BuildInternalFilename` = `"{contentId}.{ext}"`, but `{ext}` comes from the
**scraped filename cell** — which can differ from the download's HTTP-suggested name — so the payload
composes it from the scraped `filename` + `contentId` (Appendix B.2), not from the download's own name.
`idempotencyKey?` lets the payload dedupe on `contentId` before re-uploading.

---

## 10. Result and error response shapes

One request → one structured response (streaming variant in §11).

```jsonc
// success
{ "runId": "…", "status": "succeeded",
  "result": <the payload's `result` expression, evaluated>,
  "stats": { "durationMs": …, "steps": …, "requests": …, "cacheHits": …, "downloads": … } }

// failure
{ "runId": "…", "status": "failed",
  "failure": { "class": "terminal"|"retryable-exhausted", "code": "record_not_accessible",
               "message": "Record not accessible (redirected to /LJCMG/Cap/Login.aspx)",
               "atStep": { "index": 2, "kind": "guard" } },
  "partial": <result-so-far if the payload opted into partial emission>,
  "stats": { … } }

// cancelled
{ "runId": "…", "status": "cancelled", "partial": <…>, "stats": { … } }

// auto-upgraded (CD-15): a default (non-async) run still executing at the sync window (§8.4) — the run
// keeps executing on the durable surface; the caller polls GET /runs/{id} (§11) for the terminal shape above
// HTTP 202
{ "runId": "…", "status": "running" }
```

**Output shaping (tension #4): the payload declares the shape.** `result` is an object-literal
expression built from accumulated vars — mirroring the C# `return new RecordScrapedV1 {…}`. We do not
return flat step results for the caller to reassemble; the acceptance criterion is *identical nested
output*, and the payload already has the vars and loops to build it. `SearchEnforcementRecords`'
`result` is `{ newLinks, crawledToEnd, pages }`; `ScrapeEnforcementRecord`'s is the full
`RecordScrapedV1` object literal (Appendix B).

---

## 11. Long-running execution: streaming, cancellation, resumability

The reference runs for minutes; competitors cap at 40–60 s. Long runs are a differentiator, designed
deliberately. Grounded in the Critter Stack (§14): a **run is an event-sourced aggregate** whose step
events *are* the trace, orchestrated by a **Wolverine saga** (the net-new piece the foundation repo
does not itself contain).

- **Progress streaming (SSE).** As the interpreter executes, it appends step events to the run's
  Marten stream *and* publishes an in-process notification; an SSE endpoint per `runId` tails them. On
  (re)connect the client **backfills from the persisted stream** (from last-seen sequence) then
  resumes the live tail — no frames lost across reconnects. Authoritative live state is read from the
  **run's own event stream** (read-your-writes), not the async projection (which lags — the exact
  eventual-consistency split the foundation repo documents); the async projection powers cross-run
  dashboards where lag is fine.
- **Cancellation.** `POST /runs/{id}/cancel` appends `RunCancellationRequested`; the interpreter
  checks a cooperative `CancellationToken` **between steps**, tears the browser session down cleanly
  (you cannot yank mid-step without leaking a backend session), and appends `RunCancelled`. Response
  carries `partial`.
- **Resumability — checkpoint-based, explicit, NOT event-replay.** The load-bearing deviation: a live
  browser session is external stateful IO (cookies, auth, JS heap) that **cannot be rebuilt by
  replaying events**, and steps are generally **not idempotent** (clicking "next" twice is not free).
  So the event stream reconstructs the *record*, never the *execution*. Resume is defined at
  **declared checkpoints** — a `checkpoint` marker the payload places at safe boundaries (a crawl
  frontier position, a pagination cursor) — from which a *fresh* session re-establishes and continues.
  `SearchEnforcementRecords` checkpoints on the page cursor; if the run dies mid-crawl it resumes from
  the last completed page, not from a replayed browser. If the backend provider supports session
  reconnect by id (stored in the saga), we reconnect; otherwise the run is *interrupted* and resumes
  from its last checkpoint. v1 ships checkpoints for the two LJCMG loops; general resumability is a
  payload-authoring concern, documented, not magic.

---

## 12. Security

- **Credential scrubbing at the logging boundary, from day one.** Connect URLs and tokens
  (`?apiKey=…`, `?token=…`, `?signingKey=…`, `connectUrl`) are secrets that appear in the query string
  and are session- or account-draining on leak. They are **never** persisted in events, projections,
  run traces, or logs; a redaction filter at the sink strips known credential params and any
  `wss://…?(apiKey|token|signingKey)=` URL.
  Run traces are a *paid* feature — without scrubbing, credentials would otherwise land in log
  retention by default. This is non-negotiable and tested (a run whose input contains a token asserts
  the token appears in no event/log/trace).
- **Credentials by reference.** Payloads and events store a **credential reference** (an id into a
  secret store / vault), never the secret. The secret is resolved at connect time and lives only in
  the interpreter's memory for the session. `apiKey` mode stores a vault ref; `connectUrl` mode treats
  the whole URL as a one-time secret (still scrubbed).
- **Payload validation.** Every payload is validated against the JSON Schema (structure) *and* a
  semantic pass (every referenced var/frame/input is defined before use; every `loop` has
  `maxIterations`; expression parse + static builtin/arity check; no unknown head keys). Because the
  language cannot `eval`/`fs`/loop-unbounded, a schema-valid payload has a bounded, inspectable
  effect surface — the core safety claim. Validation runs at **save** time (payloads are persistent,
  §14), so bad payloads never reach execution.
- **Resource limits.** Per run: wall-clock cap (saga timeout, §8.4), max steps, max total downloaded
  bytes, max event count, max concurrent runs per tenant, regex size/time guards, expression
  evaluation step budget. Exceeding a limit is a terminal failure with a clear code.
- **Tenant isolation.** One Marten `DatabaseSchemaName`/tenant boundary per customer (Marten
  multi-tenancy); backend sessions are per-run and never shared across tenants; the cross-run asset
  **cache is keyed within region but its *contents* are public web assets** (stylesheets/scripts) — no
  tenant data is cached, so cache sharing does not cross the isolation boundary. Extracted PII and
  screenshots go to per-tenant blob storage (below).
- **PII in an immutable log.** Browser payloads extract PII and screenshots can show it. Events store
  **metadata only** (key names, hashes, blob refs) — never raw extracted PII or bytes. Bulk extracted
  data and screenshots live in **deletable blob storage**, optionally crypto-shredded (encrypt under a
  per-run/subject key; discard the key to honour erasure). The foundation repo flags this exact hazard
  (it stores `Customer.Email` in an event as an admitted simplification); Crawldad cannot.
- **No auth in the reference is deliberate and must not be copied.** Real deployments authenticate the
  tenant; the actor/`By` on any mutating command comes from the authenticated principal, never the
  request body.

---

## 13. Observability model (the paid feature — designed deliberately)

Observability is not bolted on; it *falls out* of modelling the run as an event-sourced aggregate
(§14). The run's event stream **is** the trace.

- **Step-level traces.** Each semantic step emits an event: `StepStarted(index, kind)`,
  `Navigated(url)`, `Clicked(selectorText)`, `Extracted(key, valueRef)`,
  `Downloaded(blobRef, contentType, size, sha256)`, `Waited(kind, ms)`, `LogEmitted(level, message)`,
  `StepFailed(index, error, screenshotRef)`, `RunSucceeded(resultRef)`, `RunFailed(class, code)`,
  `RunCancelled`. Granularity is **semantic** (one event per meaningful action), not per micro-op —
  verbose network/console traces go to structured logs / a blob referenced from the event, keeping
  stream volume bounded and one stream per run.
- **Screenshot-on-failure.** On any `StepFailed`, the interpreter captures a screenshot (unless
  disabled) to blob storage; `StepFailed.screenshotRef` links it. Screenshots get retention/lifecycle
  policies because they can show PII (§12).
- **Replay.** A `RunTimeline` async projection renders the ordered step list with durations, inputs
  (redacted), extracted-value refs, failure + screenshot links, and the exact **pinned payload
  revision + script hash** — so a run is reproducible and a "replay" re-executes the same revision
  against the same inputs. This is the drift story: compare a failing run's trace to a green run's on
  the same revision → target-site drift; compare pinned revision to payload head → payload drift.
- **Aggregate telemetry (the moat).** Because all execution is data and hosted, cross-customer signal
  — which selector broke, on which host, when — accretes in the trace events and the asset cache. That
  is the long-term product, and this model captures it by construction.

---

## 14. Persistence & versioning on the Critter Stack

Direct reuse of the foundation idioms (combined host, config-driven projection lifecycle, one folder
per vertical slice, anemic aggregates + handler-side decisions, `[WolverinePost]`/`[WriteAggregate]`
endpoints, async read-model projections, `ISideEffect` for IO, FluentValidation on both the HTTP and
bus pipelines, `TimeProvider` seam, Alba host reuse + fakes + tracked sessions + 100 % coverage, a
dependency-free `Crawldad.Contracts`). Then the four things the reference lacks: a **saga**, a
**subscription/SSE**, **scheduled timeouts**, and **blob/secret** handling.

### 14.1 Payload = event-sourced aggregate (its stream *is* the version history)
```
PayloadDrafted(name, script, scriptHash, by)     // revision 1
PayloadRevised(script, scriptHash, by, note)     // revision N  ← each revision = one event = one version
PayloadRenamed(name, by) | PayloadArchived(by)
```
- Aggregate `Payload { Id, Name, Head:{Revision, ScriptHash}, Status }`; async `PayloadSummary`
  projection for listing; `/state` returns a Contracts DTO (never the internal aggregate).
- **Any historical version** = `AggregateStreamAsync(id, version:N)`; **audit** (who/when/note) is free
  from event metadata + the clock seam; **drift** = a run's pinned revision/hash vs the payload head.
- Large or frequently-revised scripts: content-address the body in blob storage under `scriptHash`,
  keep `scriptHash` + note in the event (optional; not needed for v1).
- **Rejected:** a plain Marten document (keeps only current state — no history) and event upcasters
  (those version the *serialized event shape*, not the user's documents — see §14.3).

### 14.2 Run = event-sourced aggregate (trace events) + a Wolverine saga (orchestration)
- **Record/observability/replay** = the Run aggregate; its events are the §13 trace.
  `RunStarted(payloadId, payloadRevision, scriptHash, inputRedacted)` **pins the exact revision +
  hash** so editing a payload never mutates historical runs and drift is detectable.
- **Orchestration** = a **Wolverine saga** (Marten-backed saga storage), the net-new piece. It owns
  durable run state: `runId, payloadId+revision, currentStepIndex/checkpoint, backendSessionId,
  cancellationRequested, status`. `StartRun` starts the saga; it drives step execution, appends trace
  events, uses saga `Timeout` messages for the run wall-clock and per-step deadlines, and
  completes/faults. Because messaging is durable (`IntegrateWithWolverine` + `UseDurableLocalQueues`),
  orchestration survives process restarts — the run resumes from its last checkpoint (§11).
- **Command endpoints** (`StartRun`, `CancelRun`) use the request-scoped `[WriteAggregate]` idiom; the
  **long-running executor owns its own Marten sessions** (it is not inside an HTTP request), a
  deliberate departure from the reference's one-synchronous-transaction-per-handler shape.

### 14.3 Two unrelated kinds of "versioning" (do not conflate)
- **Payload-document versioning** (§14.1) — a domain feature (revision history, drift). Its own event
  stream.
- **Event-schema versioning of the trace** — when the *shape* of a `Navigated`/`StepFailed` event
  changes between releases, the foundation's `EventUpcaster<TOld,TCurrent>` (routed by stored name)
  keeps old runs readable. Used exactly as in the reference, per trace-event type.

### 14.4 Suggested slice layout
```
Features/Payloads/   Payload.cs, Events.cs, {Draft,Revise,Archive}PayloadEndpoint.cs,
                     PayloadSummaryProjection.cs, PayloadQueries.cs, PayloadModule.cs, Validators.cs, Versioning/
Features/Runs/       Run.cs, Events.cs (the step trace), StartRunEndpoint.cs, CancelRunEndpoint.cs,
                     RunExecutorSaga.cs, Interpreter/ (nodes, expressions, selectors),
                     RunTimelineProjection.cs, RunQueries.cs (by-id + SSE), RunModule.cs
Infrastructure/      IBrowserBackend + Browserbase/Browserless/SelfHosted/Fake,
                     IBlobStore/IDownloadSink, IWebhookSender (ISideEffect), ISecretStore, TimeProvider
Contracts/           Payloads/… Runs/…  (commands = HTTP body + Wolverine message; DTOs; enums; ContractsJson)
```

---

## 15. Open tensions — explicit resolutions

**#1 Mid-execution callbacks.** The reference has two: `goToNextPageCallback` (host stops pagination
on data seen so far) and `handleDownload` (streams files out mid-run). A single request/response API
cannot call back. **Resolution — three complementary mechanisms:**
(a) **Inputs + declarative predicates** replace both callbacks for LJCMG. The real
`goToNextPageCallback` (HistoricalCrawler:85-104) stops when a result URL is in the caller's known set
`dayCrawl.Links`, modulo a `crawledToEnd` flag — so pass `knownUrls` + `priorCrawlComplete` as inputs
and express the stop declaratively (Appendix B reproduces the exact `!crawledToEnd` nuance).
(b) **Downloads stream to a caller `Target`** (presigned URL / blob store), engine-hashed and
idempotent — reproducing `handleDownload` without bytes round-tripping the caller's process.
(c) **SSE streaming + a cancel control channel** is the general escape valve for genuinely dynamic
per-item host logic and satisfies the long-running/observability requirement.
**Cost:** the caller must pre-declare known-values and storage targets; host logic richer than a
predicate needs the streaming path (shipped after the declarative path). This is a good trade: it
removes N round-trips for the reference workload and keeps the one-request-in shape.

**#2 Expression-language scope.** Resolved in §7: pure/total CEL-like expressions with an enumerated
builtin surface + read-only DOM reads; all iteration/mutation is structural and capped. Appendix C
proves the surface is exactly LJCMG-sized. Too little (BrowserQL) can't branch on text; too much
(Zyte/Firecrawl) means "drop to JS" and forfeits safety. We sit precisely on the line.

**#3 `evaluate` / arbitrary JS.** **Do not ship in v1.** The reference uses zero `page.evaluate()`
(it uses `addStyleTag`, which is data, not code), so the acceptance criterion does not need it.
Shipping `eval` forfeits the "safe because it's data" thesis and the telemetry moat (opaque JS is
un-analyzable for drift). Position: the enumerated language covers the reference; a future customer
who truly needs DOM-side JS gets a separately-flagged, sandboxed, telemetry-tainting, off-by-default
capability — or is not our customer. We ship `addStyleTag`, not `evaluate`.

**#4 Output shaping.** **The payload declares the output shape** (§10): accumulate into vars/lists,
end with a `result` object-literal expression mirroring `return new RecordScrapedV1 {…}`. Flat
step-results the caller reassembles would fail the "identical nested output" criterion and push the
hardest assembly back to the caller.

**#5 `@puppeteer/replay` interop.** See §16: **ignore as the native schema; support as an import /
authoring on-ramp.** It is linear with no control flow — a strict subset — so adopting it natively
can't express our headline features; but importing it buys the free Chrome DevTools Recorder authoring
tool and a migration path.

---

## 16. `@puppeteer/replay` interop

Verified against `puppeteer/replay` `src/Schema.ts`: top level `UserFlow { title, steps[], timeout?,
selectorAttribute? }`; **14 step types** (`change, click, close, customStep, doubleClick,
emulateNetworkConditions, hover, keyDown, keyUp, navigate, scroll, setViewport, waitForElement,
waitForExpression`); selectors are `selectors: Selector[]` where `Selector = string | string[]`
across five strategies (`css, aria, text, xpath, pierce`) — an ordered list of alternatives tried
until one resolves; frames by `FrameSelector = number[]`. **No conditionals, loops, or branching.**

**Decision:** ship a one-way **importer** (post-MVP, §Not-in-MVP) that lifts a linear recording into a
Crawldad payload — `navigate→goto`, `click/doubleClick/hover→click`, `change→fill`,
`keyDown/keyUp→press`, `waitForElement→waitFor`, `waitForExpression→waitFor`-on-`cond`,
`setViewport/emulateNetworkConditions→config`, `scroll→scroll`, `close→(end)`, `customStep→(mapped or
flagged)`. Adopt its **multi-strategy selector** idea into `Sel` where free. Do **not** constrain the
native schema to it: any Crawldad control flow is a pure extension beyond the recorder's vocabulary,
and round-trip fidelity for Crawldad-only features is a non-goal. This buys the authoring tool without
capping the language at "linear."

---

## Appendix A — LJCMG capability coverage matrix

Every item from the brief's Required Capability Inventory. **S** = supported in v1; **S\*** = supported
but not exercised by the acceptance suite.

| Capability | v1 | How |
|---|---|---|
| goto, waitForLoadState incl NetworkIdle, per-step timeout | S | `goto`, `waitForLoadState`, `timeoutMs` (§5) |
| wait for element hidden (`#divGlobalLoading`) | S | `waitFor state:"hidden"` |
| wait for matching network request (URL-prefix + method wrapping a click) | S | `waitForRequest {urlPrefix, method, trigger}` |
| wait on a computed condition (page-number regex) | S | `waitFor` with `filter.hasTextRegex:"^${nextPageNumber}$"` |
| CSS + XPath; nth/first/count/all | S / XPath **S\*** | `Sel` + `locate` forms; builtins `count/first/nth`; `forEach in:` = `.All` |
| getByTitle / role locators | S | `Sel {title}` / `{role,name}` |
| locator filter by regex on text | S | `Sel.filter.hasTextRegex` |
| iframe traversal (frameLocator) | S | `frame` handle + `in:` |
| click, fill, clear | S | `click`/`fill`/`clear` |
| addStyleTag | S | `addStyleTag` |
| screenshot | **S\*** | `screenshot` (absent in ref; used by observability §13) |
| textContent / innerText / innerHTML / getAttribute | S | DOM builtins `text/innerText/innerHtml/attr` |
| trim/replace/split(incl innerHTML on `<br>`)/substring/regex | S | string builtins (§7.2, Appendix C) |
| assemble nested object | S | object/array literals + `push`; `result` (§10) |
| download: click-triggered, long timeout, stream to storage, content hash, delete temp | S | `download` → `Target`, engine hash = `AttachmentHashing`, temp lifecycle internal (§9.3) |
| download inside nested pagination loop | S | `download` inside `forEach`/`loop` |
| conditional field fill | S | `if` around `fill` |
| conditional click on existence (`count>0`) | S | `if cond:"count('#imgASI') > 0"` |
| nested loops with index arithmetic (`k*2+1`) | S | nested `loop`/`forEach` + `${k*2+1}` interpolation |
| two independent pagination loops | S | two `loop`s (one inside a `frame`) |
| loop safety cap (50 pages + warning) | S | `maxIterations:50, onMaxIterations:"warn"` |
| row-range arithmetic (`i=3 .. count-2`) | S | `for from:"3" to:"count(rows)-2" exclusiveTo` |
| continue semantics | S | `continue {when}` |
| guard/abort with typed failure, no retry | S | `guard`/`fail class:"terminal"`; `retryOn` excludes it |
| branch on extracted text (heading; unknown = hard error) | S | `switch` + `default:[{fail terminal}]` |
| branch on counted DOM shape (3/4/5 `<br>`) | S | `if` on `length(split(innerHtml,'<br>'))` |
| early termination driven by extracted data | S | `knownUrls` input + `break when any(...)` (tension #1) |
| retry: 15×5 s, only timeouts + Page crashed; reopen page preserving context | S | `config.retry` (+ §3.6 fix) |
| request interception: host/resource abort; cache stylesheet/script/html/js; global throttle | S | `config.route` (§8.1) |
| BypassCSP, `--disable-web-security`, 120 s default | S | `config.context/launch/defaultTimeoutMs` |
| mid-execution callbacks | S (reshaped) | tension #1 (a)(b)(c) |

Deferred to post-MVP (not required by acceptance): `@puppeteer/replay` importer; general
(non-checkpoint) resumability; webhooks (the `SendWebhook` `ISideEffect` is designed but the LJCMG
host uses inputs + storage targets, not webhooks); non-CDP exotic backends.

---

## Appendix B — Worked payloads (the proof)

Both operations as complete payloads. Selectors and string surgery are byte-faithful to
`LJCMGClient.cs`. `comment` nodes (`{ "comment": … }`) are no-op annotations (§6), ignored at execution.

### B.1 `SearchEnforcementRecords` (maps `:75-175` + HistoricalCrawler `:81-105`)

```jsonc
{
  "crawldad": "1",
  "name": "ljcmg.enforcement.search",
  "inputs": {
    "backend":            { "type": "backend",  "required": true },
    "startDate":          { "type": "string",   "required": false },   // "MM/dd/yyyy"
    "endDate":            { "type": "string",   "required": false },
    "knownUrls":          { "type": "array",    "default": [] },
    "priorCrawlComplete": { "type": "boolean",  "default": false }
  },
  "config": {
    "backend": "input.backend",
    "defaultTimeoutMs": 120000,
    "launch":  { "args": ["--disable-web-security"] },
    "context": { "bypassCsp": true },
    "route": {
      "blockHosts": ["www.datadoghq-browser-agent.com","cdn.walkme.com","cdn.gtranslate.net",
                     "csp-report.browser-intake-datadoghq.com","ec.walkme.com",
                     "rum.browser-intake-datadoghq.com","connect.facebook.net","fonts.googleapis.com"],
      "blockResourceTypes": ["image","media","font"],
      "cacheResourceTypes": ["stylesheet","script"],
      "cacheUrlSuffixes":   [".html",".js"],
      "throttle": { "minIntervalMs": 2000 }
    },
    "retry": { "maxAttempts": 15, "delayMs": 5000, "backoff": "constant",
               "retryOn": ["timeout","pageCrashed"], "onPageCrashed": "reopenPage" }
  },
  "vars": { "pages": [], "newLinks": [], "crawledToEnd": "input.priorCrawlComplete", "hasMorePages": false },
  "steps": [
    { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
    { "waitForLoadState": { "state": "networkidle" } },

    { "if": { "cond": "!isNullOrWhitespace(input.startDate)", "then": [
        { "click": { "selector": "#ctl00_PlaceHolderMain_generalSearchForm_txtGSStartDate" } },
        { "clear": { "selector": "#ctl00_PlaceHolderMain_generalSearchForm_txtGSStartDate" } },
        { "fill":  { "selector": "#ctl00_PlaceHolderMain_generalSearchForm_txtGSStartDate", "value": "input.startDate" } }
    ] } },
    { "if": { "cond": "!isNullOrWhitespace(input.endDate)", "then": [
        { "click": { "selector": "#ctl00_PlaceHolderMain_generalSearchForm_txtGSEndDate" } },
        { "clear": { "selector": "#ctl00_PlaceHolderMain_generalSearchForm_txtGSEndDate" } },
        { "fill":  { "selector": "#ctl00_PlaceHolderMain_generalSearchForm_txtGSEndDate", "value": "input.endDate" } }
    ] } },

    { "waitForRequest": {
        "urlPrefix": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx", "method": "POST",
        "trigger": [ { "click": { "selector": "#ctl00_PlaceHolderMain_btnNewSearch" } } ] } },
    { "waitFor": { "selector": "#divGlobalLoading", "state": "hidden" } },

    { "loop": { "maxIterations": 100000, "while": "hasMorePages", "do": [

        { "set": { "var": "pageResults", "value": "[]" } },
        { "locate": { "var": "rows", "selector": "#ctl00_PlaceHolderMain_dgvPermitList_gdvPermitList tr" } },

        { "comment": "for (i=3; i < count-2; i++)  — skip 3 header rows and 2 footer rows (:127)" },
        { "loop": { "maxIterations": 100000,
            "for": { "var": "i", "from": "3", "to": "count(rows) - 2", "exclusiveTo": true },
            "do": [
              { "locate": { "var": "row", "from": "rows", "nth": "i" } },
              { "push": { "into": "pageResults", "value":
                "{ url: '' + urlScheme(pageUrl()) + '://' + urlHost(pageUrl()) + trim(coalesce(attr(row,'td:nth-child(3) a','href'),'')), data: { date: trim(coalesce(text(row,'td:nth-child(2)'),'')), recordNumber: trim(coalesce(text(row,'td:nth-child(3) a'),'')), recordType: trim(coalesce(text(row,'td:nth-child(4)'),'')), address: trim(coalesce(text(row,'td:nth-child(5)'),'')), status: trim(coalesce(text(row,'td:nth-child(6)'),'')), shortNotes: trim(coalesce(text(row,'td:nth-child(7)'),'')) } }" } }
            ] } },

        { "set": { "var": "hasMorePages", "value": "count('table.aca_pagination td:last-child a') > 0" } },
        { "if": { "cond": "!hasMorePages", "then": [ { "set": { "var": "crawledToEnd", "value": "true" } } ] } },

        { "comment": "goToNextPageCallback reproduced (HistoricalCrawler:85-104):" },
        { "comment": "  empty page => stop" },
        { "break": { "when": "count(pageResults) == 0" } },

        { "comment": "  accumulate new links, stop scanning at the first already-known url" },
        { "set": { "var": "hitKnown", "value": "false" } },
        { "loop": { "maxIterations": 100000,
            "for": { "var": "j", "from": "0", "to": "count(pageResults)", "exclusiveTo": true },
            "do": [
              { "if": { "cond": "any(input.knownUrls, u, u == pageResults[j].url)",
                  "then": [ { "set": { "var": "hitKnown", "value": "true" } }, { "break": {} } ],
                  "else": [ { "push": { "into": "newLinks", "value": "pageResults[j].url" } } ] } }
            ] } },
        { "push": { "into": "pages", "value": "pageResults" } },

        { "comment": "  shouldContinue = hitKnown ? !crawledToEnd : hasMorePages ; break when false" },
        { "break": { "when": "hitKnown ? crawledToEnd : !hasMorePages" } },

        { "comment": "  else click next + wait postback + overlay hidden (:158-164)" },
        { "waitForRequest": {
            "urlPrefix": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx", "method": "POST",
            "trigger": [ { "click": { "selector": "table.aca_pagination td:last-child a" } } ] } },
        { "waitFor": { "selector": "#divGlobalLoading", "state": "hidden" } }
    ] } }
  ],
  "result": "{ newLinks: distinct(newLinks), crawledToEnd: crawledToEnd, pages: pages }"
}
```

`hasMorePages` is unset on the first `while` test → falsy → the loop body runs once before the test,
reproducing C# `do…while(hasMorePages)`. `pages` is the list of per-page row-arrays the reference
delivered to the callback page-by-page; `newLinks`/`crawledToEnd` are the host's assembled outputs.
The `break when: "hitKnown ? crawledToEnd : !hasMorePages"` is the exact negation of the callback's
return (`hitKnown ? !crawledToEnd : hasMorePages`).

### B.2 `ScrapeEnforcementRecord` (maps `:177-725` → `RecordScrapedV1`)

```jsonc
{
  "crawldad": "1",
  "name": "ljcmg.enforcement.scrape",
  "inputs": {
    "backend":         { "type": "backend",       "required": true },
    "link":            { "type": "string",        "required": true },   // = Link.Id (goto + relative-link base)
    "publishDate":     { "type": "string",        "required": false },  // "yyyy-MM-dd" (= Link.PublishDate)
    "attachmentStore": { "type": "storageTarget", "required": true }    // where bytes stream (handleDownload)
  },
  "config": {
    "backend": "input.backend",
    "defaultTimeoutMs": 120000,
    "launch":  { "args": ["--disable-web-security"] },
    "context": { "bypassCsp": true },
    "route": {
      "blockHosts": ["www.datadoghq-browser-agent.com","cdn.walkme.com","cdn.gtranslate.net",
                     "csp-report.browser-intake-datadoghq.com","ec.walkme.com",
                     "rum.browser-intake-datadoghq.com","connect.facebook.net","fonts.googleapis.com"],
      "blockResourceTypes": ["image","media","font"],
      "cacheResourceTypes": ["stylesheet","script"],
      "cacheUrlSuffixes":   [".html",".js"],
      "throttle": { "minIntervalMs": 2000 }
    },
    "retry": { "maxAttempts": 15, "delayMs": 5000, "backoff": "constant",
               "retryOn": ["timeout","pageCrashed"], "onPageCrashed": "reopenPage" }
  },
  "vars": {
    "violations": [], "parcels": [], "locations": [], "owners": [], "processingStatus": [],
    "attachments": [], "relatedRecords": [], "description": "''", "parentRecordNumber": "''",
    "hasMoreAttachmentPages": false
  },
  "steps": [
    { "goto": { "url": "${input.link}" } },
    { "waitForLoadState": { "state": "load" } },

    { "comment": "GUARD — redirect to Login/Error is terminal, NOT retried (:203-206)" },
    { "guard": { "cond": "contains(lower(pageUrl()), 'capdetail.aspx')",
        "elseFail": { "class": "terminal", "code": "record_not_accessible",
                      "message": "Record not accessible (redirected to ${urlPath(pageUrl())}): ${input.link}" } } },

    { "addStyleTag": { "content": ".record-detail .record-main-section .record-tab-content { display: block !important; }" } },
    { "click": { "selector": "#imgMoreDetail" } },
    { "if": { "cond": "count('#imgASI')  > 0", "then": [ { "click": { "selector": "#imgASI"  } } ] } },
    { "if": { "cond": "count('#imgASIT') > 0", "then": [ { "click": { "selector": "#imgASIT" } } ] } },
    { "click": { "selector": "#imgParcel" } },
    { "click": { "selector": { "title": "Record Info menu, press tab to expand" } } },
    { "click": { "selector": { "title": "Attachments" } } },
    { "waitForLoadState": { "state": "networkidle", "timeoutMs": 360000 } },

    { "comment": "===== LOCATION (:229-268) =====" },
    { "if": { "cond": "count('#tbl_worklocation') > 0", "then": [
      { "locate": { "var": "addrRows", "selector": "#tbl_worklocation tr:first-child, #tbl_worklocation tr[tips='tr_additional_locations']" } },
      { "forEach": { "in": "addrRows", "as": "addressRow", "maxIterations": 100000, "do": [
        { "locate": { "var": "addressBlock", "base": "addressRow", "selector": "td:nth-child(2)" } },
        { "set": { "var": "html",         "value": "innerHtml(addressBlock)" } },
        { "set": { "var": "addressLines", "value": "length(split(html, '<br>'))" } },
        { "set": { "var": "address1",     "value": "trim(replace(coalesce(text(addressBlock,'span:first-child'),''), '*', ''))" } },
        { "set": { "var": "address2",     "value": "''" } },
        { "set": { "var": "cityStateZip", "value": "''" } },
        { "switch": { "cases": [
            { "when": "addressLines == 3", "do": [
                { "set": { "var": "cityStateZip", "value": "split(split(trim(html),'<br>')[1], '<span')[0]" } } ] },
            { "when": "addressLines == 4", "do": [
                { "set": { "var": "address2",     "value": "split(trim(html),'<br>')[1]" } },
                { "set": { "var": "cityStateZip", "value": "split(split(trim(html),'<br>')[2], '<span')[0]" } } ] },
            { "when": "addressLines == 5", "do": [
                { "set": { "var": "address2",     "value": "split(trim(html),'<br>')[1]" } },
                { "set": { "var": "cityStateZip", "value": "split(trim(html),'<br>')[2]" } } ] }
          ], "default": [
            { "log": { "level": "warning", "message": "Exceptional location address lines (${addressLines}): ${input.link}" } }
          ] } },
        { "push": { "into": "locations", "value": "{ address1: trim(address1), address2: trim(address2), cityStateZip: trim(cityStateZip) }" } }
      ] } }
    ] } },

    { "comment": "===== RECORD DETAILS (:270-357) =====" },
    { "set": { "var": "recordNumber", "value": "coalesce(text('#ctl00_PlaceHolderMain_lblPermitNumber'), '')" } },
    { "guard": { "cond": "!isNullOrWhitespace(recordNumber)",
        "elseFail": { "class": "terminal", "code": "missing_record_number", "message": "Failed to locate record number: ${input.link}" } } },
    { "set": { "var": "recordType", "value": "coalesce(text('#ctl00_PlaceHolderMain_lblPermitType'), '')" } },
    { "guard": { "cond": "!isNullOrWhitespace(recordType)",
        "elseFail": { "class": "terminal", "code": "missing_record_type", "message": "Failed to locate record type: ${input.link}" } } },
    { "set": { "var": "recordStatus", "value": "count('#ctl00_PlaceHolderMain_lblRecordStatus') > 0 ? coalesce(text('#ctl00_PlaceHolderMain_lblRecordStatus'),'') : ''" } },
    { "set": { "var": "projectName", "value":
        "count('#tableCapTreeList') == 0 ? recordType : (count('#tableCapTreeList tr.ACA_RelatedCap_Highlight > td:nth-child(3)') > 0 ? trim(coalesce(text({ css: '#tableCapTreeList tr.ACA_RelatedCap_Highlight > td:nth-child(3)', first: true }),'')) : '')" } },
    { "set": { "var": "recordDate", "value": "coalesce(input.publishDate, '')" } },

    { "locate": { "var": "ownerDetails", "selector": "#ctl00_PlaceHolderMain_PermitDetailList1_TBPermitDetailTest > tbody > tr > td" } },
    { "if": { "cond": "count(ownerDetails) > 0", "then": [
      { "forEach": { "in": "ownerDetails", "as": "detail", "maxIterations": 100000, "do": [
        { "set": { "var": "heading", "value": "trim(coalesce(text(detail,'h1'),''))" } },
        { "switch": { "cases": [
          { "when": "startsWith(lower(heading), 'description')", "do": [
              { "set": { "var": "description", "value": "trim(replace(coalesce(text(detail,'table td:nth-child(2)'),''), '*', ''))" } } ] },
          { "when": "startsWith(lower(heading), 'licensed professional')", "do": [
              { "comment": "TODO in the reference — intentional no-op (:301-304)" } ] },
          { "when": "startsWith(lower(heading), 'owner')", "do": [
              { "locate": { "var": "ownerBlocks", "base": "detail", "selector": "table.table_child table.table_child" } },
              { "if": { "cond": "count(ownerBlocks) > 1",
                  "then": [ { "log": { "level": "warning", "message": "MULTIPLE OWNERS: ${input.link}" } } ] } },
              { "forEach": { "in": "ownerBlocks", "as": "ownerBlock", "maxIterations": 100000, "do": [
                { "set": { "var": "name",      "value": "trim(replace(coalesce(text(ownerBlock,'table tr:first-child td'),''), '*', ''))" } },
                { "locate": { "var": "ownerRows", "base": "ownerBlock", "selector": "table tr" } },
                { "set": { "var": "ownerLines","value": "count(ownerRows)" } },
                { "if": { "cond": "length(name) >= 2 && substring(name,1,2) == ')'",
                    "then": [ { "set": { "var": "name", "value": "trim(substring(name,2))" } } ] } },
                { "continue": { "when": "isNullOrWhitespace(name) && ownerLines == 1" } },
                { "set": { "var": "ownerAddr1", "value": "exists({ base: 'ownerBlock', css: 'table tr:nth-child(2) td' }) ? coalesce(text(ownerBlock,'table tr:nth-child(2) td'),'') : ''" } },
                { "set": { "var": "ownerAddr2", "value": "''" } },
                { "switch": { "cases": [
                    { "when": "ownerLines == 4", "do": [
                        { "set": { "var": "ownerAddr2", "value": "coalesce(text(ownerBlock,'table tr:nth-child(3) td'),'')" } } ] },
                    { "when": "ownerLines < 2 || ownerLines > 4", "do": [
                        { "log": { "level": "warning", "message": "EXCEPTIONAL OWNER LINE COUNT: ${input.link}" } } ] }
                  ] } },
                { "set": { "var": "ownerCsz", "value": "coalesce(text(ownerBlock,'table tr:last-child td'),'')" } },
                { "push": { "into": "owners", "value": "{ name: trim(name), address1: trim(ownerAddr1), address2: trim(ownerAddr2), cityStateZip: trim(ownerCsz) }" } }
              ] } }
          ] }
        ], "default": [
          { "fail": { "class": "terminal", "code": "unknown_heading", "message": "UNKNOWN HEADING: ${heading} AT ${input.link}" } }
        ] } }
      ] } }
    ] } },

    { "comment": "===== APPLICATION INFORMATION TABLE (:359-425) =====" },
    { "if": { "cond": "count('#trASITList > td > table') > 0", "then": [
      { "loop": { "maxIterations": 100000,
        "for": { "var": "i", "from": "1", "to": "count('#trASITList > td > table')", "inclusiveTo": true },
        "do": [
          { "set": { "var": "asitHeading", "value": "trim(coalesce(text('#trASITList > td > table:nth-child(' + i + ') tbody > tr:first-child'),''))" } },
          { "switch": { "cases": [
            { "when": "equalsIgnoreCase(asitHeading, 'VIOLATIONS')", "do": [
              { "loop": { "maxIterations": 100000,
                "for": { "var": "j", "from": "2", "to": "count('#trASITList > td > table:nth-child(' + i + ') tbody > tr')", "inclusiveTo": true },
                "do": [
                  { "set": { "var": "v", "value": "{ title:'', status:'', code:'', dueDate:'', inspectorComment:'', referralType:'', referralDate:'', workOrderType:'', referralResult:'' }" } },
                  { "loop": { "maxIterations": 100000,
                    "for": { "var": "k", "from": "0", "to": "count('#trASITList > td > table:nth-child(' + i + ') tbody > tr:nth-child(' + j + ') > td > div > div.MoreDetail_ItemCol1')", "exclusiveTo": true },
                    "do": [
                      { "set": { "var": "key",   "value": "trim(coalesce(text('#trASITList > td > table:nth-child(' + i + ') tbody > tr:nth-child(' + j + ') > td > div > div:nth-child(' + (k*2+1) + ')'),''))" } },
                      { "set": { "var": "value", "value": "trim(coalesce(text('#trASITList > td > table:nth-child(' + i + ') tbody > tr:nth-child(' + j + ') > td > div > div:nth-child(' + (k*2+2) + ')'),''))" } },
                      { "switch": { "cases": [
                        { "when": "startsWith(lower(key),'violation')",    "do": [ { "set": { "var": "v", "path": "title",            "value": "value" } } ] },
                        { "when": "startsWith(lower(key),'status')",       "do": [ { "set": { "var": "v", "path": "status",           "value": "value" } } ] },
                        { "when": "startsWith(lower(key),'code')",         "do": [ { "set": { "var": "v", "path": "code",             "value": "value" } } ] },
                        { "when": "startsWith(lower(key),'due date')",     "do": [ { "set": { "var": "v", "path": "dueDate",          "value": "value" } } ] },
                        { "when": "startsWith(lower(key),'inspector co')", "do": [ { "set": { "var": "v", "path": "inspectorComment", "value": "value" } } ] },
                        { "when": "startsWith(lower(key),'referral typ')", "do": [ { "set": { "var": "v", "path": "referralType",     "value": "value" } } ] },
                        { "when": "startsWith(lower(key),'referral dat')", "do": [ { "set": { "var": "v", "path": "referralDate",     "value": "value" } } ] },
                        { "when": "startsWith(lower(key),'work order t')", "do": [ { "set": { "var": "v", "path": "workOrderType",    "value": "value" } } ] },
                        { "when": "startsWith(lower(key),'referral res')", "do": [ { "set": { "var": "v", "path": "referralResult",   "value": "value" } } ] }
                      ] } }
                    ] } },
                  { "push": { "into": "violations", "value": "v" } }
                ] } }
            ] },
            { "when": "equalsIgnoreCase(asitHeading, 'CODE ENFORCEMENT BOARD')", "do": [
              { "comment": "ignored in the reference (:414-417)" } ] }
          ], "default": [
            { "log": { "level": "warning", "message": "Unknown heading in application information table" } }
          ] } }
        ] } }
    ] } },

    { "comment": "===== PARCEL LIST (:427-453) =====" },
    { "if": { "cond": "count('#trParcelList table') > 0", "then": [
      { "if": { "cond": "count('#trParcelList table') > 1", "then": [
          { "log": { "level": "warning", "message": "MULTIPLE PARCELS: ${input.link}" } } ] } },
      { "locate": { "var": "parcelBlocks", "selector": "#trParcelList table" } },
      { "forEach": { "in": "parcelBlocks", "as": "parcelBlock", "maxIterations": 100000, "do": [
        { "push": { "into": "parcels", "value":
          "{ parcelNumber: trim(split(coalesce(replace(text(parcelBlock,'tr:first-child .MoreDetail_ItemCol2'),'*',''),':'),':')[1]), block: trim(split(coalesce(text(parcelBlock,'tr:nth-child(2) .MoreDetail_ItemCol2'),':'),':')[1]), lot: trim(split(coalesce(text(parcelBlock,'tr:nth-child(3) .MoreDetail_ItemCol2 div:first-child'),':'),':')[1]), subdivision: trim(split(coalesce(text(parcelBlock,'tr:nth-child(3) .MoreDetail_ItemCol2 div:last-child'),':'),':')[1]) }" } }
      ] } }
    ] } },

    { "comment": "===== PROCESSING STATUS (:455-529) — paired rows i*2 / i*2+1 =====" },
    { "locate": { "var": "processingRows", "selector": "#divProcessingTable > table > tbody > tr" } },
    { "set": { "var": "procRowCount", "value": "count(processingRows)" } },
    { "loop": { "maxIterations": 100000,
      "for": { "var": "i", "from": "0", "to": "procRowCount", "exclusiveTo": true },
      "do": [
        { "comment": "true bound is i*2+1 <= procRowCount (:462); break enforces it before any row access" },
        { "break": { "when": "i*2+1 > procRowCount" } },
        { "locate": { "var": "headRow", "from": "processingRows", "nth": "i*2" } },
        { "comment": "heading = ... ?? throw (:464). text() yields null when the cell is absent." },
        { "set": { "var": "procHeading", "value": "text(headRow,'td:last-child')" } },
        { "guard": { "cond": "procHeading != null",
            "elseFail": { "class": "terminal", "code": "processing_heading_unparseable", "message": "Heading cannot be parsed" } } },
        { "if": { "cond": "exists({ base:'headRow', css:'a' })", "then": [
          { "locate": { "var": "expandLink", "base": "headRow", "selector": "a" } },
          { "click": { "selector": "expandLink" } },
          { "locate": { "var": "detailRow", "from": "processingRows", "nth": "i*2+1" } },
          { "locate": { "var": "lineBlocks", "base": "detailRow", "selector": "td:last-child > table:first-child > tbody > tr:first-child td:last-child td" } },
          { "continue": { "when": "count(lineBlocks) == 0" } },
          { "set": { "var": "lines", "value": "[]" } },
          { "forEach": { "in": "lineBlocks", "as": "lineBlock", "maxIterations": 100000, "do": [
            { "forEach": { "in": "split(innerText(lineBlock), '\n')", "as": "ln", "maxIterations": 100000, "do": [
              { "push": { "into": "lines", "value": "trim(ln)" } }
            ] } }
          ] } },
          { "if": { "cond": "count(lines) > 2", "then": [
              { "log": { "level": "warning", "message": "Processing status lines > 2: ${input.link}" } } ] } },
          { "set": { "var": "due",        "value": "replace(trim(split(lines[0],',')[0]), 'Due on ', '')" } },
          { "set": { "var": "assignedTo", "value": "replace(trim(split(lines[0],',')[1]), 'assigned to ', '')" } },
          { "set": { "var": "markedAs",   "value": "replace(trim(split(lines[1],' on ')[0]), 'Marked as ', '')" } },
          { "set": { "var": "markedOn",   "value": "trim(split(split(lines[1],' on ')[1], ' by ')[0])" } },
          { "set": { "var": "markedBy",   "value": "trim(split(split(lines[1],' on ')[1], ' by ')[1])" } },
          { "set": { "var": "addtl", "value": "{}" } },
          { "if": { "cond": "exists({ base:'detailRow', css:'td:last-child > table:first-child > tbody > tr:nth-child(2) td:last-child tr' })", "then": [
            { "locate": { "var": "addtlRows", "base": "detailRow", "selector": "td:last-child > table:first-child > tbody > tr:nth-child(2) td:last-child tr" } },
            { "forEach": { "in": "addtlRows", "as": "ar", "maxIterations": 100000, "do": [
              { "set": { "var": "h", "value": "trim(coalesce(text(ar,'td:first-child'),''))" } },
              { "set": { "var": "b", "value": "trim(coalesce(text(ar,'td:last-child'),''))" } },
              { "if": { "cond": "!isNullOrWhitespace(h) && !isNullOrWhitespace(b)",
                  "then": [ { "set": { "var": "addtl", "path": "[${endsWith(h,':') ? substring(h,0,length(h)-1) : h}]", "value": "b" } } ],
                  "else": [ { "log": { "level": "warning", "message": "Could not parse additional comment lines: ${input.link}" } } ] } }
            ] } }
          ] } },
          { "push": { "into": "processingStatus", "value": "{ category: procHeading, dueDate: due, assignedTo: assignedTo, status: markedAs, statusDate: markedOn, statusBy: markedBy, lines: addtl }" } }
        ] } }
      ] } },

    { "comment": "===== ATTACHMENTS (:531-623) — iframe + capped pagination + downloads =====" },
    { "frame": { "var": "attFrame", "selector": "#ctl00_PlaceHolderMain_attachmentEdit_iframeAttachmentList" } },
    { "set": { "var": "attPagesVisited", "value": "0" } },
    { "loop": { "maxIterations": 100000, "while": "hasMoreAttachmentPages", "do": [
      { "locate": { "var": "attRows", "in": "attFrame", "selector": "#attachmentList_gdvAttachmentList > tbody > tr:not(.ACA_Table_Pages)" } },
      { "if": { "cond": "count(attRows) > 1", "then": [
        { "loop": { "maxIterations": 100000,
          "for": { "var": "i", "from": "1", "to": "count(attRows)", "exclusiveTo": true },
          "do": [
            { "locate": { "var": "attRow", "in": "attFrame", "selector": "#attachmentList_gdvAttachmentList > tbody > tr:nth-child(${i+1})" } },
            { "continue": { "when": "equalsIgnoreCase(trim(coalesce(text(attRow),'')), 'No records found.')" } },
            { "locate": { "var": "fileLink", "base": "attRow", "selector": "td:first-child a" } },
            { "continue": { "when": "count(fileLink) == 0" } },
            { "set": { "var": "filename",     "value": "trim(coalesce(text(attRow,'td:first-child'),''))" } },
            { "set": { "var": "attType",      "value": "trim(coalesce(text(attRow,'td:nth-child(5)'),''))" } },
            { "set": { "var": "attSize",      "value": "trim(coalesce(text(attRow,'td:nth-child(6)'),''))" } },
            { "set": { "var": "latestUpdate", "value": "trim(coalesce(text(attRow,'td:nth-child(7)'),''))" } },
            { "comment": "download hashes the bytes and streams to the store; idempotent by content identity (§9.3)" },
            { "download": { "trigger": [ { "click": { "selector": "fileLink" } } ],
                            "to": "input.attachmentStore", "timeoutMs": 180000, "var": "dl" } },
            { "if": { "cond": "dl.stored",
                "then": [ { "push": { "into": "attachments", "value":
                  "{ attachmentId: dl.contentId, filename: filename, internalFilename: string(dl.contentId) + (contains(filename,'.') ? '.' + substringAfterLast(filename,'.') : ''), type: attType, size: attSize, latestUpdate: latestUpdate }" } } ],
                "else": [ { "log": { "level": "warning", "message": "Handling attachment failed: ${input.link}: ${filename}" } } ] } }
          ] } }
      ] } },
      { "set": { "var": "attPagesVisited", "value": "attPagesVisited + 1" } },
      { "locate": { "var": "nextAtt", "in": "attFrame", "selector": "#attachmentList_gdvAttachmentList > tbody > tr.ACA_Table_Pages table.aca_pagination > tbody > tr > td:last-child > a" } },
      { "set": { "var": "hasNextAttLink",          "value": "count(nextAtt) > 0" } },
      { "set": { "var": "hasMoreAttachmentPages",  "value": "hasNextAttLink && attPagesVisited < 50" } },
      { "if": { "cond": "hasMoreAttachmentPages", "then": [
          { "set": { "var": "nextPageNumber", "value": "string(attPagesVisited + 1)" } },
          { "click": { "selector": "nextAtt" } },
          { "waitFor": { "in": "attFrame",
              "selector": { "css": "table.aca_pagination span.SelectedPageButton", "filter": { "hasTextRegex": "^${nextPageNumber}$" } },
              "timeoutMs": 360000 } }
        ], "else": [
          { "if": { "cond": "hasNextAttLink", "then": [
              { "log": { "level": "warning", "message": "Attachment pagination hit safety cap (50 pages) for ${input.link}" } } ] } }
        ] } }
    ] } },

    { "comment": "===== RELATED RECORDS (:625-697) — indentation-based parent resolution =====" },
    { "locate": { "var": "relatedBlocks", "selector": "#tableCapTreeList > tbody > tr:not(:first-child)" } },
    { "if": { "cond": "count(relatedBlocks) > 1", "then": [
      { "set": { "var": "parents", "value": "{}" } },
      { "forEach": { "in": "relatedBlocks", "as": "rb", "maxIterations": 100000, "do": [
        { "set": { "var": "relNum",     "value": "trim(coalesce(text(rb,'td:first-child td:last-child'),''))" } },
        { "set": { "var": "indentStr",  "value": "trim(replace(coalesce(attr(rb,'td:first-child td:first-child','width'),''), 'px', ''))" } },
        { "if": { "cond": "!isNullOrWhitespace(indentStr) && isInt(indentStr)",
            "then": [ { "set": { "var": "parents", "path": "[${toInt(indentStr)}]", "value": "relNum" } } ],
            "else": [ { "log": { "level": "error", "message": "Could not determine indentation of related record: ${input.link}" } } ] } },
        { "set": { "var": "tdClass", "value": "trim(coalesce(attr(rb,'class'),''))" } },
        { "set": { "var": "indent",  "value": "isInt(indentStr) ? toInt(indentStr) : 0" } },
        { "set": { "var": "cand",    "value": "filter(map(keys(parents), k, toInt(k)), n, n < indent)" } },
        { "switch": { "cases": [
          { "when": "contains(tdClass, 'ACA_RelatedCap_Highlight')", "do": [
              { "set": { "var": "parentRecordNumber", "value": "count(cand) > 0 ? get(parents, string(max(cand))) : ''" } } ] },
          { "when": "contains(tdClass, 'ACA_RelatedCap_Normal')", "do": [
              { "set": { "var": "relType", "value": "trim(coalesce(text(rb,'> td:nth-child(2)'),''))" } },
              { "set": { "var": "relName", "value": "trim(coalesce(text(rb,'> td:nth-child(3)'),''))" } },
              { "set": { "var": "relDate", "value": "trim(coalesce(text(rb,'> td:nth-child(4)'),''))" } },
              { "set": { "var": "relLink", "value": "exists({ base:'rb', css:'> td:last-child a' }) && !isNullOrWhitespace(trim(coalesce(attr(rb,'> td:last-child a','href'),''))) ? resolveUrl(input.link, trim(attr(rb,'> td:last-child a','href'))) : ''" } },
              { "set": { "var": "relParent", "value": "count(cand) > 0 ? get(parents, string(max(cand))) : ''" } },
              { "push": { "into": "relatedRecords", "value":
                "{ recordNumber: relNum, parentRecordNumber: relParent, recordType: relType, projectName: relName, date: relDate, link: relLink }" } } ] }
        ], "default": [
          { "log": { "level": "error", "message": "Could not determine class of related record: ${input.link}" } }
        ] } }
      ] } }
    ] } }
  ],

  "result": "{ link: input.link, recordNumber: recordNumber, recordType: recordType, projectName: projectName, recordDate: recordDate, status: recordStatus, description: description, parentRecordNumber: parentRecordNumber, violations: violations, parcels: parcels, locations: locations, owners: owners, processingStatus: processingStatus, attachments: attachments, relatedRecords: relatedRecords }"
}
```

**Fidelity notes.**
- `guard`/`fail class:"terminal"` are excluded from `retryOn`, so they are not retried — matching the
  Polly policy that retries only `timeout`/`Page crashed`.
- **`count` on a structured `Sel` map counts map ENTRIES, not DOM matches — DOM counting requires a bound
  handle or `exists`.** `count`'s value-model overload takes precedence for a `Dictionary` (a `{ base, css }`
  literal), returning its *entry count* (`2`), never a page query — so `count({base,css}) > 0` is
  *always* true and `count({base,css}) > 1` is *always* true, regardless of the DOM. The reference's
  `CountAsync()` on an inline `Locator(...)` counts elements, so this payload reproduces those checks with
  the two shapes that DO query the page: an **existence** check uses `exists({base,css})` (which resolves the
  Sel map as a selector and returns match-count > 0), and a **numeric** DOM count binds the locator first
  (`locate` into a var) and then `count(var)` (a bound handle counts elements). Applied at: the multiple-owners
  `count(ownerBlocks) > 1` (owner blocks located first), the `ownerLines = count(ownerRows)` line count, and
  the `exists(...)` existence guards for the owner address-1 cell, the processing expand-link, the
  processing additional-comment rows, and the related-record anchor. `count(var)` where the var holds a
  plain array/map still counts entries (the processing `lines`, the parents `cand`) — that is the intended
  value-model behaviour and is unchanged.
- Search-row URL uses naive `scheme://host+href` concat (`:130`); related-record link uses
  `resolveUrl` = `new Uri(base, rel)` (`:672`). Both reproduced (§7.3).
- `dl.contentId` is engine-native and identical to `AttachmentHashing.AttachmentIdFromHash`
  (SHA-256 first-16-bytes GUID, §9.3) — the payload never constructs the GUID; it only composes
  `internalFilename` from the scraped `filename` + `contentId` to match `BuildInternalFilename`.
- The reference's dead `country` var in the 5-`<br>` branch (`:253`) is omitted — it has no output
  effect, and "identical output" concerns the returned object only.
- The processing-status `for i=0; i*2+1 <= rowCount` bound is expressed as `to:"(procRowCount-1)/2"
  inclusiveTo` plus a defensive `break when i*2+1 > procRowCount` — integer-division semantics match
  the C# loop's termination.
- `addtl[key]` uses a computed-key `set` path with the trailing-colon strip inlined
  (`endsWith(h,':') ? substring(h,0,length(h)-1) : h`), reproducing `h[..^1]` (`:507`).
- `newLinks` is de-duplicated via `distinct(...)` in the `result`, matching the C# `HashSet<string>`
  (idempotent across pages and across a whole-operation Polly retry).
- Out-of-range index access is a **terminal** failure (§7.2/§8.3), so a parcel/processing cell missing
  its delimiter fails the run exactly as the C# `Split(...)[1]` throw does.
- `internalFilename` is built from the **scraped** filename's extension + `contentId`, matching
  `BuildInternalFilename` (§9.3) — not from the download's HTTP-suggested name.
- Processing lines split on `'\n'`: the reference's `Environment.NewLine` resolves to `\n` on its Linux
  host and Playwright `innerText` is `\n`-normalised, so this matches.
- The attachment 50-page cap is the **explicit** `attPagesVisited < 50` logic (one warning, record
  still returned); the loop's `maxIterations` is a generic backstop, not the domain cap.
- Documented micro-divergence: the reference's additional-comment `Dictionary.Add` throws on a
  duplicate key, whereas the computed-key `set` upserts. Reachable only if one processing activity has
  two comment lines with an identical heading — absent from the corpus; noted, not designed around.

---

## Appendix C — Expression builtin → C# operation mapping (the boundary is exactly LJCMG-sized)

| Builtin | Reference C# | Site |
|---|---|---|
| `trim` | `.Trim()` | pervasive |
| `replace(s,o,n)` | `.Replace("*","")`, `.Replace("px","")`, `.Replace("Due on ","")` | `:237,:639,:489` |
| `split(s,sep)` | `.Split("<br>")`, `.Split(":")`, `.Split(",")`, `.Split(" on ")`, `.Split(" by ")`, `.Split(Environment.NewLine)` | `:236,:438,:489-493,:481` |
| `substring(s,a,b?)` | `name.Substring(1,1)`, `name[2..]`, `h[..^1]` | `:317,:507` |
| `startsWith` / `equalsIgnoreCase` | `.StartsWith(…,IgnoreCase)`, `.Equals(…,IgnoreCase)` | `:297,:369` |
| `contains` | `.Contains("ACA_RelatedCap_…")` | `:653,:660` |
| `isNullOrWhitespace` | `string.IsNullOrWhiteSpace` | `:273,:320` |
| `length` | `.Length`, `.Split(...).Length` | `:317,:236` |
| `count(x)` | `.CountAsync()` | dozens |
| `first`/`nth` | `.First`, `.Nth(i)` | `:286,:130` |
| `filter`/`map`/`max`/`keys`/`get` | `parents.Where(k<indent).OrderByDescending(k).Select(v).FirstOrDefault()` | `:655,:675` |
| `distinct` | `HashSet<string> newLinks` dedup | HistoricalCrawler:79 |
| `coalesce` | `?? ""`, `?? ":"` | pervasive, `:438` |
| `toInt`/`isInt` | `int.TryParse` | `:640` |
| `matches` / `filter.hasTextRegex` | `new Regex("^"+n+"$")` | `:613` |
| `urlScheme`/`urlHost`/`urlPath` | `new Uri(page.Url).Scheme/.Host/.AbsolutePath` | `:130,:205` |
| `resolveUrl(base,rel)` | `new Uri(new Uri(link.Id), rel)` | `:672` |
| `text`/`innerText`/`innerHtml`/`attr` | `TextContentAsync/InnerTextAsync/InnerHTMLAsync/GetAttributeAsync` | pervasive |
| engine content-hash (`download` result) | `AttachmentHashing.ComputeAttachmentIdAsync` / `BuildInternalFilename` | `:569,:576` |

Nothing in the reference requires a builtin outside this list. That is the argument that the language
is right-sized: strong enough for LJCMG's ugliness, no stronger.
