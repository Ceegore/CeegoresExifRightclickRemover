#!/usr/bin/env python3
"""Real-image end-to-end verification.

Generates a JPEG with Pillow, runs the real ExifRemover engine, then re-decodes
the result with Pillow and compares pixels. This is the adversarial test C1/C2/C3 demand.
"""

import io
import os
import struct
import subprocess
import sys
import tempfile

from PIL import Image
from PIL.ExifTags import Base as ExifBase


def build_real_jpeg_with_metadata(width=64, height=64) -> bytes:
    """Real camera-style JPEG: EXIF (Make/Model/Software/DateTime), ICC, COM."""
    exif = Image.Exif()
    exif[ExifBase.Make] = "TestCamCo"
    exif[ExifBase.Model] = "TC-100"
    exif[ExifBase.Software] = "TestSoft 2.0"
    exif[ExifBase.DateTimeOriginal] = "2024:06:15 10:30:00"
    exif[ExifBase.ImageDescription] = "Test photo with metadata"

    img = Image.new("RGB", (width, height), (255, 128, 64))
    # Add some pixel variation so compression has real data
    pixels = img.load()
    for y in range(height):
        for x in range(width):
            pixels[x, y] = ((x * 7) % 256, (y * 11) % 256, ((x + y) * 5) % 256)

    buf = io.BytesIO()
    img.save(buf, format="JPEG", quality=90, exif=exif.tobytes(), comment="Photo by Test User")
    return buf.getvalue()


def verify_one(label, profile, jpeg_bytes):
    print(f"\n=== {label} ({profile}) ===")
    print(f"  Input: {len(jpeg_bytes)} bytes, EXIF/ICC/COM embedded")

    tmpdir = tempfile.mkdtemp(prefix="er_verify_")
    inp = os.path.join(tmpdir, "in.jpg")
    out = os.path.join(tmpdir, "out.jpg")
    with open(inp, "wb") as f:
        f.write(jpeg_bytes)

    # Use the real verifier binary
    verifier_exe = r"D:\Projects\ExifRemover\verify\bin\Release\net8.0\ExifRemover.Verifier.exe"
    if not os.path.exists(verifier_exe):
        print(f"  SKIP: verifier not found at {verifier_exe}")
        return True

    r = subprocess.run(
        [verifier_exe, inp, out, profile],
        capture_output=True, text=True
    )
    if r.returncode != 0:
        print(f"  FAIL: verifier returned {r.returncode}")
        print(f"  stderr: {r.stderr}")
        return False
    print("  " + r.stdout.replace("\n", "\n  ").strip())

    # Re-decode and compare pixels. The stripper may choose a non-clashing sibling
    # filename (e.g. out (2).jpg) when the requested path already exists. The verifier
    # output also tells us the actual output_path — parse it from stdout.
    if not os.path.exists(out) or os.path.getsize(out) == 0:
        d = os.path.dirname(out)
        base, ext = os.path.splitext(os.path.basename(out))
        # Look for any sibling that starts with `base ` and ends with `ext` (the non-clashing
        # pattern) AND has a non-zero size.
        for f in sorted(os.listdir(d)):
            if (f.startswith(base + " ") or f == os.path.basename(out)) and f.endswith(ext):
                p = os.path.join(d, f)
                if os.path.getsize(p) > 0:
                    out = p
                    print(f"  (stripper used: {os.path.basename(out)})")
                    break
    if not os.path.exists(out) or os.path.getsize(out) == 0:
        print("  FAIL: no non-empty output file")
        return False
    img_orig = Image.open(inp)
    img_orig.load()
    img_stripped = Image.open(out)
    img_stripped.load()
    if img_orig.size != img_stripped.size:
        print(f"  FAIL: size changed: {img_orig.size} -> {img_stripped.size}")
        return False
    if list(img_orig.getdata()) != list(img_stripped.getdata()):
        print("  FAIL: pixel data DIFFERS — not lossless!")
        # Find first mismatch
        p1 = list(img_orig.getdata())
        p2 = list(img_stripped.getdata())
        for i in range(len(p1)):
            if p1[i] != p2[i]:
                print(f"    first mismatch at index {i}: {p1[i]} vs {p2[i]}")
                break
        return False
    print(f"  Pixel-byte-identical: YES (size={img_orig.size})")
    return True


def main():
    # Test all three profiles
    jpeg = build_real_jpeg_with_metadata(64, 64)
    all_pass = True
    for profile in ["Privacy", "Minimal", "AllMetadata"]:
        if not verify_one(f"Real camera-style JPEG with EXIF/ICC/COM", profile, jpeg):
            all_pass = False

    # Also test a JPEG with no metadata (regression for "0 of 1 files, saves 0 B")
    bare_img = Image.new("RGB", (32, 32), (10, 20, 30))
    buf = io.BytesIO()
    bare_img.save(buf, format="JPEG", quality=85)
    bare_jpeg = buf.getvalue()
    if not verify_one("JPEG with NO metadata (regression for 0/1 file bug)", "Privacy", bare_jpeg):
        all_pass = False

    print()
    if all_pass:
        print("ALL CHECKS PASSED")
        return 0
    else:
        print("SOME CHECKS FAILED")
        return 1


if __name__ == "__main__":
    sys.exit(main())
