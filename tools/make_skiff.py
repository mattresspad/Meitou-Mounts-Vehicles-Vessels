"""Build the original Western Hive skiff mesh and diffuse map using Python 3."""

import math
from pathlib import Path
import struct


PACKAGE = Path(__file__).resolve().parents[1] / "mods" / "Western Hive Skiff"
ASSET = "western_hive_demo_skiff"
MATERIAL = b"MVV_WesternHiveSkiff\n"
WOOD, PAINT, TRIM, IRON = range(4)

# Each station gives longitudinal position, half beam, bottom and sheer height.
# Both the hull and its lining follow these sections. Waterline is local Y=0.
STATIONS = [
    (-25, 5.6, 0.1, 6.7), (-19, 7.7, -1.8, 6.0), (-9, 9.0, -2.4, 5.5),
    (2, 8.8, -2.4, 5.5), (12, 7.4, -1.7, 6.0), (20, 4.7, -0.3, 6.8),
    (27, 0.48, 3.3, 8.1),
]


def cross(a, b):
    return (a[1]*b[2] - a[2]*b[1], a[2]*b[0] - a[0]*b[2], a[0]*b[1] - a[1]*b[0])


class Mesh:
    def __init__(self):
        self.vertices = []
        self.indices = []

    def face(self, points, outward, swatch):
        # Flat normals preserve the chines. Map grain along the longest horizontal edge.
        spans = [max(p[k] for p in points) - min(p[k] for p in points) for k in range(3)]
        along = 2 if spans[2] >= spans[0] else 0
        across = 1 if abs(outward[1]) < 0.7 else 2 - along
        base = min(p[across] for p in points)
        for i in range(1, len(points) - 1):
            triangle = [points[0], points[i], points[i+1]]
            a, b, c = triangle
            normal = cross([v-u for u, v in zip(a,b)], [v-u for u, v in zip(a,c)])
            length = math.hypot(*normal)
            if length < 1e-8:  # A half-section tapers to a single keel vertex.
                continue
            if sum(n*d for n,d in zip(normal,outward)) < 0:
                triangle.reverse()
                length = -length
            for p in triangle:
                u = 0.5 + p[along] / 58
                t = (p[across] - base) / max(1e-8, spans[across])
                v = (swatch + 0.055 + 0.89*t) / 4
                self.indices.append(len(self.vertices))
                self.vertices.append((*p, *(n/length for n in normal), u, v))

    def box(self, lo, hi, swatch):
        for axis in range(3):
            u, v = (axis + 1) % 3, (axis + 2) % 3
            for end, direction in [(lo, -1), (hi, 1)]:
                points = []
                for eu, ev in [(lo, lo), (hi, lo), (hi, hi), (lo, hi)]:
                    p = list(lo)
                    p[axis], p[u], p[v] = end[axis], eu[u], ev[v]
                    points.append(p)
                outward = [0, 0, 0]
                outward[axis] = direction
                self.face(points, outward, swatch)


def half_section(station, sign, inside):
    z, width, bottom, top = station
    if inside:
        width *= 0.91
        bottom += 1.0
        if z == STATIONS[0][0]:
            z += 0.55
        elif z == STATIONS[-1][0]:
            z -= 0.45
    profile = [(0, bottom), (0.46, bottom + 0.35), (0.76, bottom + 2.35),
               (0.98, top - 1.05), (1, top)]
    return [(sign*f*width, y, z) for f,y in profile]


def build_hull():
    mesh = Mesh()
    for sign in [-1, 1]:
        for first, last in zip(STATIONS, STATIONS[1:]):
            for inside in (False, True):
                a, b = half_section(first, sign, inside), half_section(last, sign, inside)
                direction = -1 if inside else 1
                for j, swatch in enumerate([WOOD, WOOD, PAINT, TRIM]):
                    outward = (direction*sign, -direction if j < 2 else 0, 0)
                    mesh.face([a[j], a[j+1], b[j+1], b[j]], outward, WOOD if inside else swatch)
            a, b = half_section(first, sign, False), half_section(last, sign, False)
            c, d = half_section(first, sign, True), half_section(last, sign, True)
            mesh.face([a[-1], b[-1], d[-1], c[-1]], (0,1,0), TRIM)

        for station, end in [(STATIONS[0], -1), (STATIONS[-1], 1)]:
            for inside in (False, True):
                ring = half_section(station, sign, inside)
                for j in range(len(ring)-1):
                    p, q = ring[j:j+2]
                    mesh.face([(0,p[1],p[2]), p, q, (0,q[1],q[2])],
                              (0,0,-end if inside else end), WOOD if inside or j < 2 else PAINT)
            p = half_section(station, sign, False)[-1]
            q = half_section(station, sign, True)[-1]
            mesh.face([(0,p[1],p[2]), p, q, (0,q[1],q[2])], (0,1,0), TRIM)

    # Two timber thwarts and their knees. The aft seat surface is centered at (0, 5, -7).
    for z, width in [(-7, 7.65), (9, 6.75)]:
        mesh.box((-width,4.05,z-1.65), (width,5.0,z+1.65), WOOD)
        for sign in [-1,1]:
            x = sign*(width-0.9)
            mesh.box((x-0.23,0.5,z-0.9), (x+0.23,4.05,z+0.9), WOOD)
            mesh.box((x-0.25,5.0,z-0.22), (x+0.25,5.09,z+0.22), IRON)
    # Small raised foredeck over the solid stem, with a single mooring cleat.
    mesh.face([(-4.3,6.8,20),(4.3,6.8,20),(0.4,8.1,27),(-0.4,8.1,27)], (0,1,0), WOOD)
    mesh.box((-0.38,7.2,22.8),(0.38,8.3,23.5), IRON)
    mesh.box((-1.25,8.2,22.7),(1.25,8.55,23.6), IRON)
    return mesh


def pack(fmt, *args):
    return struct.pack("<" + fmt, *args)


def chunk(identifier, contents):
    return pack("HI", identifier, len(contents) + 6) + contents


def encode_mesh(mesh, material=MATERIAL):
    # Ogre v1.100, local geometry, one interleaved position/normal/UV vertex buffer.
    elements = [(2,1,0), (2,4,12), (1,7,24)]
    decl = b"".join(chunk(0x5110, pack("5H", 0, kind, semantic, offset, 0))
                    for kind, semantic, offset in elements)
    data = b"".join(pack("8f", *vertex) for vertex in mesh.vertices)
    geometry = (pack("I",len(mesh.vertices)) + chunk(0x5100, decl)
                + chunk(0x5200, pack("HH", 0, 32) + chunk(0x5210, data)))
    submesh = (material + pack("?I?", False,len(mesh.indices),False)
               + pack(f"{len(mesh.indices)}H", *mesh.indices) + chunk(0x5000, geometry))
    positions = [v[:3] for v in mesh.vertices]
    minimum = map(min, zip(*positions))
    maximum = map(max, zip(*positions))
    radius = max(math.hypot(*p) for p in positions)
    bounds = chunk(0x9000, pack("7f", *minimum, *maximum, radius))
    return pack("H",0x1000) + b"[MeshSerializer_v1.100]\n" + chunk(0x3000, b"\0" + chunk(0x4000, submesh) + bounds)


def paint(x, y):
    # Four bands in one atlas: sun-bleached timber, mineral teal, ochre and aged iron.
    band, row = divmod(y,32)
    base = [(141,101,65), (49,103,101), (207,151,73), (77,75,70)][band]
    grain = math.sin(row*2.2 + 1.4*math.sin(x*0.055 + row*0.09))
    fleck = ((x*67 + y*43 + x*y*11) % 29) / 29
    tone = 7*grain + 6*fleck + 5*math.cos(row*0.38)
    if row % 14 == 0 and band < 2:
        tone -= 20
    if band == PAINT and grain > 0.75 and row % 11 < 4 and fleck > 0.4:
        base = (147,119,79)
    return bytes(max(0,min(255,round(c+tone))) for c in (*base,)) + b"\xff"


def encode_texture(painter=paint):
    size = 128
    colors = [painter(x,y) for y in range(size) for x in range(size)]
    levels = []
    while size:
        levels.append(b"".join(colors))
        colors = [bytes(sum(colors[(y+j)*size+x+i][k] for i in (0,1) for j in (0,1)) // 4
                        for k in range(4)) for y in range(0,size-1,2) for x in range(0,size-1,2)]
        size //= 2
    # DDS, uncompressed R8G8B8A8, all eight mip levels.
    header = pack("7I", 124, 0x2100F, 128, 128, 512, 0, len(levels)) + bytes(44)
    pixel_format = pack("8I", 32, 0x41, 0, 32, 0xFF, 0xFF00, 0xFF0000, 0xFF000000)
    return b"DDS " + header + pixel_format + pack("5I",0x401008,0,0,0,0) + b"".join(levels)


if __name__ == "__main__":
    for folder, filename, data in [
        ("meshes", ASSET+".mesh", encode_mesh(build_hull())),
        ("textures", ASSET+".dds", encode_texture()),
    ]:
        path = PACKAGE / folder / filename
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        print(path, len(data), "bytes")
