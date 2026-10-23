import math
import xml.etree.ElementTree as ET

from .catalog import text


def value(node, key, default="0"):
    result = float(text(node, key, default))
    if not math.isfinite(result):
        raise ValueError("Non-finite model coordinate")
    return result


def read_model(file, index=0, definition=""):
    nodes = list(ET.parse(file).getroot())
    if not 0 <= index < len(nodes):
        raise ValueError("Model entry no longer exists; refresh catalog")
    node = nodes[index]
    name = text(node, "thingDef")
    if definition and name != definition:
        raise ValueError("The XML model belongs to " + name + ", not " + definition)
    vertices = [(value(v, "x"), value(v, "height"), value(v, "z")) for v in node.findall("vertices/li")]
    faces = []
    for face in node.findall("faces/li"):
        indices = [int(i.strip()) for i in text(face, "indices").split(",")]
        if len(indices) < 3 or any(i < 0 or i >= len(vertices) for i in indices):
            raise ValueError("Invalid face indices")
        normal_text = text(face, "normals")
        normals = [tuple(float(c) for c in normal.split(",")) for normal in normal_text.split(";")] if normal_text else None
        if normals is not None and (len(normals) != len(indices) or any(len(n) != 3 or any(not math.isfinite(c) for c in n) or sum(c * c for c in n) <= 1e-16 for n in normals)):
            raise ValueError("Invalid corner normals")
        direction = text(face, "texture", "Thing")
        if direction not in ("Thing", "North", "East", "South", "West"):
            direction = "Thing"
        solid = text(face, "solidColor")
        color = tuple(float(c) for c in solid.split(",")) if solid else None
        if color and (len(color) != 3 or any(not math.isfinite(c) for c in color)):
            raise ValueError("Invalid solid color")
        uv = text(face, "uvPoints")
        if uv:
            points = [tuple(float(c) for c in point.split(",")) for point in uv.split(";")]
            if len(points) != len(indices) or any(len(p) != 2 or any(not math.isfinite(c) for c in p) for p in points):
                raise ValueError("Invalid UV points")
        else:
            points = None
        rect = text(face, "uvPixels")
        rectangle = tuple(float(c) for c in rect.split(",")) if rect else None
        if rectangle and (len(rectangle) != 4 or any(not math.isfinite(c) for c in rectangle)):
            raise ValueError("Invalid UV rectangle")
        texture_path = text(face, "texturePath")
        if texture_path and color:
            raise ValueError("A face cannot have both a custom texture and solid color")
        size = text(face, "uvTextureSize")
        uv_size = tuple(float(c) for c in size.split(",")) if size else None
        if uv_size and (len(uv_size) != 2 or any(not math.isfinite(c) or c <= 0 for c in uv_size)):
            raise ValueError("Invalid UV texture size")
        faces.append(dict(indices=indices, normals=normals, smooth=text(face, "smooth", "true" if normals else "false") == "true", direction=direction, texture_path=texture_path, color=color, tint=text(face, "tintWithThing") == "true",
                          two_sided=text(face, "twoSided", "true") == "true", turret_top=text(face, "turretTop") == "true",
                          required_links=text(face, "requiredLinks"), forbidden_links=text(face, "forbiddenLinks"),
                          uv=points, rect=rectangle, uv_size=uv_size, flip_u=text(face, "flipU") == "true", flip_v=text(face, "flipV") == "true"))
    cutout = [int(i) for i in text(node, "groundCutout").split(",")] if text(node, "groundCutout") else []
    if cutout and (len(cutout) < 3 or any(i < 0 or i >= len(vertices) for i in cutout)):
        raise ValueError("Invalid ground cutout")
    if not faces:
        raise ValueError("No faces in model")
    anchors, effects = [], set()
    for anchor in node.findall("anchors/li"):
        for key in ("x", "height", "z"):
            value(anchor, key)
        if text(anchor, "name") in ("fire", "light"):
            kind = text(anchor, "name")
            if kind in effects:
                raise ValueError("Duplicate effect anchor")
            effects.add(kind)
            if not 0.05 <= value(anchor, "size", "1") <= 5 or not 0.1 <= value(anchor, "radius", "8") <= 32 or not 0 <= value(anchor, "intensity", "1") <= 8:
                raise ValueError("Invalid effect parameters")
            channels = tuple(float(c) for c in text(anchor, "color", "1,1,1").split(","))
            if len(channels) != 3 or any(not math.isfinite(c) or not 0 <= c <= 1 for c in channels):
                raise ValueError("Invalid effect color")
        anchors.append(ET.tostring(anchor, encoding="unicode"))
    return dict(defName=name, vertices=vertices, faces=faces, height_offset=value(node, "heightOffset"),
                filter=text(node, "graphicPathContains"), cutout=cutout, anchors=anchors)


def face_uvs(face, vertices):
    if face["uv"]:
        points = face["uv"]
        low = [min(p[i] for p in points) for i in range(2)]
        high = [max(p[i] for p in points) for i in range(2)]
        return [(low[0] + high[0] - u if face["flip_u"] else u,
                 low[1] + high[1] - v if face["flip_v"] else v) for u, v in points], True
    points = [vertices[i] for i in face["indices"]]
    normal = [0, 0, 0]
    for a, b in zip(points, points[1:] + points[:1]):
        normal[0] += (a[1] - b[1]) * (a[2] + b[2])
        normal[1] += (a[2] - b[2]) * (a[0] + b[0])
        normal[2] += (a[0] - b[0]) * (a[1] + b[1])
    ax, ay, az = (abs(n) for n in normal)
    axis = 1 if ay >= ax and ay >= az else 2 if az >= ax else 0
    u_axis, v_axis = (0, 2) if axis == 1 else (0, 1) if axis == 2 else (2, 1)
    low = [min(p[i] for p in points) for i in range(3)]
    high = [max(p[i] for p in points) for i in range(3)]
    result = []
    rect = face["rect"]
    pixel = bool(rect and rect[0] != rect[2] and rect[1] != rect[3])
    for point in points:
        u = (point[u_axis] - low[u_axis]) / (high[u_axis] - low[u_axis]) if high[u_axis] != low[u_axis] else 0
        v = (point[v_axis] - low[v_axis]) / (high[v_axis] - low[v_axis]) if high[v_axis] != low[v_axis] else 0
        if face["flip_u"]:
            u = 1 - u
        if face["flip_v"]:
            v = 1 - v
        if pixel:
            u = min(rect[0], rect[2]) + abs(rect[2] - rect[0]) * u
            v = max(rect[1], rect[3]) - abs(rect[3] - rect[1]) * v
        result.append((u, v))
    return result, pixel
