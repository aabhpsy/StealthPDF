# Deploying stealthpdf.com

The site is the static contents of this folder. There is no build step — what is
here is what gets served. `_headers` is Cloudflare Pages / Netlify syntax; on any
other host, port those rules to its own header mechanism.

## The one rule

`index.html` ships with `REPLACE_DATE`, `REPLACE_SIZE` and `REPLACE_HASH`
placeholders in the download panel. **They are filled in by `release.ps1`, not by
hand.** Publishing before that runs would advertise those literal strings — and,
worse, publishing a hand-edited hash is how the site ends up vouching for a build
nobody verified.

Before 2.0.0 the panel drifted the other way: it still advertised v1.6.0 and the
1.6.0 SHA256 long after 2.0.0 shipped. The stamping step exists so that cannot
recur.

## Release → deploy

1. **Build and stamp** — on the Windows build machine, from the repo root:

   ```powershell
   .\release.ps1
   ```

   This publishes the EXE, signs it, hashes it, writes `SHA256SUMS.txt`, and
   stamps `pdf-landing/` with the version, release date, EXE size and SHA256.
   It reads the version from `<Version>` in `StealthPDF.csproj`, so bump that
   first — nothing else needs editing.

2. **Verify the stamp landed**:

   ```powershell
   pwsh .\pdf-landing\deploy-check.ps1
   ```

   Exits non-zero if a sentinel survived, an asset a page references is missing,
   the footer version badge disagrees with the csproj, a `sitemap.xml` entry has
   no matching file, or a retired KillerPDF asset path reappeared.

3. **Commit the stamped pages** so `main` and the live site agree:

   ```powershell
   git add pdf-landing SHA256SUMS.txt
   git commit -m "site: stamp <version> release facts"
   ```

4. **Upload**:
   - the contents of `pdf-landing/` to the site root, and
   - the signed EXE to `/download/StealthPDF.exe`.

   The EXE is deliberately not tracked in git — the download button points at
   `/download/StealthPDF.exe`, which is served from the host, not the repo.
   `deploy-check.ps1` reports it as a note rather than an error for that reason.

5. **Spot-check the live site**: the download panel shows the new version and
   hash, the hash matches `SHA256SUMS.txt`, and the wordmark renders in every
   theme (the theme swatches are in the toolbar).

## Notes

- **Cache busting**: `kp.css` and `kp.js` are linked with `?v=N`. Bump that
  number in every page whenever you edit either file, or returning visitors keep
  the old copy.
- **The wordmark is text, not an image.** It is `<span class="wm-logo">` styled
  in `kp.css`, coloured by `--logo-pdf`, so it tracks the theme and accent with
  no per-variant asset. The old `brand/*.svg` files were KillerPDF artwork and
  have been removed.
- **`404.html`** is `noindex` and intentionally absent from `sitemap.xml`. Point
  the host's not-found handler at it.
