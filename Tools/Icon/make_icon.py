#!/usr/bin/env python3
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""
Builds ByteExplorer's application icon ("Byte Folder": a blue folder carrying a bold B and the letter's byte,
01000010) from vector drawings, one per size band:

  * 128 px and up: the full drawing (highlight line, the byte as eight bit marks under the B);
  * 32-64 px: no bit marks (they would be sub-pixel), a larger B;
  * 16-24 px: a simplified folder (shorter tab, no highlight) with the B filling the front, so it stays a B.

Output in Assets/Icon/: ByteExplorer.svg (master), one PNG per size, ByteExplorer.ico (16-256: BMP frames, 256 as PNG) and
ByteExplorer.icns (macOS). Rendering is done by Chromium through Playwright (render.js), which is pre-installed in
the dev container; no other tools are needed. The B and the bits are paths, so nothing depends on installed fonts.

Run: python3 Tools/Icon/make_icon.py   (from the repository root or anywhere)
"""
import os, struct, subprocess, sys, json

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "Icon"))

# The B in the 256 grid: outer contour and two counters (even-odd), box x 90..178, y 108..202.
B_PATH = ("M90 108H141C161 108 173 119 173 134C173 144 167 151 159 154C171 157 179 166 179 178"
          "C179 193 167 202 147 202H90Z"
          "M111 125V146H137C147 146 152 141 152 135.5C152 129.5 147 125 137 125Z"
          "M111 162V185H141C152 185 158 180 158 173.5C158 166.5 152 162 141 162Z")
B_BOX = (90, 108, 179, 202)

def b_glyph(cx, top, height, fill="#fff"):
    x0, y0, x1, y1 = B_BOX
    s = height / (y1 - y0)
    tx = cx - (x0 + x1) / 2 * s
    ty = top - y0 * s
    return f'<path transform="translate({tx:.2f} {ty:.2f}) scale({s:.4f})" fill-rule="evenodd" fill="{fill}" d="{B_PATH}"/>'

GRADIENTS = """<defs>
 <linearGradient id="be_back" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#4FA3FF"/><stop offset="1" stop-color="#1D5FD1"/></linearGradient>
 <linearGradient id="be_front" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#7CC0FF"/><stop offset="1" stop-color="#2F7BEA"/></linearGradient>
</defs>"""

def bits(cx, y):
    # 01000010: a 1 is a filled bar, a 0 an outlined ring.
    out, pitch, w, h = [], 15, 8, 13
    left = cx - (pitch * 7 + w) / 2
    for i, bit in enumerate("01000010"):
        x = left + i * pitch
        if bit == "1":
            out.append(f'<rect x="{x:.1f}" y="{y}" width="{w}" height="{h}" rx="2" fill="#fff" opacity=".8"/>')
        else:
            out.append(f'<rect x="{x+1.25:.1f}" y="{y+1.25}" width="{w-2.5}" height="{h-2.5}" rx="2.5" fill="none" stroke="#fff" stroke-width="2.5" opacity=".55"/>')
    return "".join(out)

def drawing(band):
    if band == "large":
        return (GRADIENTS +
            '<path d="M28 60a14 14 0 0 1 14-14h58l20 20h94a14 14 0 0 1 14 14v20H28z" fill="url(#be_back)"/>'
            '<rect x="28" y="86" width="200" height="130" rx="16" fill="url(#be_front)"/>'
            '<rect x="40" y="86" width="176" height="5" rx="2.5" fill="#fff" opacity=".35"/>'
            + b_glyph(128, 103, 86) + bits(128, 196))
    if band == "medium":
        return (GRADIENTS +
            '<path d="M24 60a16 16 0 0 1 16-16h58l20 20h98a16 16 0 0 1 16 16v20H24z" fill="url(#be_back)"/>'
            '<rect x="24" y="86" width="208" height="134" rx="18" fill="url(#be_front)"/>'
            + b_glyph(128, 104, 100))
    # small: chunkier shapes snapped to a 16-px grid (1 unit = 16 px of the 256 grid)
    return (GRADIENTS +
        '<path d="M16 64a16 16 0 0 1 16-16h64l16 16h112a16 16 0 0 1 16 16v16H16z" fill="url(#be_back)"/>'
        '<rect x="16" y="80" width="224" height="144" rx="24" fill="url(#be_front)"/>'
        + b_glyph(128, 96, 112))

def svg(band, size):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 256 256">'
            f'{drawing(band)}</svg>')

def band_for(size):
    return "small" if size <= 24 else "medium" if size <= 64 else "large"

ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
PNG_SIZES = sorted(set(ICO_SIZES + [128, 512, 1024]))

def main():
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "ByteExplorer.svg"), "w") as f:
        f.write(svg("large", 256))
    jobs = [{"svg": svg(band_for(s), s), "size": s, "out": os.path.join(OUT, f"ByteExplorer-{s}.png")} for s in PNG_SIZES]
    subprocess.run(["node", os.path.join(HERE, "render.js")], input=json.dumps(jobs).encode(), check=True)
    pngs = {s: open(os.path.join(OUT, f"ByteExplorer-{s}.png"), "rb").read() for s in PNG_SIZES}
    write_ico(os.path.join(OUT, "ByteExplorer.ico"), pngs)
    write_icns(os.path.join(OUT, "ByteExplorer.icns"), pngs)
    print("wrote", OUT)

def write_ico(path, pngs):
    # The usual layout: 32-bit BMP frames (with an AND mask) below 256 px, which every Windows icon API reads, and a
    # PNG frame at 256 px (Vista and later), which keeps the file small.
    entries = []
    for s in ICO_SIZES:
        entries.append((s, pngs[s] if s >= 256 else bmp_frame(*decode_png(pngs[s]))))
    header = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    directory, data = b"", b""
    for s, frame in entries:
        d = 0 if s >= 256 else s
        directory += struct.pack("<BBBBHHII", d, d, 0, 0, 1, 32, len(frame), offset + len(data))
        data += frame
    with open(path, "wb") as f:
        f.write(header + directory + data)

def bmp_frame(width, height, rgba):
    # BITMAPINFOHEADER with double height (XOR image + AND mask), rows bottom-up, BGRA.
    info = struct.pack("<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    xor = bytearray()
    mask = bytearray()
    mask_stride = ((width + 31) // 32) * 4
    for y in range(height - 1, -1, -1):
        row = rgba[y * width * 4:(y + 1) * width * 4]
        bits = bytearray(mask_stride)
        for x in range(width):
            r, g, b, a = row[x * 4:x * 4 + 4]
            xor += bytes((b, g, r, a))
            if a == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        mask += bits
    return info + bytes(xor) + bytes(mask)

def decode_png(data):
    # Minimal decoder for what render.js writes: 8-bit RGBA (colour type 6), not interlaced. No PIL needed.
    import zlib
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    pos, idat, width, height = 8, b"", 0, 0
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        chunk = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            width, height, depth, colour, _, _, interlace = struct.unpack(">IIBBBBB", chunk)
            if (depth, colour, interlace) != (8, 6, 0):
                raise ValueError(f"unsupported PNG: depth {depth}, colour type {colour}, interlace {interlace}")
        elif kind == b"IDAT":
            idat += chunk
        pos += 12 + length
    raw = zlib.decompress(idat)
    stride = width * 4
    out = bytearray(height * stride)
    prev = bytearray(stride)
    for y in range(height):
        f = raw[y * (stride + 1)]
        line = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for i in range(stride):
            a = line[i - 4] if i >= 4 else 0
            b = prev[i]
            c = prev[i - 4] if i >= 4 else 0
            if f == 1:
                line[i] = (line[i] + a) & 255
            elif f == 2:
                line[i] = (line[i] + b) & 255
            elif f == 3:
                line[i] = (line[i] + (a + b) // 2) & 255
            elif f == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        out[y * stride:(y + 1) * stride] = line
        prev = line
    return width, height, bytes(out)

def write_icns(path, pngs):
    # PNG chunks: icp4 16, icp5 32, icp6 64, ic07 128, ic08 256, ic09 512, ic10 1024 (512@2x).
    types = [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)]
    body = b"".join(t.encode() + struct.pack(">I", 8 + len(pngs[s])) + pngs[s] for t, s in types)
    with open(path, "wb") as f:
        f.write(b"icns" + struct.pack(">I", 8 + len(body)) + body)

if __name__ == "__main__":
    sys.exit(main())
