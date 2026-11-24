"""Turns the Phoenix logo picture (phoenix-source.png, the bird the user chose, on a dark background) into:
  Phoenix.ico      the app icon, 16-256 px, bird on a dark rounded tile
  phoenix.svg      the logo as a vector image (outlines traced from the picture)
  phoenix-512.png  a preview
and offers path_data() / colours for the logo drawn inside the app. Uses only the Python standard library.
"""
import os, struct, sys, zlib

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "phoenix-source.png")
INSET = 0.16          # empty margin around the bird on the tile, as a share of the tile's size


def load_png(path):
    d = open(path, "rb").read()
    assert d[:8] == b"\x89PNG\r\n\x1a\n"
    i, idat, w, h, ct = 8, b"", 0, 0, 0
    while i < len(d):
        n, kind = struct.unpack(">I4s", d[i:i + 8])
        body = d[i + 8:i + 8 + n]
        if kind == b"IHDR":
            w, h, depth, ct, _, _, interlace = struct.unpack(">IIBBBBB", body)
            assert depth == 8 and interlace == 0
        if kind == b"IDAT": idat += body
        i += 12 + n
    bpp = {2: 3, 6: 4}[ct]
    raw, stride = zlib.decompress(idat), w * bpp
    rows, prev, p = [], bytearray(stride), 0
    for _ in range(h):
        f, line = raw[p], bytearray(raw[p + 1:p + 1 + stride]); p += 1 + stride
        for x in range(stride):
            a = line[x - bpp] if x >= bpp else 0
            b = prev[x]
            c = prev[x - bpp] if x >= bpp else 0
            if f == 1: line[x] = (line[x] + a) & 255
            elif f == 2: line[x] = (line[x] + b) & 255
            elif f == 3: line[x] = (line[x] + (a + b) // 2) & 255
            elif f == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(line); prev = line
    return w, h, bpp, rows


class Logo:
    """The picture as a coverage map (0 = background, 1 = bird), with its colours and its outlines."""

    def __init__(self):
        w, h, bpp, rows = load_png(SOURCE)
        self.w, self.h = w, h
        # the bird is bright pink to orange, the background nearly black: the red channel separates them
        corners = [(2, 2), (w - 3, 2), (2, h - 3), (w - 3, h - 3)]
        self.tile = tuple(int(sum(rows[y][x * bpp + k] for x, y in corners) / 4) for k in range(3))
        lo, hi = self.tile[0] + 45, 200
        self.alpha = [[min(1.0, max(0.0, (rows[y][x * bpp] - lo) / (hi - lo))) for x in range(w)] for y in range(h)]
        ys = [y for y in range(h) if max(self.alpha[y]) > 0.5]
        xs = [x for x in range(w) if any(self.alpha[y][x] > 0.5 for y in ys)]
        self.box = (xs[0], ys[0], xs[-1] + 1, ys[-1] + 1)
        # the colour of the bird from top to bottom, taken from the picture itself
        self.stops = []
        for t in (0.0, 0.25, 0.5, 0.75, 1.0):
            y0 = int(self.box[1] + t * (self.box[3] - self.box[1] - 1))
            band = [(rows[y][x * bpp], rows[y][x * bpp + 1], rows[y][x * bpp + 2])
                    for y in range(max(0, y0 - 12), min(h, y0 + 13)) for x in range(w) if self.alpha[y][x] > 0.98]
            self.stops.append((t, tuple(int(sum(c[k] for c in band) / len(band)) for k in range(3))))
        # summed-area table, for fast averages when shrinking to icon sizes
        self.sat = [[0.0] * (w + 1) for _ in range(h + 1)]
        for y in range(h):
            run, above, here, a = 0.0, self.sat[y], self.sat[y + 1], self.alpha[y]
            for x in range(w):
                run += a[x]
                here[x + 1] = above[x + 1] + run

    def colour(self, t):
        t = max(0.0, min(1.0, t))
        for (t0, c0), (t1, c1) in zip(self.stops, self.stops[1:]):
            if t <= t1:
                u = (t - t0) / (t1 - t0)
                return tuple(c0[k] + (c1[k] - c0[k]) * u for k in range(3))
        return self.stops[-1][1]

    def cover(self, x0, y0, x1, y1):
        """Average coverage over a rectangle of the picture (in picture pixels, may be fractional or outside)."""
        def at(x, y):   # the table, read between its entries
            x, y = max(0.0, min(self.w, x)), max(0.0, min(self.h, y))
            xi, yi = min(self.w - 1, int(x)), min(self.h - 1, int(y))
            fx, fy = x - xi, y - yi
            s = self.sat
            return s[yi][xi] * (1 - fx) * (1 - fy) + s[yi][xi + 1] * fx * (1 - fy) + s[yi + 1][xi] * (1 - fx) * fy + s[yi + 1][xi + 1] * fx * fy
        area = (x1 - x0) * (y1 - y0)
        return max(0.0, min(1.0, (at(x1, y1) - at(x0, y1) - at(x1, y0) + at(x0, y0)) / area)) if area > 0 else 0.0

    # ---- vector outlines ----

    def outlines(self):
        """Closed outlines of the bird, in picture pixels: the edge between bird and background, smoothed and thinned."""
        w, h = self.w, self.h
        inside = [[self.alpha[y][x] > 0.5 for x in range(w)] for y in range(h)]
        nxt = {}

        def edge(a, b): nxt.setdefault(a, []).append(b)
        for y in range(h):
            row = inside[y]
            for x in range(w):
                if not row[x]: continue
                if y == 0 or not inside[y - 1][x]: edge((x, y), (x + 1, y))
                if x == w - 1 or not row[x + 1]: edge((x + 1, y), (x + 1, y + 1))
                if y == h - 1 or not inside[y + 1][x]: edge((x + 1, y + 1), (x, y + 1))
                if x == 0 or not row[x - 1]: edge((x, y + 1), (x, y))
        loops = []
        while nxt:
            start = next(iter(nxt))
            loop, cur = [start], start
            while True:
                ends = nxt[cur]
                to = ends.pop()
                if not ends: del nxt[cur]
                if to == start: break
                loop.append(to); cur = to
            if len(loop) >= 40: loops.append(loop)
        return [self._thin(self._smooth(loop), 0.55) for loop in loops]

    @staticmethod
    def _smooth(loop, reach=4):
        # the traced edge runs in pixel steps: average each point with its neighbours to get the line they stand for
        n = len(loop)
        return [(sum(loop[(i + k) % n][0] for k in range(-reach, reach + 1)) / (2 * reach + 1),
                 sum(loop[(i + k) % n][1] for k in range(-reach, reach + 1)) / (2 * reach + 1)) for i in range(n)]

    @staticmethod
    def _thin(loop, eps):
        # keep only the points needed to stay within eps of the line (Douglas-Peucker, on the two halves of the loop)
        def run(pts):
            keep = [False] * len(pts)
            keep[0] = keep[-1] = True
            stack = [(0, len(pts) - 1)]
            while stack:
                a, b = stack.pop()
                ax, ay, bx, by = pts[a][0], pts[a][1], pts[b][0], pts[b][1]
                dx, dy = bx - ax, by - ay
                ll = (dx * dx + dy * dy) ** 0.5 or 1e-9
                worst, at = 0.0, -1
                for i in range(a + 1, b):
                    d = abs((pts[i][0] - ax) * dy - (pts[i][1] - ay) * dx) / ll
                    if d > worst: worst, at = d, i
                if worst > eps:
                    keep[at] = True
                    stack += [(a, at), (at, b)]
            return [p for p, k in zip(pts, keep) if k]
        far = max(range(len(loop)), key=lambda i: (loop[i][0] - loop[0][0]) ** 2 + (loop[i][1] - loop[0][1]) ** 2)
        return run(loop[:far + 1])[:-1] + run(loop[far:] + [loop[0]])[:-1]


_logo = None


def logo():
    global _logo
    if _logo is None: _logo = Logo()
    return _logo


def hexc(c): return "#%02X%02X%02X" % tuple(int(v) for v in c)
def tile_colour(): return hexc(logo().tile)
def gradient(): return [(t, hexc(c)) for t, c in logo().stops]


def path_data():
    """The bird's outlines as one filled path on a 100-unit-wide grid (fill rule: even-odd, set by the caller)."""
    L = logo()
    x0, y0, x1, y1 = L.box
    k = 100.0 / max(x1 - x0, y1 - y0)
    ox, oy = (100 - (x1 - x0) * k) / 2, (100 - (y1 - y0) * k) / 2
    parts = []
    for loop in L.outlines():
        pts = [f"{round((x - x0) * k + ox, 2):g},{round((y - y0) * k + oy, 2):g}" for x, y in loop]
        parts.append("M" + " L".join(pts) + " Z")
    return " ".join(parts)


def render(size, tile=True):
    """RGBA rows of the logo at the given pixel size: the picture shrunk into the tile's middle."""
    L = logo()
    x0, y0, x1, y1 = L.box
    inset = INSET if tile else 0.02
    scale = max(x1 - x0, y1 - y0) / (size * (1 - 2 * inset))      # picture pixels per icon pixel
    left = (x0 + x1) / 2 - size / 2 * scale
    top = (y0 + y1) / 2 - size / 2 * scale
    radius = size * 0.19
    rows = []
    for y in range(size):
        row = bytearray([0])
        sy0, sy1 = top + y * scale, top + (y + 1) * scale
        r, g, b = L.colour(((sy0 + sy1) / 2 - y0) / (y1 - y0))
        for x in range(size):
            c = L.cover(left + x * scale, sy0, left + (x + 1) * scale, sy1)
            if tile:
                qx = max(abs(x + 0.5 - size / 2) - (size / 2 - radius), 0.0)
                qy = max(abs(y + 0.5 - size / 2) - (size / 2 - radius), 0.0)
                a = max(0.0, min(1.0, radius - (qx * qx + qy * qy) ** 0.5 + 0.5))
                t = L.tile
                row += bytes((int(t[0] + (r - t[0]) * c), int(t[1] + (g - t[1]) * c), int(t[2] + (b - t[2]) * c), int(a * 255)))
            else:
                row += bytes((int(r), int(g), int(b), int(c * 255)))
        rows.append(bytes(row))
    return rows


def png(size, tile=True):
    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(b"".join(render(size, tile)), 9)) + chunk(b"IEND", b""))


def svg():
    stops = "".join(f'<stop offset="{t:g}" stop-color="{c}"/>' for t, c in gradient())
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="-8 -8 116 116" width="512" height="512">
  <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1">{stops}</linearGradient></defs>
  <path d="{path_data()}" fill="url(#g)" fill-rule="evenodd"/>
</svg>
'''


if __name__ == "__main__":
    if "--preview" in sys.argv:
        out = sys.argv[sys.argv.index("--preview") + 1]
        open(out, "wb").write(png(384))
        print("wrote", out)
        sys.exit(0)
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [png(s) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, data in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    ico = os.path.join(HERE, "Phoenix.ico")
    open(ico, "wb").write(header + entries + b"".join(images))
    data = path_data()
    open(os.path.join(HERE, "phoenix.svg"), "w", encoding="utf-8").write(svg())
    open(os.path.join(HERE, "phoenix-512.png"), "wb").write(png(512))
    print("wrote", ico, os.path.getsize(ico), "bytes; phoenix.svg; phoenix-512.png")
    print("outlines:", data.count("M"), "shapes,", data.count("L") + data.count("M"), "points,", len(data), "characters; tile", tile_colour(), "gradient", gradient())
