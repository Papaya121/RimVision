"""Import missing DLC props from an IsoRim content folder. Requires Pillow."""
import argparse
import hashlib
import math
from collections import Counter
from pathlib import Path
import xml.etree.ElementTree as ET

from PIL import Image


ROOT = Path(__file__).resolve().parents[2]
MOD = ROOT / 'Mods/RimVision'
DEST = MOD / 'OptionalContent/RimVision/Models/IsoRim'
DLCS = ('Anomaly', 'Biotech', 'Odyssey', 'Royalty', 'Ideology')


def text(node, key, default=''):
    return (node.findtext(key) or default).strip()


def vector(value):
    return tuple(float(v.strip()) for v in value.strip('()').split(','))


def number(value):
    return format(value if abs(value) > 1e-8 else 0, '.8g')


def field(node, key, value):
    ET.SubElement(node, key).text = str(value)


def cross(a, b):
    return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])


def rotate(p, degrees):
    angle = math.radians(degrees)
    x, y, z = p
    return (x*math.cos(angle)+z*math.sin(angle), y, z*math.cos(angle)-x*math.sin(angle))


def remove_coplanar_overlaps(vertices, colors, faces, two_sided, face_groups):
    surfaces, planes = [], {}
    for j, (indices, uvs, material) in enumerate(faces):
        for k in range(1, len(indices)-1):
            ii = (indices[0], indices[k], indices[k+1])
            uv = (uvs[0], uvs[k], uvs[k+1])
            points = [vertices[i] for i in ii]
            normal = cross(tuple(points[1][a]-points[0][a] for a in range(3)),
                           tuple(points[2][a]-points[0][a] for a in range(3)))
            length = math.sqrt(sum(n*n for n in normal))
            if length <= 1e-10:
                continue
            normal = tuple(n/length for n in normal)
            sign = next(1 if n > 0 else -1 for n in normal if abs(n) > 1e-6)
            distance = sum(normal[a]*points[0][a] for a in range(3))
            key = tuple(round(n*sign, 5) for n in (*normal, distance))
            bounds = [(min(p[a] for p in points), max(p[a] for p in points)) for a in range(3)]
            surface = (ii, uv, material, two_sided[j], face_groups[j], normal, bounds, key)
            planes.setdefault(key, []).append(len(surfaces))
            surfaces.append(surface)

    def subtract(polygon, clip, normal):
        original = polygon
        outside = []
        for a, b in zip(clip, clip[1:]+clip[:1]):
            edge = tuple(b[c]-a[c] for c in range(3))
            inward = cross(normal, edge)
            inside, fragment = [], []
            for p, q in zip(polygon, polygon[1:]+polygon[:1]):
                dp = sum(inward[c]*(p[0][c]-a[c]) for c in range(3))
                dq = sum(inward[c]*(q[0][c]-a[c]) for c in range(3))
                dp = 0 if abs(dp) < 1e-9 else dp
                dq = 0 if abs(dq) < 1e-9 else dq
                if dp >= 0:
                    inside.append(p)
                if dp <= 0:
                    fragment.append(p)
                if dp*dq < 0:
                    t = dp/(dp-dq)
                    point = tuple(tuple(p[c][v]+t*(q[c][v]-p[c][v]) for v in range(len(p[c])))
                                  if p[c] is not None and q[c] is not None else None for c in range(3))
                    inside.append(point)
                    fragment.append(point)
            if len(fragment) >= 3:
                outside.append(fragment)
            polygon = inside
            if len(polygon) < 3:
                return [original]
        area = 0
        for i in range(1, len(polygon)-1):
            a, b, c = polygon[0][0], polygon[i][0], polygon[i+1][0]
            triangle = cross(tuple(b[v]-a[v] for v in range(3)), tuple(c[v]-a[v] for v in range(3)))
            area += abs(sum(triangle[v]*normal[v] for v in range(3)))
        if area <= 1e-8:
            return [original]
        return outside

    lookup = {(p, colors[i]): i for i, p in enumerate(vertices)}
    result, sided = [], []
    for i, (ii, uv, material, double, group, normal, bounds, key) in enumerate(surfaces):
        polygons = [[(vertices[index], uv[c], colors[index]) for c, index in enumerate(ii)]]
        if not double:
            for j in planes[key]:
                jj, _, _, other_double, other_group, other_normal, other_bounds, _ = surfaces[j]
                if other_double or group == other_group:
                    continue
                if sum(a*b for a, b in zip(normal, other_normal)) > 0 and j < i:
                    continue
                if any(bounds[a][1] < other_bounds[a][0]-1e-8 or other_bounds[a][1] < bounds[a][0]-1e-8 for a in range(3)):
                    continue
                clip = [vertices[index] for index in jj]
                polygons = [part for polygon in polygons for part in subtract(polygon, clip, other_normal)]
                if not polygons:
                    break
        for polygon in polygons:
            for k in range(1, len(polygon)-1):
                triangle = (polygon[0], polygon[k], polygon[k+1])
                a, b, c = (p[0] for p in triangle)
                area = cross(tuple(b[v]-a[v] for v in range(3)), tuple(c[v]-a[v] for v in range(3)))
                if sum(n*n for n in area) <= 1e-16:
                    continue
                indices, points = [], []
                for position, point, color in triangle:
                    vertex_key = (position, color)
                    if vertex_key not in lookup:
                        lookup[vertex_key] = len(vertices)
                        vertices.append(position)
                        colors.append(color)
                    indices.append(lookup[vertex_key])
                    points.append(point)
                result.append((indices, points, material))
                sided.append(double)
    return result, sided


def game_defs(game):
    definitions, parents = {}, {}
    for dlc in ('Core', *DLCS):
        for path in sorted((game / 'Data' / dlc / 'Defs').rglob('*.xml')):
            for node in ET.parse(path).getroot():
                if node.tag != 'ThingDef':
                    continue
                if node.get('Name'):
                    parents[node.get('Name')] = node
                name = text(node, 'defName')
                if name:
                    definitions[name] = (dlc, node)

    def inherited(node, key, default=''):
        if node.find(key) is not None:
            return text(node, key, default)
        parent = parents.get(node.get('ParentName'))
        return inherited(parent, key, default) if parent is not None else default

    def is_door(node):
        if node.get('ParentName') == 'DoorBase':
            return True
        parent = parents.get(node.get('ParentName'))
        return is_door(parent) if parent is not None else False

    return definitions, inherited, is_door


def read_obj(path):
    vertices, colors, uvs, faces, materials = [], [], [], [], {}
    groups, face_groups, group = {}, [], 0
    material = ''
    for line in path.read_text(encoding='utf-8-sig').splitlines():
        parts = line.split()
        if not parts or parts[0].startswith('#'):
            continue
        key, values = parts[0], parts[1:]
        if key == 'o':
            group += 1
        elif key == 'mtllib':
            current = None
            for row in (path.parent / ' '.join(values)).read_text(encoding='utf-8-sig').splitlines():
                tokens = row.split()
                if not tokens:
                    continue
                if tokens[0] == 'newmtl':
                    name = tokens[1]
                    current = materials.setdefault(name, {'color': (1, 1, 1) if name.startswith('stuff') else (.8, .8, .8), 'texture': ''})
                elif current is not None and tokens[0] == 'Kd':
                    current['color'] = tuple(map(float, tokens[1:4]))
                elif current is not None and tokens[0] == 'map_Kd':
                    current['texture'] = ' '.join(tokens[1:])
        elif key == 'usemtl':
            material = values[0]
        elif key == 'v':
            x, y, z = map(float, values[:3])
            vertices.append((x, y, -z))
            colors.append(tuple(map(float, values[3:6])) if len(values) >= 6 else None)
        elif key == 'vt':
            uvs.append(tuple(map(float, values[:2])))
        elif key == 'f':
            indices, points = [], []
            for value in values:
                tokens = value.split('/')
                index = int(tokens[0])
                indices.append(index-1 if index > 0 else len(vertices)+index)
                uv = int(tokens[1]) if len(tokens) > 1 and tokens[1] else 0
                points.append(uvs[uv-1 if uv > 0 else len(uvs)+uv] if uv else (0, 0))
            groups.setdefault(group, []).append(len(faces))
            face_groups.append(group)
            faces.append((indices, points, material))
    two_sided = [True]*len(faces)
    for members in groups.values():
        edges = Counter()
        directions = Counter()
        volume = 0
        for member in members:
            indices = faces[member][0]
            for a, b in zip(indices, indices[1:]+indices[:1]):
                if vertices[a] != vertices[b]:
                    edge = tuple(sorted((vertices[a], vertices[b])))
                    edges[edge] += 1
                    directions[edge] += 1 if vertices[a] < vertices[b] else -1
            a = vertices[indices[0]]
            for i in range(1, len(indices)-1):
                volume += sum(x*y for x, y in zip(a, cross(vertices[indices[i]], vertices[indices[i+1]]))) / 6
        closed = (bool(edges) and all(count == 2 for count in edges.values()) and
                  not any(directions.values()) and abs(volume) > 1e-8)
        for member in members:
            two_sided[member] = not closed
            if volume < -1e-8:
                indices, uv, material = faces[member]
                faces[member] = (list(reversed(indices)), list(reversed(uv)), material)
    faces, two_sided = remove_coplanar_overlaps(vertices, colors, faces, two_sided, face_groups)
    return vertices, faces, materials, colors, two_sided


def color_atlas(path, faces, materials, colors):
    side = math.ceil(math.sqrt(len(faces)))
    image = Image.new('RGB', (side*16, side*16))
    pixels = image.load()
    points = []
    for i, (indices, _, material) in enumerate(faces):
        if materials[material]['texture']:
            raise ValueError('Textured vertex colors need a separate bake: ' + str(path))
        corners = [colors[index] or materials[material]['color'] for index in indices]
        x0, y0 = (i % side)*16, (i // side)*16
        points.append(((x0+2.5, y0+2.5), (x0+13.5, y0+2.5), (x0+2.5, y0+13.5)))
        for y in range(16):
            for x in range(16):
                u, v = max(0, (x-2)/11), max(0, (y-2)/11)
                if u+v > 1:
                    u, v = u/(u+v), v/(u+v)
                pixels[x0+x, y0+y] = tuple(round(255*((1-u-v)*corners[0][c]+u*corners[1][c]+v*corners[2][c])) for c in range(3))
    relative = 'RimVision/IsoRim/' + path.stem + '_VertexColors'
    destination = MOD / 'Textures' / (relative + '.png')
    destination.parent.mkdir(parents=True, exist_ok=True)
    image.save(destination)
    return relative, image.size, points


def export(source, entry, definition, inherited, model_path, path_filter=''):
    name = text(definition, 'defName')
    size = vector(inherited(definition, 'size', '(1,1)'))
    built = vector(text(entry, 'builtFor', inherited(definition, 'size', '(1,1)')))
    swapped = built[0] != built[1] and size[0] != size[1] and (built[0] > built[1]) != (size[0] > size[1])
    sx, sz = (size[1] if swapped else size[0])/built[0], (size[0] if swapped else size[1])/built[1]
    scale = float(text(entry, 'scale', '1'))
    fit = (sx*scale, math.sqrt(sx*sz)*scale, sz*scale)
    yaw = float(text(entry, 'rotation', '0')) + (90 if swapped else 0)
    offset = vector(text(entry, 'offset', '(0,0,0)'))
    result = ET.Element('RimVisionModel')
    field(result, 'thingDef', name)
    if path_filter:
        field(result, 'graphicPathContains', path_filter)
    vertices_node, faces_node = ET.SubElement(result, 'vertices'), ET.SubElement(result, 'faces')
    vertices = []

    def place(p):
        p = rotate(tuple(p[i]*fit[i] + offset[i] for i in range(3)), yaw)
        return (p[0]+size[0]/2, p[1], p[2]+size[1]/2)

    def append(path, turn=0, shift=(0, 0, 0), link=''):
        points, faces, materials, colors, two_sided = read_obj(source / path)
        atlas = color_atlas(Path(path), faces, materials, colors) if any(c is not None for c in colors) else None
        base = len(vertices)
        laid = text(entry, 'layFlat') == 'true'
        lift = max(abs(p[2]) for p in points) if laid else 0
        for p in points:
            p = rotate(p, turn)
            p = tuple(p[i]+shift[i] for i in range(3))
            if laid:
                p = (p[0], -p[2]+lift, p[1])
            vertices.append(place(p))
        for face_index, (indices, uvs, material) in enumerate(faces):
            face = ET.SubElement(faces_node, 'li')
            field(face, 'indices', ','.join(str(base+i) for i in indices))
            field(face, 'twoSided', str(two_sided[face_index]).lower())
            if link:
                field(face, 'requiredLinks', link)
            data = materials[material]
            color = data['color']
            texture = data['texture']
            if atlas:
                field(face, 'texturePath', atlas[0])
                field(face, 'uvTextureSize', ','.join(map(str, atlas[1])))
                field(face, 'uvPoints', ';'.join(number(u)+','+number(v) for u, v in atlas[2][face_index]))
            elif texture:
                texture = texture.replace('{stuff}', 'Metal')
                key = hashlib.sha256((texture+repr(color)).encode()).hexdigest()[:12]
                relative = 'RimVision/IsoRim/' + Path(texture).name + '_' + key
                destination = MOD / 'Textures' / (relative + '.png')
                if not destination.exists():
                    image = Image.open(source / 'Textures' / (texture + '.png')).convert('RGBA')
                    channels = list(image.split())
                    for i in range(3):
                        channels[i] = channels[i].point([round(v*color[i]) for v in range(256)])
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    Image.merge('RGBA', channels).save(destination)
                field(face, 'texturePath', relative)
                image_size = Image.open(destination).size
                field(face, 'uvTextureSize', ','.join(map(str, image_size)))
                field(face, 'uvPoints', ';'.join(number(u*image_size[0])+','+number((1-v)*image_size[1]) for u, v in uvs))
            else:
                field(face, 'solidColor', ','.join(map(number, color)))
            if material.lower().startswith('stuff'):
                field(face, 'tintWithThing', 'true')

    append(model_path)
    for part in entry.findall('parts/li'):
        append(part.text.strip())
    for key in ('bobModel', 'headModel'):
        if text(entry, key):
            append(text(entry, key))
    for part in entry.findall('pulseParts/li'):
        x, z = vector(text(part, 'pivot', '(0,0)'))
        append(text(part, 'model'), shift=(x, 0, z))
    if text(entry, 'linked') == 'true':
        arm = entry.findall('models/li')[1].text.strip()
        for i, direction in enumerate(('North', 'East', 'South', 'West')):
            append(arm, turn=90*i, link=direction)
    radius = float(text(entry, 'sinkHole', '0'))
    if radius:
        cutout = []
        for i in range(32):
            angle = -2*math.pi*i/32
            cutout.append(len(vertices))
            vertices.append(place((radius*math.cos(angle), 0, radius*math.sin(angle))))
        field(result, 'groundCutout', ','.join(map(str, cutout)))
    if text(entry, 'wallMounted') == 'true' or inherited(definition, 'building/isAttachment') == 'true':
        field(result, 'heightOffset', number(max(0, 1.55-max(p[1] for p in vertices))))
    for p in vertices:
        vertex = ET.SubElement(vertices_node, 'li')
        for axis, value in zip(('x', 'height', 'z'), p):
            field(vertex, axis, number(value))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('game', type=Path)
    args = parser.parse_args()
    definitions, inherited, is_door = game_defs(args.game)
    existing = {text(n, 'thingDef') for p in (MOD / 'OptionalContent').rglob('*.xml')
                if not p.is_relative_to(DEST) for n in ET.parse(p).getroot()}
    counts = Counter()
    for entry in ET.parse(args.source / 'Defs/Models.xml').getroot():
        name = text(entry, 'thingDef')
        if name not in definitions or name in existing or text(entry, 'style'):
            continue
        dlc, definition = definitions[name]
        if dlc not in DLCS or inherited(definition, 'category') not in ('Building', 'Item'):
            continue
        if 'Stump' in name or name.startswith('Plant_'):
            continue
        if is_door(definition) or (inherited(definition, 'altitudeLayer') in ('Floor', 'FloorEmplacement') and not text(entry, 'sinkHole')):
            continue
        paths = [text(entry, 'model')] if text(entry, 'model') else [n.text.strip() for n in entry.findall('models/li')]
        if not paths:
            continue
        document = ET.Element('Defs')
        document.append(export(args.source, entry, definition, inherited, paths[0]))
        if text(entry, 'emptyModel'):
            for comp in definition.findall('comps/li'):
                if comp.get('Class') == 'CompProperties_EmptyStateGraphic':
                    graphic_path = text(comp, 'graphicData/texPath')
                    if graphic_path:
                        document.append(export(args.source, entry, definition, inherited, text(entry, 'emptyModel'), graphic_path))
        if len(paths) > 1 and text(entry, 'linked') != 'true':
            graphic_path = inherited(definition, 'graphicData/texPath')
            textures = set()
            for manifest in (args.game / 'Data' / dlc / 'AssetBundles').glob('*.manifest'):
                for line in manifest.read_text(encoding='utf-8-sig').splitlines():
                    if '/Textures/'+graphic_path+'/' in line and line.startswith('- Assets/'):
                        textures.add(Path(line[2:]).stem)
            if not textures:
                raise ValueError('No random graphic variants for ' + name)
            for i, texture in enumerate(sorted(textures)):
                document.append(export(args.source, entry, definition, inherited, paths[i % len(paths)], texture))
        destination = DEST / dlc / (name + '.xml')
        destination.parent.mkdir(parents=True, exist_ok=True)
        ET.indent(document, space='  ')
        ET.ElementTree(document).write(destination, encoding='utf-8', xml_declaration=True)
        counts[dlc] += 1
        existing.add(name)
    print(dict(counts))


if __name__ == '__main__':
    main()
