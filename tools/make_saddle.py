"""Generate the Garru Saddle pack's leather seat, woven blanket, texture atlas and FCS header."""
import math
from pathlib import Path
from make_skiff import Mesh, encode_mesh, encode_texture, pack

PACK = Path(__file__).resolve().parents[1] / "mods" / "Garru Saddle"


def saddle():
    mesh = Mesh()
    # Crown and skirts follow the Garru's hump. Raise the pommel and cantle above the seat.
    for side in (-1, 1):
        for z0, z1 in zip([-4, -2.4, 0, 2.4], [-2.4, 0, 2.4, 4]):
            def section(z):
                taper = 1 - 0.17 * abs(z / 4)
                end = (abs(z) / 4) ** 3
                return [(side * x * taper, y + end * rise, z) for x, y, rise in
                        [(0, .48, .85), (1.6, .36, .7), (3.1, -.25, .22),
                         (4.1, -1.1, 0), (4.8, -3, -.25), (4.85, -3.2, -.25)]]
            a, b = section(z0), section(z1)
            for i, swatch in enumerate([0, 0, 1, 2, 1]):
                mesh.face([a[i], b[i], b[i+1], a[i+1]], (side*.4, 1, 0), swatch)
            if abs(z0) == 4 or z1 == 4:
                ring = a if z0 == -4 else b
                for p, q in zip(ring, ring[1:]):
                    mesh.face([p, q, (q[0], q[1]-.28,q[2]), (p[0],p[1]-.28,p[2])],
                              (0,0,ring[0][2]), 1)
        # A leather stirrup strap and a brass foot ring at each skirt.
        x = side * 4.8
        mesh.box((x-.13,-5.1,-.28),(x+.13,-2.5,.24), 1)
        for z in (-.63,.63):
            mesh.box((x-.14,-5.8,z-.1),(x+.14,-5,z+.1), 3)
        mesh.box((x-.22,-5.9,-.72),(x+.22,-5.65,.72), 3)
    return mesh


def paint(x, y):
    band, row = divmod(y, 32)
    base = [(128,72,42), (75,43,32), (65,106,102), (186,141,69)][band]
    grain = ((x*29 + y*59 + x*y*11) % 17) - 8
    if band == 2:
        grain += 11 * ((x//2 + row//2) % 2) - 6
        if row in (4,5,26,27): base = (204,155,86)
    elif band == 0:
        grain += round(4*math.sin(x*.24 + math.sin(row*.57)))
    return bytes(max(0,min(255,c+grain)) for c in base) + b"\xff"


def header():
    def string(s):
        data = s.encode("utf-8")
        return pack("I", len(data)) + data
    fields = pack("I",1) + b"".join(string(s) for s in
        ["", "A riding saddle for squad Garru and Pack Beasts.", "Meitou Transport.mod", ""])
    return pack("II",17,len(fields)) + fields + pack("II",0,0)


if __name__ == "__main__":
    for name, data in [("Garru Saddle.mod", header()),
                       ("meshes/mvv_garru_saddle.mesh", encode_mesh(saddle(), b"MVV_GarruSaddle\n")),
                       ("textures/mvv_garru_saddle.dds", encode_texture(paint))]:
        path = PACK / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
