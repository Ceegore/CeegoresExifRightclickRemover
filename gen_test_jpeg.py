from PIL import Image
from PIL.ExifTags import Base as ExifBase
import os
import sys

tmp = sys.argv[1]
inp = os.path.join(tmp, "in.jpg")
out = os.path.join(tmp, "out.jpg")
exif = Image.Exif()
exif[ExifBase.Make] = "TestCam"
exif[ExifBase.Model] = "TC-100"
exif[ExifBase.Software] = "TestSoft 2.0"
exif[ExifBase.DateTimeOriginal] = "2024:06:15 10:30:00"
img = Image.new("RGB", (64, 64), (255, 128, 64))
pixels = img.load()
for y in range(64):
    for x in range(64):
        pixels[x, y] = ((x * 7) % 256, (y * 11) % 256, ((x + y) * 5) % 256)
img.save(inp, format="JPEG", quality=90, exif=exif.tobytes(), comment="Photo by Test User")
print(f"Generated {inp} ({os.path.getsize(inp)} bytes)")
print(f"Out: {out}")
