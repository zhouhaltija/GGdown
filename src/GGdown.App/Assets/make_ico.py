from pathlib import Path
from PIL import Image

src = Path(__file__).with_name("app-source.png")
img = Image.open(src).convert("RGBA")
out = Path(__file__).with_name("app.ico")
img.save(out, format="ICO", sizes=[(16, 16), (32, 32), (48, 48), (256, 256)])
print("wrote", out, out.stat().st_size)
