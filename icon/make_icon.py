"""按 SVG 几何重绘图标，输出 icon-1024.png、多尺寸 app.ico 和预览图。"""
import math
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent

S = 64          # SVG viewBox 单位
K = 64          # 超采样倍数，先画 4096px 再缩
PX = S * K

BLUE = (47, 129, 247, 255)      # #2f81f7
WHITE = (255, 255, 255, 255)

CX, CY, R = 31.4, 30.5, 19.2    # 气泡圆心，三个点共用
STROKE = 4.2


def u(v):
    return v * K


def arc_deg(p):
    """点 p 相对圆心的角度（3 点钟起，顺时针，y 向下）。"""
    return math.degrees(math.atan2(p[1] - CY, p[0] - CX)) % 360


def stroke(draw, pts, width):
    """圆头折线：PIL 的 line 不带端点，自己补圆。"""
    w = u(width)
    xy = [(u(x), u(y)) for x, y in pts]
    draw.line(xy, fill=WHITE, width=int(round(w)), joint="curve")
    for x, y in xy:
        r = w / 2
        draw.ellipse([x - r, y - r, x + r, y + r], fill=WHITE)


def main():
    img = Image.new("RGBA", (PX, PX), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    d.rounded_rectangle([0, 0, PX - 1, PX - 1], radius=u(15), fill=BLUE)

    # 气泡圆：左下留缺口接尾巴
    box = [u(CX - R), u(CY - R), u(CX + R), u(CY + R)]
    a1 = arc_deg((24.0, 48.3))    # 尾巴肩部，刚过圆形最低点
    a2 = arc_deg((17.0, 42.3))    # 尾巴落在圆上的接点
    lw = int(round(u(STROKE)))
    # Pillow 的 arc 也按 3 点钟起顺时针
    d.arc(box, start=0, end=a1, fill=WHITE, width=lw)
    d.arc(box, start=a2, end=360, fill=WHITE, width=lw)

    stroke(d, [(24.0, 48.3), (13.4, 52.4), (17.0, 42.3)], STROKE)

    for dotx in (23.6, 31.4, 39.2):
        r = u(2.6)
        d.ellipse([u(dotx) - r, u(CY) - r, u(dotx) + r, u(CY) + r], fill=WHITE)

    master = img.resize((1024, 1024), Image.LANCZOS)
    master.save(OUT / "icon-1024.png")

    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [master.resize((s, s), Image.LANCZOS) for s in sizes]
    ico = OUT / "app.ico"
    frames[-1].save(ico, format="ICO", sizes=[(s, s) for s in sizes],
                    append_images=frames[:-1])
    print("wrote", OUT / "icon-1024.png", "and", ico, ico.stat().st_size, "bytes")

    # 预览图：大尺寸 + 各真实尺寸横向排一排
    sheet = Image.new("RGBA", (1024 + 260, 300), (245, 247, 250, 255))
    sheet.alpha_composite(master.resize((256, 256), Image.LANCZOS), (20, 22))
    x = 300
    for s in (128, 96, 64, 48, 32, 16):
        sheet.alpha_composite(master.resize((s, s), Image.LANCZOS), (x, 22 + (128 - s) // 2))
        x += s + 14
    sheet.convert("RGB").save(OUT / "preview.png")
    print("wrote", OUT / "preview.png")


if __name__ == "__main__":
    main()
