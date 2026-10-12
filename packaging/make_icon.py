"""Draws the application icon without third-party libraries: icon.png (256px) and icon.ico."""
import struct, zlib, math, os

N = 256
def px(x, y):
    # rounded square background
    r = 48
    dx = max(r - x, 0, x - (N - 1 - r)); dy = max(r - y, 0, y - (N - 1 - r))
    if dx * dx + dy * dy > r * r: return (0, 0, 0, 0)
    bg = (22, 27, 34, 255)
    # toolpath: a rounded rectangle outline and a centre circle, like a cut preview
    def near(d, w=7): return abs(d) <= w
    cx, cy = N / 2, N / 2
    # rounded rect outline 56..200 with radius 30
    a, b, rr = 56, 200, 30
    qx = max(a + rr - x, 0, x - (b - rr)); qy = max(a + rr - y, 0, y - (b - rr))
    d_rect = math.hypot(qx, qy) - rr if (qx or qy) else -min(x - a, b - x, y - a, b - y)
    d_circ = math.hypot(x - cx, y - cy) - 38
    if near(d_rect) or near(d_circ): return (47, 129, 247, 255)
    # origin cross, bottom left
    if (abs(x - 56) <= 4 and 184 <= y <= 216) or (abs(y - 200) <= 4 and 40 <= x <= 72): return (218, 54, 51, 255)
    return bg

rows = b"".join(b"\0" + b"".join(bytes(px(x, y)) for x in range(N)) for y in range(N))
def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", N, N, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b"")
here = os.path.dirname(os.path.abspath(__file__))
open(os.path.join(here, "icon.png"), "wb").write(png)
ico = struct.pack("<HHH", 0, 1, 1) + struct.pack("<BBBBHHII", 0, 0, 0, 0, 1, 32, len(png), 22) + png
open(os.path.join(here, "icon.ico"), "wb").write(ico)
