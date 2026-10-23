"""Generate only AncientWheel XML meshes, using the original RimWorld artwork."""
import math
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / 'Mods/RimVision/OptionalContent/RimWorld3DVR/Models/Core'
SIDES = 20
# Original sprite centres and opaque rim radii, in pixels (origin at top left).
ART = {
    'A': (32, 29, 21, 22, 0),
    'B': (32, 28, 21, 21, 6),
    'C': (32, 30, 22, 23, -5),
    'D': (32, 28, 21, 22, 3),
    'E': (32, 30, 22, 22, 8),
    'F': (30, 26, 22, 21, -7),
}
# Radius, height, artwork radius: bevelled rim, recessed dish, raised hub.
PROFILE = (
    (.335, .012, .88),
    (.380, .045, .96),
    (.380, .185, .96),
    (.350, .225, .92),
    (.275, .225, .73),
    (.235, .125, .62),
    (.090, .125, .24),
    (.075, .175, .20),
)


def number(value):
    return f'{value:.6f}'.rstrip('0').rstrip('.') or '0'


def generate(letter, fallback=False):
    cx, cy, rx, ry, tilt = ART[letter]
    if fallback:
        tilt = 0
    angle = math.radians(tilt)
    points, uvs = [], []
    for radius, height, artwork_radius in PROFILE:
        for i in range(SIDES):
            theta = i * math.tau / SIDES
            # Small dents retain a simple silhouette instead of a perfect cylinder.
            dent = 1 - (.07 if letter == 'E' and i in (2, 3, 4) else 0)
            x = radius * math.cos(theta) * dent
            z = radius * math.sin(theta) * dent
            y = height
            if letter == 'E' and i in (2, 3, 4):
                y *= .88
            points.append((x + .5, y * math.cos(angle) + z * math.sin(angle),
                           z * math.cos(angle) - y * math.sin(angle) + .5))
            uvs.append((cx + rx * artwork_radius * math.cos(theta),
                        cy - ry * artwork_radius * math.sin(theta)))
    ground = min(p[1] for p in points)
    defs = ET.Element('Defs')
    model = ET.SubElement(defs, 'RimWorld3DVR.Render.Rim3DModelDef')
    suffix = '' if fallback else '_Wheel' + letter
    ET.SubElement(model, 'defName').text = 'Rim3DVR_AncientWheelModel' + suffix
    ET.SubElement(model, 'thingDef').text = 'AncientWheel'
    if not fallback:
        ET.SubElement(model, 'graphicPathContains').text = 'Wheel' + letter
    model.append(ET.Comment(' Original game texture on every face; no solid-colour body. '))
    vertices = ET.SubElement(model, 'vertices')
    for x, y, z in points:
        vertex = ET.SubElement(vertices, 'li')
        for key, value in (('x', x), ('z', z), ('height', y - ground + .008)):
            ET.SubElement(vertex, key).text = number(value)
    faces = ET.SubElement(model, 'faces')

    def face(indices):
        node = ET.SubElement(faces, 'li')
        ET.SubElement(node, 'indices').text = ','.join(map(str, indices))
        ET.SubElement(node, 'texture').text = 'Thing'
        ET.SubElement(node, 'uvPoints').text = ';'.join(
            number(uvs[i][0]) + ',' + number(uvs[i][1]) for i in indices)
        ET.SubElement(node, 'twoSided').text = 'false'

    for ring in range(len(PROFILE) - 1):
        lower, upper = ring * SIDES, (ring + 1) * SIDES
        for i in range(SIDES):
            j = (i + 1) % SIDES
            face([lower + i, upper + i, upper + j, lower + j])
    face(list(range(SIDES)))
    face(list(reversed(range((len(PROFILE) - 1) * SIDES, len(PROFILE) * SIDES))))
    ET.indent(defs, space='  ')
    path = OUTPUT / ('Rim3DVR_AncientWheelModel' + suffix + '.xml')
    ET.ElementTree(defs).write(path, encoding='utf-8', xml_declaration=True)
    print(path.relative_to(ROOT))


if __name__ == '__main__':
    for variant in ART:
        generate(variant)
    # Graphic_Random can expose the folder path instead of an individual sprite.
    generate('A', fallback=True)
