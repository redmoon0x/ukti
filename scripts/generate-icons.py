"""Build Ukti's app, tray and documentation assets from the supplied artwork.
Optional asset-authoring dependency: Pillow (not an application dependency).
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageOps

root = Path(__file__).resolve().parents[1]
source = root / 'src/assets/brand-source.png'
image = Image.open(source).convert('RGB')
luminance = ImageOps.grayscale(image)
# Remove the supplied black canvas and its very faint tile outline, preserving
# the white mark's original geometry and antialiased edges.
alpha = luminance.point(lambda value: max(0, min(255, (value - 24) * 255 // 231)))
bounds = alpha.point(lambda value: 255 if value > 96 else 0).getbbox()
alpha = alpha.crop(bounds)
alpha.thumbnail((448, 448), Image.Resampling.LANCZOS)
mark = Image.new('RGBA', (512, 512), (255, 255, 255, 0))
mark.putalpha(Image.new('L', mark.size, 0))
white = Image.new('RGBA', alpha.size, 'white')
white.putalpha(alpha)
mark.alpha_composite(white, ((512 - alpha.width) // 2, (512 - alpha.height) // 2))
mark.save(root / 'src/assets/ukti-glyph.png')
tile = Image.new('RGBA', (512, 512))
ImageDraw.Draw(tile).rounded_rectangle((0, 0, 511, 511), radius=112, fill=(8, 8, 8, 255))
tile.alpha_composite(mark)
tile.save(root / 'src/assets/ukti.ico', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
tile.save(root / 'docs/logo.png')
print('Updated glyph, multi-resolution Windows icon and documentation logo.')
