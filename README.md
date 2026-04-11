<p align="center">
  <img src="images/banner.png" alt="JellyShim — Performance Patch for Jellyfin" width="800" />
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Jellyfin-10.11+-00a4dc?style=for-the-badge&logo=jellyfin&logoColor=white" alt="Jellyfin 10.11+" />
  <img src="https://img.shields.io/badge/.NET-9.0-512bd4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 9.0" />
  <img src="https://img.shields.io/badge/license-MIT-green?style=for-the-badge" alt="MIT License" />
</p>

---

## 🎯 Why JellyShim?

Out of the box, Jellyfin serves uncompressed web assets, full-resolution images, and minimal cache headers. On slow networks, mobile clients, or instances with lots of media, this translates to **seconds of unnecessary loading**.

JellyShim fixes all of that in one plugin:

- **60–90 % smaller** JS/CSS/HTML via minification + Brotli pre-compression
- **50–80 % smaller** images via native resizing + WebP conversion
- **Instant repeat visits** via intelligent caching with ETag + 304 support
- **Faster first paint** via modulepreload, script defer, preconnect hints

**Install → Restart → Done.** Most optimizations are on by default. Nothing on disk is modified — disable the plugin and everything reverts instantly.

---

## 📦 Installation

### Add Repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Click **Add** and enter:
   - **Name:** `JellyShim`
   - **URL:** `https://raw.githubusercontent.com/alexisometric/jellyshim/main/manifest.json`
3. Go to **Catalog**, find **JellyShim**, click **Install**
4. Restart Jellyfin

### Manual Install

1. Download the latest release DLL from [Releases](../../releases)
2. Copy to your Jellyfin plugins directory:

   | Platform | Path |
   |---|---|
   | Linux | `~/.local/share/jellyfin/plugins/JellyShim/` |
   | Docker | `/config/plugins/JellyShim/` |
   | Windows | `%APPDATA%\jellyfin\plugins\JellyShim\` |

3. Restart Jellyfin

### Build from Source

```bash
git clone https://github.com/alexisometric/jellyshim.git
cd jellyshim
dotnet build -c Release
```

Output: `Jellyfin.Plugin.JellyShim/bin/Release/net9.0/Jellyfin.Plugin.JellyShim.dll`

---

## 💬 Contributing

JellyShim is **open source** and built for the community — everyone is welcome to contribute!

- **Found a bug?** [Open an issue](https://github.com/alexisometric/jellyshim/issues/new) — describe what happened and we'll look into it
- **Have an idea?** [Suggest a feature](https://github.com/alexisometric/jellyshim/issues/new) — all improvement proposals are welcome
- **Want to code?** Fork the repo, make your changes, and submit a pull request

Whether it's a typo fix, a new optimization, or a performance tweak — every contribution helps make Jellyfin faster for everyone.

---

## ✨ Feature Overview

| # | Feature | Default | What it does |
|---|---|:---:|---|
| 1 | [JS/CSS Minification](#1--jscss-minification) | ✅ On | Strips whitespace, comments, shortens names |
| 2 | [Brotli/Gzip Pre-compression](#2--brotligzip-pre-compression) | ✅ On | Pre-compresses all text assets to disk cache |
| 3 | [Native Image Optimization](#3--native-image-optimization) | ❌ Off | Resize + re-encode every Jellyfin image in-process |
| 4 | [Smart Cache Headers](#4--smart-cache-headers) | ✅ On | Optimal Cache-Control per asset type |
| 5 | [HTML Optimization](#5--html-optimization) | ✅ On | Modulepreload, defer, preconnect, DNS-prefetch |
| 6 | [Link Preload Headers](#6--link-preload-headers) | ✅ On | HTTP Link headers for fonts and JS |
| 7 | [CORS / CORP Headers](#7--cors--corp-headers) | ✅ On | Cross-Origin-Resource-Policy for iframe embedding |
| 8 | [Security Headers](#8--security-headers) | ❌ Off | X-Content-Type-Options, Referrer-Policy, Permissions-Policy |
| 9 | [Plugin Asset Support](#9--plugin-asset-support) | ✅ On | Extends optimization to community plugins |
| 10 | [File Transformation Bridge](#10--file-transformation-bridge) | ✅ On | Optional integration with File Transformation plugin |

---

## 1 · JS/CSS Minification

Uses [NUglify](https://github.com/trullock/NUglify) to minify JavaScript and CSS at build time.

- Automatically skips already-minified files (`.min.js` / `.min.css`)
- Falls back to original on any error — never breaks assets
- Runs as part of the startup pre-processing pipeline

> **Config:** `EnableMinification` · default `true`

---

## 2 · Brotli/Gzip Pre-compression

All compressible assets (`.js`, `.css`, `.html`, `.json`, `.svg`, `.xml`, `.txt`, `.map`, `.mjs`) are pre-compressed at server startup and stored in a disk cache. Requests are served with zero runtime compression cost.

```
Original asset: 450 KB
├── Brotli (q11):  52 KB  (-88 %)
└── Gzip:          68 KB  (-85 %)
```

- **Accept-Encoding negotiation** — Serves Brotli when supported, Gzip fallback, raw as last resort
- **Configurable quality** — Brotli level 0–11 (default 11 = maximum compression)
- **Scheduled re-processing** — Runs at startup + every day at 4 AM

> **Config:** `EnableCompression` · `BrotliCompressionLevel` (0–11, default `11`)

---

## 3 · Native Image Optimization

**Built-in image processing powered by [ImageSharp](https://sixlabors.com/products/imagesharp/) — no external service, no Docker sidecar, nothing to install.**

JellyShim intercepts all Jellyfin image requests and processes them on the fly:

1. **Resize** — Downscale to a per-type max width (preserves aspect ratio, never upscales)
2. **Re-encode** — Convert to WebP or JPEG with per-type quality
3. **Cache** — Processed images saved to disk; subsequent requests served instantly
4. **304 support** — ETag-based conditional requests avoid re-sending unchanged images

### Format Negotiation

| Config value | Behavior |
|---|---|
| `webp` | Serve WebP if browser supports it, JPEG fallback |
| `jpeg` | Always serve JPEG |
| `auto` | Prefer WebP when `Accept: image/webp` is present |

### Per-Type Independent Settings

Every Jellyfin image type has its own **max width** and **quality**, giving you full granular control:

| Image Type | Description | Default Width | Default Quality |
|---|---|---:|---:|
| **Primary** | Poster, cover art, album art | 600 px | 80 |
| **Backdrop** | Background art, detail view | 1920 px | 75 |
| **Art** | Fan art, clear art | 1280 px | 75 |
| **Banner** | Wide banner images | 1000 px | 80 |
| **Logo** | Transparent logo overlays | 400 px | 90 |
| **Thumb** | Thumbnail previews | 480 px | 75 |
| **Screenshot** | TV episode screenshots | 1280 px | 75 |
| **Chapter** | Chapter thumbnails in player timeline | 400 px | 70 |
| **Profile** | User profile pictures | 200 px | 85 |
| **Disc** | CD/DVD/Blu-ray disc art | 300 px | 80 |
| **Box** | Box art (front) | 300 px | 80 |
| **BoxRear** | Box art (rear) | 300 px | 80 |
| **Default** | Fallback for any unrecognized type | 300 px | 80 |

> **Config:** `EnableImageOptimization` (default `false`) · `EnableImageCache` (default `true`) · `ImageOutputFormat` (default `webp`) · per-type `*MaxWidth` and `*Quality`

---

## 4 · Smart Cache Headers

Applies optimal `Cache-Control`, `ETag`, `Vary`, and `stale-while-revalidate` headers based on what's being served:

| Asset Category | Cache Strategy | Default TTL |
|---|---|---|
| Hashed assets (`main.a1b2c3d4.js`) | `public, immutable` | 1 year |
| Static web assets (`/web/*`) | `public` + stale-while-revalidate | 30 days |
| Plugin static assets | `public` + stale-while-revalidate | 1 day |
| HTML entry points (`index.html`) | `no-cache, must-revalidate` | — |
| Processed images | `public` + stale-while-revalidate | 30 days |
| Fonts (`.woff2`, `.woff`, `.ttf`, ...) | `public, immutable` | 1 year |
| API endpoints | **`no-store`** | — |

**Key behaviors:**
- **Automatic hashed asset detection** — Filenames with 8+ hex characters (e.g., `main.a1b2c3d4.js`) get `immutable` caching
- **ETag + 304** — In-memory ETag cache (ConcurrentDictionary) based on SHA-256 content hash; serves 304 Not Modified on match
- **API safety** — **30+ Jellyfin API prefixes** are never cached (`/System/`, `/Sessions/`, `/Library/`, `/Items/`, `/Users/`, `/Playlists/`, `/Search/`, `/Audio/`, `/Videos/`, etc.)

> **Config:** `EnableCacheHeaders` · `HashedAssetMaxAge` · `StaticAssetMaxAge` · `PluginAssetMaxAge` · `StaleWhileRevalidate` · `ImageCacheMaxAge` · `ImageStaleWhileRevalidate`

---

## 5 · HTML Optimization

Processes HTML responses through [AngleSharp](https://anglesharp.github.io/) DOM manipulation:

| Optimization | What it does |
|---|---|
| **Modulepreload** | Injects `<link rel="modulepreload">` for all JS bundles — eliminates module loading waterfalls |
| **Script defer** | Adds `defer` to non-critical `<script>` tags — unblocks rendering |
| **Preconnect hints** | Injects `<link rel="preconnect">` + `<link rel="dns-prefetch">` for external origins |
| **SRI stripping** | Removes `integrity` attributes from modified resources to prevent hash mismatches |
| **HTML minification** | Collapses whitespace (preserves `<script>`, `<style>`, `<pre>`, `<textarea>`) |

Default preconnect origins:
```
https://fonts.googleapis.com
https://fonts.gstatic.com
https://cdn.jsdelivr.net
https://www.gstatic.com
```

> **Config:** `EnablePreloadInjection` · `EnableScriptDefer` · `EnablePreconnectHints` · `StripSriOnModification` · `PreconnectOrigins`

---

## 6 · Link Preload Headers

Adds HTTP `Link` headers that trigger the browser to start fetching critical resources **during** the HTTP response — before the HTML parser even sees the `<link>` tags:

- **Fonts** → `Link: </web/font.woff2>; rel=preload; as=font; type=font/woff2; crossorigin`
- **JavaScript** → `Link: </web/main.js>; rel=modulepreload`

> **Config:** `EnableFontPreloadHeaders` · `EnableJsModulepreloadHeaders`

---

## 7 · CORS / CORP Headers

Adds `Cross-Origin-Resource-Policy` to all static assets and images:

- Default: `cross-origin` — enables iframe embedding for **Jellyseerr**, **Organizr**, **Homepage**, etc.
- Options: `cross-origin`, `same-site`, `same-origin`

> **Config:** `EnableCrossOriginResourcePolicy` · `CrossOriginResourcePolicyValue`

---

## 8 · Security Headers

Optional hardening headers for security-conscious deployments:

| Header | Default Value |
|---|---|
| `X-Content-Type-Options` | `nosniff` |
| `Referrer-Policy` | `strict-origin-when-cross-origin` |
| `Permissions-Policy` | `accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), usb=()` |

> **Config:** `EnableSecurityHeaders` (default `false`) · `XContentTypeOptions` · `ReferrerPolicy` · `PermissionsPolicy`

---

## 9 · Plugin Asset Support

Extends optimization to community plugin static assets by recognizing their URL path prefixes:

**Pre-configured plugins:**
- JellyTweaks, HomeScreen, MediaBarEnhanced, Announcements, JellyfinEnhanced, JavaScriptInjector

Plugin assets get appropriate cache headers and compression. Add your own paths in the admin UI (one per line).

> **Config:** `PluginAssetPaths`

---

## 10 · File Transformation Bridge

Optionally integrates with the [File Transformation](https://github.com/jellyfin/jellyfin-plugin-file-transformation) plugin. When detected, JellyShim registers its HTML optimizer as a runtime transformation callback via reflection — no hard dependency.

> **Config:** `EnableFileTransformationIntegration` (default `true`)

---

## 🏗️ Architecture

JellyShim injects two ASP.NET Core middlewares early in the Jellyfin HTTP pipeline via `IStartupFilter`:

```
Client Request
       │
       ▼
┌──────────────────────────────────┐
│  ImageOptimizationMiddleware     │  Intercepts /Items/*/Images/*
│                                  │  and /Users/*/Images/*
│  → Check disk cache             │
│  → If miss: capture response,   │
│    resize + re-encode, cache     │
│  → Serve with ETag/304          │
└──────────┬───────────────────────┘
           │
           ▼
┌──────────────────────────────────┐
│  AssetOptimizationMiddleware     │  Intercepts /web/* and plugin paths
│                                  │
│  → Classify: web / plugin /     │
│    font / API / other            │
│  → Serve Brotli/Gzip from cache │
│  → Add Cache-Control, ETag,     │
│    Vary, CORP, security headers  │
│  → Skip API endpoints (30+)     │
└──────────┬───────────────────────┘
           │
           ▼
┌──────────────────────────────────┐
│  Jellyfin Default Pipeline       │  Static files, API, streaming...
└──────────────────────────────────┘
```

### Scheduled Task

| Trigger | When |
|---|---|
| **Startup** | Immediately on server start |
| **Daily** | 4:00 AM |

Pre-processes all `/web/` assets: minify → transform HTML → compress Brotli + Gzip → cache to disk.

### Disk Cache Structure

```
{JellyfinCachePath}/jellyshim/
├── raw/     Minified but uncompressed assets
├── br/      Brotli-compressed assets
├── gz/      Gzip-compressed assets
└── img/     Processed & cached images
```

---

## ⚙️ Configuration

After installation, go to **Dashboard → Plugins → JellyShim**.

**The default configuration is production-ready.** Most users only need to:

1. ✅ Install and restart — asset optimization, compression, and caching are already on
2. ☑️ Toggle **Enable Image Optimization** if you want image resizing + WebP conversion
3. 🎛️ (Optional) Tune per-type image width/quality to match your setup

### All Configuration Properties

<details>
<summary><strong>Asset Optimization</strong></summary>

| Property | Type | Default |
|---|---|---|
| `EnableMinification` | bool | `true` |
| `EnableCompression` | bool | `true` |
| `BrotliCompressionLevel` | int (0–11) | `11` |
| `EnableFileTransformationIntegration` | bool | `true` |

</details>

<details>
<summary><strong>Cache Headers</strong></summary>

| Property | Type | Default |
|---|---|---|
| `EnableCacheHeaders` | bool | `true` |
| `HashedAssetMaxAge` | seconds | `31536000` (1 year) |
| `StaticAssetMaxAge` | seconds | `2592000` (30 days) |
| `PluginAssetMaxAge` | seconds | `86400` (1 day) |
| `StaleWhileRevalidate` | seconds | `86400` (1 day) |
| `ImageCacheMaxAge` | seconds | `2592000` (30 days) |
| `ImageStaleWhileRevalidate` | seconds | `604800` (7 days) |

</details>

<details>
<summary><strong>HTML Optimization</strong></summary>

| Property | Type | Default |
|---|---|---|
| `EnablePreloadInjection` | bool | `true` |
| `EnableScriptDefer` | bool | `true` |
| `StripSriOnModification` | bool | `true` |
| `EnablePreconnectHints` | bool | `true` |
| `PreconnectOrigins` | string (multiline) | Google Fonts, gstatic, jsDelivr |

</details>

<details>
<summary><strong>Link Headers</strong></summary>

| Property | Type | Default |
|---|---|---|
| `EnableFontPreloadHeaders` | bool | `true` |
| `EnableJsModulepreloadHeaders` | bool | `true` |

</details>

<details>
<summary><strong>CORS / Resource Policy</strong></summary>

| Property | Type | Default |
|---|---|---|
| `EnableCrossOriginResourcePolicy` | bool | `true` |
| `CrossOriginResourcePolicyValue` | string | `cross-origin` |

</details>

<details>
<summary><strong>Security Headers</strong></summary>

| Property | Type | Default |
|---|---|---|
| `EnableSecurityHeaders` | bool | `false` |
| `XContentTypeOptions` | string | `nosniff` |
| `ReferrerPolicy` | string | `strict-origin-when-cross-origin` |
| `PermissionsPolicy` | string | Restricts camera, mic, geolocation, etc. |

</details>

<details>
<summary><strong>Image Optimization</strong></summary>

| Property | Type | Default |
|---|---|---|
| `EnableImageOptimization` | bool | `false` |
| `ImageOutputFormat` | string | `webp` |
| `EnableImageCache` | bool | `true` |

**Per-type settings** — Each with `*MaxWidth` (px) and `*Quality` (1–100):

Primary (600/80) · Backdrop (1920/75) · Art (1280/75) · Banner (1000/80) · Logo (400/90) · Thumb (480/75) · Screenshot (1280/75) · Chapter (400/70) · Profile (200/85) · Disc (300/80) · Box (300/80) · BoxRear (300/80) · Default (300/80)

</details>

---

## 🔒 Security

- **No files modified on disk** — All optimizations live in the cache directory
- **Path traversal protection** — Cache rejects `..` segments, validates resolved paths
- **API endpoints never cached** — 30+ Jellyfin API prefixes explicitly excluded
- **Image size limit** — Rejects images larger than 50 MB
- **SRI safety** — Strips integrity attributes from modified resources
- **Optional security headers** — Referrer-Policy, Permissions-Policy, X-Content-Type-Options

---

## 🤝 Compatibility

| | Compatible |
|---|---|
| **Jellyfin** | 10.11.x and later |
| **Platforms** | Linux, Windows, macOS, Docker |
| **Reverse proxies** | Nginx, Caddy, Traefik, Apache, HAProxy |
| **Community plugins** | JellyTweaks, HomeScreen, MediaBarEnhanced, etc. |
| **Themes** | All Jellyfin themes (nothing on disk is modified) |
| **Clients** | Web, mobile, TV — all benefit from smaller transfers |
| **iframe dashboards** | Jellyseerr, Organizr, Homepage (via CORP header) |

### What JellyShim does NOT do

- ❌ Does **not** modify any original files on disk
- ❌ Does **not** cache or interfere with API responses or dynamic data
- ❌ Does **not** require any external service, Docker sidecar, or background process
- ❌ Does **not** affect streaming, transcoding, or playback in any way
- ❌ Disable the plugin → **everything reverts instantly**

---

## 📚 Dependencies

| Package | Version | Purpose |
|---|---|---|
| [NUglify](https://github.com/trullock/NUglify) | 1.21.17 | JS/CSS minification |
| [AngleSharp](https://anglesharp.github.io/) | 1.3.0 | HTML DOM parsing & manipulation |
| [SixLabors.ImageSharp](https://sixlabors.com/products/imagesharp/) | 3.1.12 | Native image resizing & encoding |

---

## ❓ FAQ

<details>
<summary><strong>Will this break my Jellyfin setup?</strong></summary>

No. JellyShim only intercepts HTTP responses — it never modifies original files. Disable the plugin and everything returns to normal instantly.
</details>

<details>
<summary><strong>Does it work with Docker?</strong></summary>

Yes. JellyShim runs entirely inside the Jellyfin process. No sidecar containers or external services needed.
</details>

<details>
<summary><strong>How much bandwidth does image optimization save?</strong></summary>

Typically **50–80%** on image data. For example, a 500 KB JPEG poster becomes ~120 KB WebP at quality 80. Backdrops see even larger savings.
</details>

<details>
<summary><strong>Does it slow down my server?</strong></summary>

Asset pre-compression runs once at startup (and daily at 4 AM). Image processing happens on first request but results are cached to disk — subsequent requests are served instantly from cache.
</details>

<details>
<summary><strong>Can I use this with a CDN or reverse proxy cache?</strong></summary>

Yes. JellyShim adds proper `Vary`, `Cache-Control`, and `ETag` headers that CDNs and reverse proxies respect.
</details>

<details>
<summary><strong>Does it need imgproxy or any external tool?</strong></summary>

No. Image processing is built-in via ImageSharp. Everything runs inside the Jellyfin process — zero external dependencies.
</details>

<details>
<summary><strong>What about API endpoints — are they cached?</strong></summary>

Never. JellyShim maintains a comprehensive list of 30+ Jellyfin API path prefixes and explicitly marks them `no-store`. Dynamic data is never intercepted.
</details>

---

## 📄 License

MIT — see [LICENSE](LICENSE) for details.

---

<p align="center">
  <sub>Built to make every Jellyfin instance faster. ⚡</sub><br/>
  <sub><a href="https://github.com/alexisometric/jellyshim/issues">Report a bug</a> · <a href="https://github.com/alexisometric/jellyshim/issues">Request a feature</a> · <a href="https://github.com/alexisometric/jellyshim/pulls">Contribute</a></sub>
</p>
