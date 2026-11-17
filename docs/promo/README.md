# Promo images

| File | Size | Use |
|---|---|---|
| `banner.png` | 2560×1280 (1280×640 @2x) | README header, GitHub social preview |
| `instagram-en.png` / `instagram-zh.png` | 1080×1350 (4:5) | Instagram posts |

Sources: `banner.html`, `ig-template.html` (→ `ig-en.html`, `ig-zh.html`) and `promo.css`. The code on the right is a
shortened version of what the ColorLog sample's macros expand to. Re-render with headless Chrome, e.g.

```bash
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless=new --hide-scrollbars \
  --force-device-scale-factor=2 --window-size=1280,640 --screenshot="$PWD/banner.png" "file://$PWD/banner.html"
```
