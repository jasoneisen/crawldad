# Vendored Tabler UI

These assets are **vendored** (committed into the repo) so the portal serves them from its own
`wwwroot` at runtime — there is **no CDN dependency** when the app is running.

- Package: [`@tabler/core`](https://www.npmjs.com/package/@tabler/core)
- Pinned version: **1.4.0**
- Source: `https://registry.npmjs.org/@tabler/core/-/core-1.4.0.tgz`
- License: MIT (© Tabler contributors)

## Files

| Path | Origin inside the npm tarball |
| --- | --- |
| `1.4.0/css/tabler.min.css` | `package/dist/css/tabler.min.css` |
| `1.4.0/js/tabler.min.js`   | `package/dist/js/tabler.min.js`   |

The CSS is self-contained: every `url(...)` inside it is an inline `data:` URI (embedded SVG),
so there are no font/image files to fetch at runtime. Nav icons in the app shell are hand-written
inline SVGs (also MIT-style, no icon-font dependency).

## Upgrading

1. Download a new pinned release: `curl -O https://registry.npmjs.org/@tabler/core/-/core-<version>.tgz`
2. Extract `package/dist/css/tabler.min.css` and `package/dist/js/tabler.min.js` into
   `wwwroot/lib/tabler/<version>/`.
3. Update the `TablerVersion` reference in `Components/App.razor` and this file.
4. Delete the old version folder.
