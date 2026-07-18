#!/usr/bin/env python3
"""StealthPDF PDF compression helper (PyMuPDF).

Runs out-of-process from the main C# app (which stays x64 for pdfium/Tesseract).
Does IMAGE-LEVEL compression: for each embedded raster image painted LARGER than the
target DPI on its page, downsample it to the target and re-encode it as JPEG. Text,
vector art, content streams and page /Rotate are NEVER touched, so rotation and
selectable text are preserved exactly, and images at/below the target are never
upsampled.

Performance notes (the previous version appeared to HANG on large files):
  * Text-only pages cost NOTHING: a resource-dict check (Page.get_images) skips them
    before any TextPage/content parsing - on a text-heavy book the per-page TextPage
    extraction was the dominant remaining cost.
  * Pages WITH images get ONE content pass via Page.get_image_info() — metadata only,
    NO image decodes. The old code called Page.get_image_rects() PER IMAGE, which
    re-parses the page content AND MD5-decodes every image on the page on each call,
    i.e. O(images^2) full image decodes per page.
  * Pages whose images are all at/below the target DPI now cost ZERO image decodes
    (the verdict comes from the painted transform vs. native pixel size). The old
    code decoded every image about twice even when it did nothing with it.
  * A processed image is decoded exactly once; the pixmap used for the xref<->
    placement digest match is reused for the re-encode.
  * doc.save() no longer passes clean=True (which rewrites/sanitizes every content
    stream in the document) and only linearizes small files (linearization is a
    full second write pass — it roughly doubles I/O on large files).

Protocol (stdout is the ONLY channel the parent parses; stderr is human diagnostics):
    compress <src> <dest> <targetDpi> <jpegQuality> <grayscale[0|1]>
    -- prints PROGRESS\t<page>\t<pageCount> as it goes (must be flushed: the parent
       shows these in its busy overlay),
       then DONE\t<beforeBytes>\t<afterBytes>\t<imagesProcessed>\t<imagesDownsampled>
       and exits 0.
    -- on error prints ERROR\t<message> and exits 1.
"""
import sys
import os
import math

import pymupdf  # PyMuPDF

# Linearization (fast web view) costs a full second save pass; only worth it on
# small files. Above this we trade fast-web-view for a bounded, quick save.
LINEARIZE_MAX_BYTES = 32 * 1024 * 1024


def effective_dpi(info):
    """Effective on-page DPI of an image block.

    The block's transform maps the unit square onto the page, so its column vectors
    are the painted lengths of the image's x/y axes in points. This is exact under
    any rotation or skew (pairing bbox width/height with pixel width/height would
    swap axes for images painted at 90 degrees).
    """
    try:
        a, b, c, d = info["transform"][:4]
        w_pt = math.hypot(a, b)
        h_pt = math.hypot(c, d)
        if w_pt <= 0 or h_pt <= 0:  # degenerate: fall back to the bbox
            rect = info["bbox"]
            w_pt = abs(rect[2] - rect[0])
            h_pt = abs(rect[3] - rect[1])
        px = info["width"]
        py = info["height"]
        if w_pt <= 0 or h_pt <= 0 or px <= 0 or py <= 0:
            return 0.0
        return max(72.0 * px / w_pt, 72.0 * py / h_pt)
    except Exception:
        return 0.0



def reencode_as_jpeg(doc, page, xref, info, target_dpi, jpeg_quality, grayscale, pix=None):
    """Downsample image `xref` to `target_dpi` and swap it in as a JPEG.

    `pix` may carry an already-decoded pixmap (from digest matching) so a processed
    image is decoded exactly once. Returns "done" when the image was replaced,
    "skipped" when it was deliberately left untouched. Never upsamples, and never
    replaces an image with something larger than the original stream (unless the
    user explicitly asked for grayscale conversion).
    """
    dpi = effective_dpi(info)
    if dpi <= target_dpi:
        return "skipped"
    if info.get("bpc", 8) == 1:
        # Bitonal (1-bit) scans are already extremely compact (JBIG2/CCITT); a JPEG
        # re-encode would only bloat and blur them.
        return "skipped"

    scale = target_dpi / dpi
    new_w = max(1, int(round(info["width"] * scale)))
    new_h = max(1, int(round(info["height"] * scale)))
    if new_w >= info["width"] and new_h >= info["height"]:
        return "skipped"  # never upsample

    if pix is None:
        pix = pymupdf.Pixmap(doc, xref)  # the one and only decode of this image
    if pix.alpha:
        # JPEG has no alpha channel; drop it from the pixels (the PDF keeps its
        # separate /SMask object, so transparency still renders).
        pix = pymupdf.Pixmap(pix, 0)
    if pix.colorspace not in (pymupdf.csRGB, pymupdf.csGRAY):
        pix = pymupdf.Pixmap(pymupdf.csRGB, pix)  # CMYK / palette / ICC -> RGB
    if grayscale and pix.colorspace != pymupdf.csGRAY:
        pix = pymupdf.Pixmap(pymupdf.csGRAY, pix)
    if new_w != pix.width or new_h != pix.height:
        pix = pymupdf.Pixmap(pix, new_w, new_h)  # scaled copy

    jpeg_bytes = pix.tobytes(output="jpeg", jpg_quality=jpeg_quality)

    orig_size = info.get("size", 0)  # compressed stream size (0 = unknown)
    if not grayscale and 0 < orig_size <= len(jpeg_bytes):
        return "skipped"  # our JPEG would not shrink this image: keep the original

    page.replace_image(xref, stream=jpeg_bytes)
    return "done"


def compress(src, dest, target_dpi, jpeg_quality, grayscale):
    before = os.path.getsize(src)
    doc = pymupdf.open(src)
    page_count = doc.page_count
    images_processed = 0      # unique xrefs we decoded and judged
    images_downsampled = 0    # unique xrefs actually replaced
    decided_xrefs = set()     # xrefs already judged (replaced, or fine as-is)

    for pno in range(page_count):
        page = doc.load_page(pno)

        # Cheap resource-dict check FIRST (no content parse, no decodes; includes
        # images nested inside Form XObjects): text-only pages are skipped WITHOUT
        # the TextPage extraction below - on a text-heavy book that extraction was
        # the dominant remaining cost and could take minutes on its own.
        try:
            page_imgs = page.get_images(full=True)
        except Exception:
            page_imgs = []
        if not page_imgs:
            print(f"PROGRESS\t{pno + 1}\t{page_count}", flush=True)
            continue

        # One content pass for this page, metadata only: NO image is decoded for this.
        try:
            infos = page.get_image_info()
        except Exception:
            infos = []

        # Cheap verdict first: is anything on this page actually above the target?
        over = [i for i in infos
                if effective_dpi(i) > target_dpi and i.get("bpc", 8) != 1]

        if over:
            if len(infos) == 1 and len(page_imgs) == 1:
                # Fast path (the typical scan/photo page): the one painted image IS
                # the one listed image - no digest matching needed, single decode.
                xref = page_imgs[0][0]
                if xref not in decided_xrefs:
                    decided_xrefs.add(xref)
                    images_processed += 1
                    try:
                        if reencode_as_jpeg(doc, page, xref, infos[0],
                                            target_dpi, jpeg_quality, grayscale) == "done":
                            images_downsampled += 1
                    except Exception:
                        pass  # leave a problem image untouched rather than fail the run
            else:
                # Multi-image page: map painted placements to xrefs via the MD5 of
                # their decoded samples (the same technique PyMuPDF uses internally
                # for get_image_info(xrefs=True)). Only pages WITH over-target
                # placements pay for this, and only the placements above target are
                # matched (decodes stop as soon as they are all found).
                over_numbers = {i.get("number") for i in over}
                try:
                    infos_h = page.get_image_info(hashes=True)
                except Exception:
                    infos_h = []
                pending = {}
                for i in infos_h:
                    if i.get("number") not in over_numbers or "digest" not in i:
                        continue
                    d = i["digest"]
                    # Same image painted twice on this page: judge it at its largest
                    # placement (highest effective DPI).
                    if d not in pending or effective_dpi(i) > effective_dpi(pending[d]):
                        pending[d] = i

                pend_dims = {(i["width"], i["height"]) for i in pending.values()}
                for item in page_imgs:
                    if not pending:
                        break  # every over-target placement already matched
                    xref = item[0]
                    if xref in decided_xrefs:
                        continue
                    # A match requires identical native pixels, so if this image's
                    # dimensions match no pending placement it can't be over target -
                    # skip its decode (big win on gallery pages with many small images).
                    if (item[2], item[3]) not in pend_dims:
                        decided_xrefs.add(xref)
                        continue
                    try:
                        pix = pymupdf.Pixmap(doc, xref)  # decode reused below
                    except Exception:
                        decided_xrefs.add(xref)
                        continue
                    info = pending.pop(pix.digest, None)
                    decided_xrefs.add(xref)
                    if info is None:
                        continue  # at/below target here, or not painted on this page
                    images_processed += 1
                    try:
                        if reencode_as_jpeg(doc, page, xref, info, target_dpi,
                                            jpeg_quality, grayscale, pix=pix) == "done":
                            images_downsampled += 1
                    except Exception:
                        pass

        # Page-granular progress for the parent's busy overlay. flush is mandatory:
        # stdout is a pipe, so without it the lines sit in the Python buffer.
        print(f"PROGRESS\t{pno + 1}\t{page_count}", flush=True)

    # Signal "all pages done" before the save pass (the save itself can take a
    # moment on big files and emits no progress of its own).
    print(f"PROGRESS\t{page_count}\t{page_count}", flush=True)

    # garbage=4 collects the orphaned original image streams (this is where the size
    # win materializes); deflate squeezes the remaining streams. clean=True is NOT
    # used (it rewrites/sanitizes every content stream - expensive, and pointless
    # here because content streams are never modified). Linearize only small files:
    # it is a full second write pass.
    doc.save(dest, garbage=4, deflate=True, linear=(before <= LINEARIZE_MAX_BYTES))
    doc.close()
    after = os.path.getsize(dest)
    return before, after, images_processed, images_downsampled


def main():
    if len(sys.argv) < 7:
        print("ERROR\tusage: compress <src> <dest> <targetDpi> <jpegQuality> <grayscale[0|1]>")
        return 1
    src, dest = sys.argv[2], sys.argv[3]
    try:
        target_dpi = int(sys.argv[4])
        jpeg_quality = int(sys.argv[5])
        grayscale = sys.argv[6] == "1"
    except Exception as ex:
        print(f"ERROR\tbad numeric argument: {ex}")
        return 1
    try:
        before, after, processed, downsampled = compress(src, dest, target_dpi, jpeg_quality, grayscale)
        print(f"DONE\t{before}\t{after}\t{processed}\t{downsampled}", flush=True)
        return 0
    except Exception as ex:
        import traceback
        traceback.print_exc(file=sys.stderr)
        print(f"ERROR\t{ex}")
        return 1


if __name__ == "__main__":
    sys.exit(main())

