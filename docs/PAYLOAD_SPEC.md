# Crawldad — Payload language specification

A Crawldad payload is one JSON document: a small, safe, declarative browser-automation DSL. Its differentiators are **real loops** (with mandatory safety caps), **content-aware conditions** (branching on extracted text, which point-in-time DOM-only competitors structurally cannot do), and a payload that is a **composable JSON structure**, not an authored string — so it is diffable, storable, and LLM-generatable.

This document is the **semantic** reference for authoring: the rules and evaluation behavior behind the structure. It complements two exhaustive sources it does not duplicate — the [JSON Schema](../schema/crawldad-1.schema.json) (every node and field, with a `description`, served live at `GET /schema/crawldad-1.schema.json`) and [`API.md` §2](API.md) (the payload-in-brief and the request surface). Engine behavior (execution model, error taxonomy, checkpoints mechanism) is in [`SPEC.md`](SPEC.md).

---

## Automation is data, not code

A payload is composable, diffable JSON. Only **leaf expressions** are strings in a small expression language — the same shape as SQL-in-JSON or CEL-in-YAML. It is safe to execute because it cannot call `fs`, cannot `eval`, and cannot loop unboundedly: every loop carries a mandatory cap, and expressions are pure and total. A schema-valid payload therefore has a bounded, inspectable effect surface — the core safety claim.

## Document shape

```jsonc
{
  "crawldad": "1",            // dialect version — v1 frozen at first ship
  "name": "example.search",   // logical identity of the managed payload
  "inputs": { "<name>": { "type": "…", "required": false, "default": <literal> } },
  "config": { /* session config: backend, timeouts, route, retry (post-connect program) + connectRetry (connect boundary) — see SPEC.md */ },
  "vars":   { "<name>": <Expr> },  // initial bindings, evaluated once, in order
  "steps":  [ <Node> … ],          // the ordered program
  "result": <Expr>                 // final expression shaping the response body
}
```

**Field kinds** used throughout: `Expr` (a pure expression string; string literals inside are quoted — `"'owner'"`); `Tmpl` (a template with `${<Expr>}` interpolation — `"…/page/${n}"`; no `${}` means a literal); `Sel` (a selector); `Node` (one step — an object with exactly one recognised head key); `Target` (a storage sink for bytes); `Failure` (`{ class: "terminal"|"retryable", code, message:Tmpl }`).

## Inputs

Each input declares a `type` (`string`, `number`, `boolean`, `date`, `array`, `object`, `backend`, `storageTarget`, `secretRef`), an optional `required`, and an optional `default`. Three types are structural: a `backend` value names the adapter + credential binding; a `storageTarget` names a download sink; a **`secretRef` is a vault reference only** (never the secret) and is consumable *only* by `fill.secret` — see [Secrets](#secrets) below.

## Action set

Every step is `{ "<head>": { … } }`. The schema is exhaustive; the semantics that matter when authoring:

- **Navigation & waits.** `goto` (navigate), `waitForLoadState` (`load`/`domcontentloaded`/`networkidle`), `waitFor` (await a selector reaching `visible`/`hidden`/`attached`/`detached`), `waitForRequest` (the wait is **armed before** its `trigger` fires, so a request the trigger provokes is never missed), `frame` (bind a `FrameLocator` handle to a var), `addStyleTag` (inject CSS — data, not code).
- **Interaction.** `click`, `fill` (`value:Expr` **or** `secret` — mutually exclusive), `clear`, `screenshot` (full-page PNG to the screenshot store), `download` (run a `trigger`, stream the provoked download to a `Target`), `capture` (serialize the full document — doctype + `<html>`, **not** `innerHtml('html')` — or an element subtree via `selector`, and stream it to a `Target`; the **document** channel, symmetric with `download`).

The `capture` node binds `{ contentId, sha256, sizeBytes, storedAs, stored }` (identical to `download`) so `result` returns a compact **manifest of refs**, never megabytes of HTML. The captured document is the customer's own content going to the customer's own storage, so it bypasses the credential scrubber **entirely** (even the exact-secret rule) and never lands in Crawldad's Postgres. A result-borne `innerHtml` differs only in that it still exact-scrubs the run's **own** registered secrets (it is returned in the HTTP response and persisted); both leave a third-party `token=`/`apiKey=`-shaped param in the page **verbatim**, never rewriting it to `[redacted]` (issue #70). `config.captureOnFailure: { to: <Expr→storageTarget> }` is the failure analog of `screenshotOnFailure`: on a step failure it banks the failing page's HTML to a tenant BYO target (absent ⇒ disabled — there is no default target).
- **Data & control state.** `locate` (bind a **lazy** locator handle — re-queried on each use, matching Playwright, so it survives a grid re-render after a postback), `set`/`push` (mutate the flat run scope), `log` (`info`/`warning`/`error` → a `LogEmitted` event; a warning does **not** fail the run), and the control-flow nodes below.

Any action or selector may set `in:"<frameVar>"` to resolve inside a bound frame, and `timeoutMs?` to override the timeout hierarchy. `in` only scopes a selector, so on `capture` — the one node whose `selector` is optional — an `in` with no `selector` (a full-document capture has no frame to scope, and the engine would silently drop it) is rejected at save time as `capture_in_without_selector`.

## Selectors (`Sel`)

A `Sel` is either a **string** (CSS by default; `"xpath=…"` for XPath, Playwright-style; comma-union CSS like `"#a, #b"` passes through verbatim) or a **structured object** rooted at **exactly one** of `css` / `xpath` / `text` / `role` / `title` / `base`:

```jsonc
{ "css": "table tr", "nth": <Expr>, "first": true,
  "filter": { "hasTextRegex": <Tmpl> },
  "base": "rowVar",   // relative to a bound locator (child locator)
  "in": "attFrame" }  // resolve inside a bound frame handle
{ "role": "button", "name": <Tmpl> }   // getByRole + accessible-name substring
{ "title": <Tmpl> } | { "text": <Tmpl> } | { "xpath": <Tmpl> }
```

**Root rule (enforced at save time).** A structured `Sel` roots at exactly one root, with two refinements: `base` may pair with a relative `css` (the sole two-root combination — the child-locator form), and `name` accompanies `role` only (its accessible name). `role` is a fixed ARIA vocabulary (a schema `enum`). A violation — two page-roots, `base` with a non-`css` root, or `name` without `role` — is `ambiguous_selector`, rejected at save rather than resolved by precedence. The Locator-string roots (`css`, `xpath`) resolve inside a bound frame (`in`); the `GetBy*` roots (`role`, `text`, `title`) are page-level.

`locate` has two forms, mirroring Playwright: **from a selector** (`{ var, selector }` → `page.Locator`) or **derived from a handle** (`{ var, from, nth?, first?, filter? }` → `handle.Nth(i)`/`.First`/`.Filter`). An `nth` index must evaluate to a whole, non-negative value (a `long`, or an integral double like `2.0`); a non-integral value (`2.5`) is a terminal `type_error` and a negative or out-of-range index is `index_out_of_range` (a bare bad literal is caught at save time).

## Control flow

Control flow and state mutation are **structural nodes**, never expressions. Every loop carries a **mandatory `maxIterations`** cap (a missing cap is `missing_max_iterations` at save time) — the loop safety cap is a first-class requirement.

- **`if` / `switch`.** `if { cond, then, else? }`; `switch { cases: [{ when, do } …], default? }` (boolean predicates, first true wins — sugar over nested `if/else`).
- **`loop`.** Three forms: a numeric **`for`** (`{ var, from, to, step?, inclusiveTo?/exclusiveTo? }`), a condition **`while`** (body-then-test = do/while), and a collection **`forEach`** (`{ in, as, index? }` over a bound locator var → `.AllAsync()` or an array value). Any loop form carries an optional `onMaxIterations` (`"fail"` default → terminal `max_iterations_exceeded`; or `"warn"` → log and stop). A `for` bound (`from`/`to`/`step`) must evaluate to an integer (the counter is a `long`); a non-integral bound is a terminal `type_error` (a bare fractional literal is rejected at save time), while an integral double (`2.0`) is coerced.
- **`break` / `continue`.** `{ break: { when: <Expr>? } }` / `{ continue: { when: <Expr>? } }` — a bare form is unconditional.
- **`guard` / `fail`.** `guard { cond, elseFail:Failure }` asserts-or-raises; `fail { Failure }` raises unconditionally. A `class:"terminal"` failure is **not retried** — this is how you avoid burning the retry budget on an unrecoverable state (e.g. a redirect to a login page).
- **`comment`.** `{ "comment": "…" }` is a no-op annotation, ignored at execution and exempt from unknown-head-key validation.

## Expressions

The expression sublanguage is modelled on **Google CEL**: a real, non-Turing-complete grammar with **no** user-defined functions, recursion, assignment, iteration, or IO. It is pure, total, and side-effect-free — precisely strong enough to express ugly string surgery and content-aware branching, and precisely weak enough to keep the safety argument (an expression cannot loop, recurse, allocate unboundedly, or reach the filesystem/network/clock).

**Grammar.** Operators `+ - * / %`, comparisons `== != < <= > >=`, `&& || !`, ternary `?:`, member `.`, index `[]`. References: `input.*`, declared vars, loop `var`/`index`, and `pageUrl()`. Literals: string (`'…'`), number, boolean, null, array `[…]`, object `{ k: Expr }`.

**Three semantics that bite:**

- **`+` also concatenates** when either operand is a string; `- * / %` are numeric (integer `/` or `%` by zero is a terminal `division_by_zero`).
- **String/DOM builtins null-propagate** — a null primary argument yields null (like C# `?.`), so a missing element flows through `replace`/`split`/`trim` as null, and `coalesce(x, default)` / `?:` supply the default. `==`/`!=` compare against `null` directly.
- **An out-of-range index and a failed required conversion are TERMINAL, never null** (matching a C# `IndexOutOfRangeException`/`TryParse` throw). A default is produced *only* by explicit `coalesce`/`?:`. This is distinct from the null-propagation above: `split(x, ',')[1]` on a value with no comma **fails the run**; `coalesce(text('#missing'), '')` yields `''`.

**Builtins — the enumerated surface (the boundary).** This is the whole vocabulary; the schema's per-field descriptions and the expression parser are the authority on arity.

- **String:** `trim, lower, upper, replace(s,old,new), replaceRegex(s,re,rep), split(s,sep), substring(s,a,b?), substringAfterLast(s,sep), startsWith(s,p), endsWith(s,p), contains(s,x), indexOf(s,x), lastIndexOf(s,x), length(s), matches(s,re), equalsIgnoreCase(a,b), isNullOrWhitespace(s), string(x), join(list,sep)`
- **Collection:** `count(x), length(x), first(x), last(x), nth(x,i), slice(x,a,b?), reverse(x), distinct(x), filter(list,v,pred), map(list,v,expr), any(list,v,pred), all(list,v,pred), min(x), max(x), sortBy(list,v,key), keys(map), get(map,key), coalesce(a,b…), toInt(s), isInt(s)`
- **URL:** `urlScheme(u), urlHost(u), urlPath(u), pageUrl(), resolveUrl(base,rel)`
- **DOM (read-only — the only page access an expression has):** `count(Sel|loc), exists(x[,css]), text(x[,css]), innerText(x[,css]), innerHtml(x[,css]), attr(x[,css],name)`
- **Extraction severity:** `require(x)` — evaluates `x` with selector misses in its subtree promoted to a terminal `selector_miss` (see [Strict extraction](#strict-extraction--observable-selector-misses)).

Two nuances worth knowing: **`count` on a structured `Sel` map counts map *entries*, not DOM matches** (its value-model overload wins for a `{base,css}` literal) — for a DOM existence check use `exists({base,css})`, and for a DOM count bind the locator first (`locate` then `count(var)`). And there are **two URL strategies**: naive `"${urlScheme(pageUrl())}://${urlHost(pageUrl())}${href}"` concatenation, or proper `resolveUrl(base, rel)` (`new Uri(base, rel)`) — the language expresses both.

Explicitly **not** in the language: user functions, recursion, assignment/mutation (that is `set`/`push`), arbitrary iteration (only `map`/`filter`/`any`/`all` bounded by their input; unbounded iteration is a capped `loop`), date arithmetic beyond formatting, catastrophic-backreference regex (patterns are size- and time-guarded → `regex_too_large`/`regex_timeout`), and any network/filesystem/clock/randomness/`eval`. There is no `page.evaluate()` — `addStyleTag` (data) is shipped, arbitrary JS (code) is not, deliberately: it would forfeit the safety thesis and the drift-telemetry analysability.

The expression parse/validation codes (`unknown_function`, `wrong_arity`, `syntax_error`, `expression_too_deep`, and the run-time `type_error`/`index_out_of_range`/`unknown_identifier`/`invalid_url`/`int_conversion_failed`) are tabulated in [`API.md` §12](API.md).

## Strict extraction — observable selector misses

The documented drift idiom `trim(coalesce(text(row,'td:nth-child(3) a'),''))` has a failure mode: when the site restructures (a column moves, a control id changes), every extraction quietly degrades to `''`, the run **succeeds**, and every field is empty — a drifted county is indistinguishable from a quiet one. **Selector-miss is therefore made observable and classifiable**, at the payload author's chosen severity.

A **selector miss** is an *extraction* builtin — `text`/`innerText`/`innerHtml`/`attr` — whose target matched **zero elements**. The distinction that `coalesce` erases is preserved exactly:

- **matched nothing → a miss** (the drift signal): the target found no element, so the read is `null`.
- **matched an empty element → NOT a miss** (legitimately blank data): the element exists but its text is `''`, or `attr` names an attribute the matched element simply lacks. Blank ≠ absent.

Only the four extraction builtins count. The **existence predicates `count`/`exists` never miss** — probing "is it there?" and getting "no" is their whole point; guard an optional region with `if (exists(...))` and its `text(...)` inside is never even evaluated when absent.

**Soft mode (always on, the default).** A miss increments `stats.selectorMisses` and emits a `SelectorMiss(selector, stepIndex, at)` trace event, but the run **still succeeds** — so monitoring can alert on "canary succeeded but `selectorMisses > 0`" (issue #47) without the miss ever failing a healthy run. Events are **deduped by selector** (one per distinct selector per run) so a drifted selector across every row of a loop is one event, not thousands, while the counter still reflects the true magnitude.

**Required extraction — `require(x)`.** Wrap an extraction to make its miss **terminal**: `require(text(row,'td.recordNumber a'))` fails the run `selector_miss` (class `terminal`, message naming the selector and step) the moment that selector matches nothing, instead of returning a blank field. `require` promotes miss-severity for its whole argument subtree, so it composes with `trim`/`coalesce`/`map`/… (`trim(require(text(...)))` and `require(trim(text(...)))` are equivalent); with no extraction inside it is a transparent passthrough. A required miss still increments the counter and emits its event before failing, and composes with `config.captureOnFailure` — the failing page's HTML is banked next to the failure screenshot, the clearest drift diagnostic.

**`config.strictExtraction: true`.** Makes **every** miss terminal — the `require(...)` severity as the run-wide default (so a payload need not wrap each extraction). Default `false` (soft mode).

## Secrets

A `secretRef` input carries a **reference** into a vault, never the secret. It is consumable **only** by `fill.secret`, which resolves it from the vault at fill time and types it straight into the field:

```jsonc
"inputs": { "password": { "type": "secretRef", "required": true } },
{ "fill": { "selector": { "role": "textbox", "name": "Password" }, "secret": "input.password" } }
```

The secret never enters an expression, a variable, the trace, or any durable store (the `Filled` event records only `secret:<name>`). The save-time walker enforces this structurally: a `secretRef` named anywhere in an expression is `secret_ref_in_expression`, and a `fill.secret` that is not a bare `input.<secretRef>` reference is `fill_secret_not_secret_ref`. The full credential handling is in [`THREAT_MODEL.md`](THREAT_MODEL.md).

## Checkpoints

A `checkpoint` lets a long-running loop survive a process death or host restart: the run resumes from the last checkpoint against a *fresh* browser session instead of from the top. This is a **payload-authoring contract** — the engine cannot make an arbitrary loop resumable — enforced at save time. What a checkpoint captures and how resume restores it is the engine mechanism in [`SPEC.md`](SPEC.md#checkpoints--resumability); the authoring rules are here.

**Where a checkpoint may legally appear** (else `checkpoint_misplaced` / `checkpoint_not_unique`):

- **Inside a top-level `while` loop, and only there.** The loop must be a direct member of `steps` (resume re-enters a *top-level* step), and must be the `while` form (a `for`/`forEach` re-initialises its counter/binding on entry, discarding the restored value — a resumable loop drives its own continuation from restored variables).
- **Not inside a second, nested loop** (only the top-level step index is recorded).
- **Not inside a `resume` or `trigger` sub-program** (those re-run wholesale).
- **At most one per top-level loop** (resume restores a single cursor). Two separate top-level loops may each carry one.

**What breaks resumability** (the author's responsibility, beyond static validation): the checkpoint must **head the iteration** — nothing page-touching or side-effecting before it, or that work runs against a blank page and re-runs on every resume; the checkpointed iteration is **replayed**, so its per-iteration work must be idempotent (content-addressed `download` and appending the current page's rows are safe; a submit/confirm click is not); and the **cursor must be sufficient** to re-navigate from restored state alone. The safe pattern:

```jsonc
{ "loop": { "maxIterations": …, "while": "<continue from restored state>", "do": [
    { "comment": "checkpoint FIRST — nothing page-touching or side-effecting before it" },
    { "checkpoint": { "name": "…", "cursor": "<enough to re-navigate>",
        "resume": [ /* re-open the session at the cursor: goto / click-forward, re-bind handles */ ] } },
    /* … replay-safe work: extract, content-addressed download, push result-so-far … */
    /* … advance the cursor / set the while condition for the next iteration … */
] } }
```

## Validation

Every saved payload runs a two-stage gate: the **JSON Schema** (structure) and a **semantic pass** (every referenced var/frame/input is defined before use; every loop has `maxIterations`; expression parse + static builtin/arity check; selector root rule; checkpoint placement; the secret-ref boundary; no unknown head keys). Validation runs at **save** time (`POST /payloads`), so bad payloads never reach execution. An **inline** run (`POST /runs` with an inline `payload`) skips the semantic pass, so a mistake a saved payload would catch statically (e.g. `undefined_reference`, `ambiguous_selector`) surfaces at *run* time instead (typically `unknown_identifier` or a `malformed_node`). Save your payload to get the full static check. How to read the structured error list is [`API.md` §13](API.md).

## Worked examples

Eight curated, schema-valid payloads live in [`examples/`](examples/) (each validated against the schema in CI). They range from a gentle intro (`first-search.json`) through the expression language doing real string surgery (`extract-location.json`), the `download` node with content-addressed dedup (`download-attachment.json`), the `capture` node banking full documents + element subtrees to BYO storage with `captureOnFailure` (`capture-document.json`), strict extraction with a `require(...)` anchor + soft optional fields (`strict-extraction.json`), the newer surface — `fill.secret` + `screenshot` + structured selectors + a checkpointed `while` loop (`login-and-search.json`) — a full checkpointed crawl (`search-pagination.json`), and a comprehensive end-to-end scrape building one nested result object (`scrape-record.json`). Per-example commentary is in [`API.md` §15](API.md).
