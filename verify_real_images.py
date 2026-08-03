#!/usr/bin/env python3
"""
Real-image end-to-end verification (C1/C2/C3 + ICC-profile coverage).

Generates real camera-style JPEG inputs (EXIF + ICC + COM + XMP) with Pillow,
runs the real ExifRemover engine on each, then re-decodes the result with Pillow
and compares pixels. This is the adversarial test the original audit demands:
a synthetic-only fixture won't catch the C1 byte-stuffing bug or the
profile-difference-on-ICC regression.

Requires:
  * Python 3.10+ with Pillow >= 10 (we use ImageCms for ICC profile generation)
  * verify/bin/Release/net8.0/ExifRemover.Verifier.exe to be already built
    (run `dotnet build verify/ExifRemover.Verifier.csproj -c Release`)
"""

import os
import subprocess
import sys
import tempfile

# Pillow 12 deprecated Image.Image.getdata; we use the equivalent .tobytes() instead
# to keep the script warning-free (and thus keep a non-zero exit code only on real
# failures, not on deprecation noise).
from PIL import Image


VERIFIER_EXE = r"D:\Projects\ExifRemover\verify\bin\Release\net8.0\ExifRemover.Verifier.exe"


def verifier_present() -> bool:
    return os.path.exists(VERIFIER_EXE)


def decode_pixels(path: str):
    img = Image.open(path)
    img.load()
    # Compare via the raw bytes of the decoded pixel buffer — this is the
    # strict, allocation-light way and avoids the Pillow 12 deprecation of getdata().
    return img.size, img.tobytes()


def verify_one(label: str, profile: str, input_path: str) -> bool:
    print(f"\n=== {label} (profile={profile}) ===")
    print(f"  Input: {os.path.basename(input_path)} ({os.path.getsize(input_path)} bytes)")

    tmpdir = tempfile.mkdtemp(prefix="er_verify_")
    out = os.path.join(tmpdir, "out.jpg")

    r = subprocess.run(
        [VERIFIER_EXE, input_path, out, profile],
        capture_output=True, text=True,
    )
    if r.returncode != 0:
        print(f"  FAIL: verifier returned {r.returncode}")
        print(f"  stderr: {r.stderr.strip()}")
        print(f"  stdout: {r.stdout.strip()}")
        return False
    for line in r.stdout.splitlines():
        if line:
            print(f"  {line}")

    # The stripper may choose a non-clashing sibling (e.g. out (2).jpg) when the
    # requested path already exists. Look for any non-empty sibling that begins
    # with our base name.
    if not os.path.exists(out) or os.path.getsize(out) == 0:
        base, ext = os.path.splitext(os.path.basename(out))
        for f in sorted(os.listdir(tmpdir)):
            if (f == os.path.basename(out) or f.startswith(base + " ")) and f.endswith(ext):
                candidate = os.path.join(tmpdir, f)
                if os.path.getsize(candidate) > 0:
                    out = candidate
                    print(f"  (stripper used: {f})")
                    break
    if not os.path.exists(out) or os.path.getsize(out) == 0:
        print("  FAIL: no non-empty output file")
        return False

    try:
        in_size, in_pixels = decode_pixels(input_path)
        out_size, out_pixels = decode_pixels(out)
    except Exception as ex:
        print(f"  FAIL: could not decode one of the files: {ex}")
        return False

    if in_size != out_size:
        print(f"  FAIL: image size changed: {in_size} -> {out_size}")
        return False
    if in_pixels != out_pixels:
        print(f"  FAIL: pixel data DIFFERS — not lossless!")
        # Find first mismatch (in_size * channels stride)
        n = min(len(in_pixels), len(out_pixels))
        for i in range(0, n, 3):  # RGB stride
            if in_pixels[i:i+3] != out_pixels[i:i+3]:
                print(f"    first mismatch at byte {i}: in={in_pixels[i:i+3].hex()} out={out_pixels[i:i+3].hex()}")
                break
        return False

    print(f"  Pixel-byte-identical: YES (size={in_size})")
    return True


def main() -> int:
    if not verifier_present():
        print(f"SKIP: verifier not found at {VERIFIER_EXE}")
        print("Build it with: dotnet build verify/ExifRemover.Verifier.csproj -c Release")
        return 0

    # Generate the inputs via gen_test_jpeg.py into a fresh temp dir.
    inputs = tempfile.mkdtemp(prefix="er_inputs_")
    gen = subprocess.run(
        [sys.executable, os.path.join(os.path.dirname(__file__), "gen_test_jpeg.py"), inputs],
        capture_output=True, text=True,
    )
    if gen.returncode != 0:
        print(f"FAIL: gen_test_jpeg.py returned {gen.returncode}")
        print(f"  stdout: {gen.stdout}")
        print(f"  stderr: {gen.stderr}")
        return 1
    for line in gen.stdout.splitlines():
        if line:
            print(f"  [gen] {line}")

    full_jpeg = os.path.join(inputs, "real_full.jpg")
    bare_jpeg = os.path.join(inputs, "real_bare.jpg")

    if not os.path.exists(full_jpeg) or not os.path.exists(bare_jpeg):
        print(f"FAIL: gen_test_jpeg.py did not produce the expected outputs")
        return 1

    all_pass = True

    # Same input across the three profiles — they differ only on ICC handling, so
    # the byte counts and the verifier's dropped_segments will vary. The pixel data
    # must be identical in all three (the entropy-coded scan is never re-encoded).
    for profile in ["Privacy", "Minimal", "AllMetadata"]:
        if not verify_one("Real camera-style JPEG with EXIF+ICC+COM+XMP", profile, full_jpeg):
            all_pass = False

    # Regression: a JPEG with no metadata. Must succeed without throwing and must
    # produce a pixel-identical copy (no metadata removed, entropy preserved).
    if not verify_one("Bare JPEG (no metadata)", "Privacy", bare_jpeg):
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
