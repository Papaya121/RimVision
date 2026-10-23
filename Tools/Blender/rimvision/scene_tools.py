import math
import hashlib
import tempfile
import xml.etree.ElementTree as ET
from array import array
from pathlib import Path

import bpy
from mathutils import Euler, Vector

from .model import face_uvs, read_model
from .i18n import tr


def sprite_path(record, direction):
    return record.get("sprite" + ("North" if direction == "Thing" else direction), "")


def name_resource(resource, record, kind):
    if resource.library is not None:
        return
    previous = resource.get("rimvision_auto_name")
    if previous is not None and resource.name != previous:
        return
    if resource.get("rimvision_resource_def", record["defName"]) != record["defName"]:
        return
    resource.name = "RV | " + record["defName"] + " | " + tr(kind)
    resource["rimvision_auto_name"] = resource.name
    resource["rimvision_resource_def"] = record["defName"]


def game_image(record, direction, path, original=False):
    image = bpy.data.images.load(path, check_existing=True)
    if image.get("rimvision_auto_name") is not None or image.name == Path(path).name:
        actual = next((key for key, value in record.get("directionalSprites", {}).items() if value == path), "")
        if original and not actual:
            actual = next((key for key in ("North", "East", "South", "West") if record.get("baseSprite" + key) == path), "")
        kind = {"North": "North", "South": "South", "East": "East", "West": "West"}.get(actual, "Sprite")
        if original and path != sprite_path(record, direction):
            kind += " · original"
        name_resource(image, record, kind)
    return image


def image_for(record, direction, original=False):
    path = record.get("baseSprite" + ("North" if direction == "Thing" else direction), "") if original else sprite_path(record, direction)
    return game_image(record, direction, path, original) if path and Path(path).is_file() else None


def atlas_image(record):
    directional = record.get("directionalSprites") or {}
    tiles = []
    for direction, column, row in (("North", 0, 1), ("East", 1, 1), ("South", 0, 0), ("West", 1, 0)):
        path = directional.get(direction, "") if directional else sprite_path(record, direction)
        mirrored = False
        if not path and direction in ("East", "West"):
            path = directional.get("West" if direction == "East" else "East", "")
            mirrored = bool(path)
        if path and Path(path).is_file():
            tiles.append((path, column, row, mirrored, Path(path).stat().st_mtime_ns))
    if not tiles:
        raise ValueError("Prepare item sprites before creating an atlas")
    fingerprint = hashlib.sha256(repr(tiles).encode("utf-8")).hexdigest()
    name = "RV | " + record["defName"] + " | Atlas 2×2"
    existing = next((image for image in bpy.data.images if image.get("rimvision_atlas") == fingerprint), None)
    if existing is not None:
        if existing.get("rimvision_auto_name") or existing.name.startswith("RimVision atlas "):
            name_resource(existing, record, "Atlas 2×2")
        return existing
    images = [(game_image(record, "Thing", path), column, row, mirrored) for path, column, row, mirrored, _ in tiles]
    width = max(image.size[0] for image, _, _, _ in images)
    height = max(image.size[1] for image, _, _, _ in images)
    if min(width, height) <= 0:
        raise ValueError("Could not read sprite sizes for the atlas")
    pixels = array("f", [0]) * (width * height * 16)
    for image, column, row, mirrored in images:
        source_width, source_height = image.size
        source = array("f", [0]) * (source_width * source_height * 4)
        image.pixels.foreach_get(source)
        left = column * width + (width - source_width) // 2
        bottom = row * height + (height - source_height) // 2
        for y in range(source_height):
            start = ((bottom + y) * width * 2 + left) * 4
            line = source[y * source_width * 4:(y + 1) * source_width * 4]
            if mirrored:
                flipped = array("f")
                for x in range(source_width - 1, -1, -1):
                    flipped.extend(line[x * 4:x * 4 + 4])
                line = flipped
            pixels[start:start + len(line)] = line
    image = bpy.data.images.new(name, width=width * 2, height=height * 2, alpha=True)
    image.pixels.foreach_set(pixels)
    image.update()
    image["rimvision_atlas"] = fingerprint
    name_resource(image, record, "Atlas 2×2")
    image.pack()
    return image


def surface_image(surface, record):
    if surface.mode == "Atlas":
        return atlas_image(record)
    if surface.mode == "Custom":
        path = bpy.path.abspath(surface.texture_path)
        if not surface.texture_path or not Path(path).is_file():
            raise ValueError("Choose an existing custom texture file")
        return bpy.data.images.load(path, check_existing=True)
    return image_for(record, surface.direction) if surface.mode == "Game" else None


def object_surface(material):
    if not material.use_nodes:
        return None, tuple(material.diffuse_color[:3]), ""
    output = next((node for node in material.node_tree.nodes if node.type == "OUTPUT_MATERIAL" and node.is_active_output), None)
    if output is None or not output.inputs["Surface"].is_linked:
        raise ValueError(material.name + ": connect Principled BSDF to Material Output")
    shader = output.inputs["Surface"].links[0].from_node
    if shader.type != "BSDF_PRINCIPLED":
        raise ValueError(material.name + ": use a color or Image Texture in Principled BSDF Base Color; bake complex materials first")
    base_color = shader.inputs["Base Color"]
    if not base_color.is_linked:
        return None, tuple(base_color.default_value[:3]), ""
    link = base_color.links[0]
    node = link.from_node
    if node.type != "TEX_IMAGE" or link.from_socket.name != "Color" or node.image is None:
        raise ValueError(material.name + ": connect Image Texture directly to Base Color or bake the material")
    if node.projection != "FLAT":
        raise ValueError(material.name + ": texture export requires Flat UV projection")
    uv_name = ""
    if node.inputs["Vector"].is_linked:
        vector = node.inputs["Vector"].links[0]
        if vector.from_node.type == "UVMAP":
            uv_name = vector.from_node.uv_map
        elif vector.from_node.type != "TEX_COORD" or vector.from_socket.name != "UV":
            raise ValueError(material.name + ": use UV or UV Map without Mapping; apply transforms to UV first")
    return node.image, (1, 1, 1), uv_name


def prepare_texture(surface, record, image=None):
    if image is None:
        image = surface_image(surface, record)
    if image is None or min(image.size) <= 0:
        raise ValueError("Could not load the custom texture")
    size = tuple(image.size)
    copy = image.copy()
    try:
        copy.update()
        copy.scale(*size)
        if image.is_dirty:
            copy.pixels.foreach_set(image.pixels[:])
        with tempfile.TemporaryDirectory(prefix="rimvision-texture-") as folder:
            path = Path(folder) / "texture.png"
            copy.file_format = "PNG"
            copy.filepath_raw = str(path)
            copy.save()
            data = path.read_bytes()
    finally:
        bpy.data.images.remove(copy)
    return "RimVision/Custom/" + hashlib.sha256(data).hexdigest(), data, size


def imported_texture(file, texture_path):
    relative = Path(texture_path.replace("\\", "/"))
    if relative.is_absolute() or ".." in relative.parts or ":" in texture_path:
        raise ValueError("Invalid custom texture path")
    for parent in Path(file).resolve().parents:
        if parent.name == "OptionalContent":
            return str(parent.parent / "Textures" / (str(relative) + ".png"))
    raise ValueError("Cannot locate the model's mod folder")


def name_material(material, record):
    surface = material.rimvision
    if surface.mode != "Object" and material.get("rimvision_auto_name") is not None:
        kind = {"Solid": "Color", "Custom": "Custom texture", "Atlas": "Atlas 2×2"}.get(surface.mode,
            "Game · " + {"Thing": "Auto", "North": "North", "South": "South", "East": "East", "West": "West"}.get(surface.direction, surface.direction))
        name_resource(material, record, kind)


def material_preview(material, record):
    surface = material.rimvision
    if surface.mode == "Object":
        return
    name_material(material, record)
    material.use_backface_culling = not surface.two_sided
    material.diffuse_color = (*surface.color, 1) if surface.mode == "Solid" else (1, 1, 1, 1)
    material.use_nodes = True
    shader = material.node_tree.nodes.get("Principled BSDF")
    if shader is None:
        return
    shader.inputs["Roughness"].default_value = 0.85
    for name in ("Base Color", "Alpha"):
        for link in list(shader.inputs[name].links):
            material.node_tree.links.remove(link)
    shader.inputs["Base Color"].default_value = material.diffuse_color
    shader.inputs["Alpha"].default_value = 1
    if surface.mode in ("Game", "Custom", "Atlas"):
        image = surface_image(surface, record)
        if image:
            node = next((n for n in material.node_tree.nodes if n.type == "TEX_IMAGE"), None)
            if node is None:
                node = material.node_tree.nodes.new("ShaderNodeTexImage")
            node.image = image
            node.interpolation = "Closest"
            if surface.mode == "Atlas":
                material.node_tree.nodes.active = node
                node.select = True
            material.node_tree.links.new(node.outputs["Color"], shader.inputs["Base Color"])
            material.node_tree.links.new(node.outputs["Alpha"], shader.inputs["Alpha"])


def guides(scene):
    collection = scene.rimvision.guides
    if collection is None:
        collection = bpy.data.collections.new("RimVision References")
        scene.collection.children.link(collection)
        scene.rimvision.guides = collection
    return collection


def frame_model(context, record, obj):
    if context.area and context.area.type == "VIEW_3D" and context.space_data.region_3d:
        context.space_data.shading.type = "MATERIAL"
        view = context.space_data.region_3d
        view.view_location = (record["width"] * 0.4, 0, obj.dimensions.z * 0.5)
        view.view_distance = max(record["width"], record["depth"], *obj.dimensions, 1) * 3
        view.view_rotation = Euler((math.radians(65), 0, math.radians(25))).to_quaternion()
        view.view_perspective = "PERSP"


def show_catalog_models(context, record):
    definition = record["defName"] if record else ""
    layer = context.view_layer
    layer.update()
    visible = []
    for obj in list(layer.objects):
        if obj is None or not obj.get("rimvision_def") or obj.get("rimvision_role"):
            continue
        if obj.parent and obj.parent.get("rimvision_def") == obj["rimvision_def"]:
            continue
        hide = obj["rimvision_def"] != definition
        for member in (obj, *obj.children_recursive):
            if member is None or layer.objects.get(member.name) is None:
                continue
            states = member.get("rimvision_catalog_hidden")
            if hide:
                if states is None:
                    member["rimvision_catalog_hidden"] = {}
                    states = member["rimvision_catalog_hidden"]
                if layer.name not in states:
                    states[layer.name] = member.hide_get(view_layer=layer)
                member.hide_set(True, view_layer=layer)
            elif states is not None and layer.name in states:
                member.hide_set(bool(states[layer.name]), view_layer=layer)
                del states[layer.name]
                if not len(states):
                    del member["rimvision_catalog_hidden"]
        if not hide and not obj.hide_get(view_layer=layer):
            visible.append(obj)
    active = layer.objects.active
    if visible and (active is None or (active.get("rimvision_def") and (active["rimvision_def"] != definition or not active.select_get(view_layer=layer)))):
        obj = visible[-1]
        obj.select_set(True, view_layer=layer)
        layer.objects.active = obj
        context.scene.rimvision.graphic_filter = obj.get("rimvision_filter", "")
        context.scene.rimvision.height_offset = obj.get("rimvision_height_offset", 0.0)


def reference_bottom_padding(image):
    cached = image.get("rimvision_reference_padding")
    if cached is not None and not image.is_dirty:
        return cached
    width, height = image.size
    if min(width, height) <= 0:
        return 0
    pixels = array("f", [0]) * (width * height * 4)
    image.pixels.foreach_get(pixels)
    padding = next(((index // 4 // width) / height for index in range(3, len(pixels), 4) if pixels[index] >= .25), 0)
    image["rimvision_reference_padding"] = padding
    return padding


def show_reference(scene, record):
    collection = guides(scene)
    for obj in list(collection.objects):
        if obj.get("rimvision_guide"):
            data = obj.data
            kind = obj.type
            bpy.data.objects.remove(obj, do_unlink=True)
            if data and data.users == 0 and kind in ("CURVE", "FONT"):
                bpy.data.curves.remove(data)
    width, depth = record["width"], record["depth"]
    curve = bpy.data.curves.new("RimVision cell footprint", "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = 0.006
    material = next((m for m in bpy.data.materials if m.get("rimvision_guide_material")), None)
    if material is None:
        material = bpy.data.materials.new("RimVision guide blue")
        material["rimvision_guide_material"] = True
        material.diffuse_color = (0.15, 0.65, 1, 1)
        material.use_nodes = True
        material.node_tree.nodes.clear()
        output = material.node_tree.nodes.new("ShaderNodeOutputMaterial")
        emission = material.node_tree.nodes.new("ShaderNodeEmission")
        emission.inputs["Color"].default_value = material.diffuse_color
        material.node_tree.links.new(emission.outputs["Emission"], output.inputs["Surface"])
    curve.materials.append(material)
    for x in range(width + 1):
        spline = curve.splines.new("POLY")
        spline.points.add(1)
        spline.points[0].co = (x - width / 2, -depth / 2, 0, 1)
        spline.points[1].co = (x - width / 2, depth / 2, 0, 1)
    for y in range(depth + 1):
        spline = curve.splines.new("POLY")
        spline.points.add(1)
        spline.points[0].co = (-width / 2, y - depth / 2, 0, 1)
        spline.points[1].co = (width / 2, y - depth / 2, 0, 1)
    obj = bpy.data.objects.new("RimVision footprint", curve)
    collection.objects.link(obj)
    obj.color = (0.15, 0.65, 1, 1)
    obj.show_in_front = True
    obj.hide_select = True
    obj.hide_render = True
    obj["rimvision_guide"] = True
    font = bpy.data.curves.new("RimVision reference label", "FONT")
    font.body = record["defName"] + "  |  " + tr(str(width) + " x " + str(depth) + " cells  |  North +Y")
    font.size = 0.15
    font.materials.append(material)
    label = bpy.data.objects.new("RimVision reference label", font)
    collection.objects.link(label)
    label.location = (-width / 2, depth / 2 + 0.15, 0)
    label.hide_select = label.hide_render = True
    label["rimvision_guide"] = True
    settings = scene.rimvision
    directional = record.get("directionalSprites")
    if directional is None:
        candidates = {direction: sprite_path(record, direction) for direction in ("North", "East", "South", "West")}
        directional = {direction: path for direction, path in candidates.items() if path} if len(set(candidates.values()) - {""}) > 1 else {}
    views = []
    if settings.reference_all_sides and directional:
        for direction in ("South", "North", "East", "West"):
            path = directional.get(direction, "")
            mirrored = False
            if not path and direction in ("East", "West"):
                path = directional.get("West" if direction == "East" else "East", "")
                mirrored = bool(path)
            if path and Path(path).is_file():
                views.append((direction, game_image(record, direction, path), mirrored))
    if not views:
        image = image_for(record, settings.reference_direction)
        if image:
            views.append(("", image, False))
    for direction, image, mirrored in views:
        title = {"South": "Front –  face north (+Y)", "North": "Back –  tail south (−Y)", "East": "Side (east)", "West": "Side (west)"}.get(direction, "sprite reference")
        reference = bpy.data.objects.new("RimVision –  " + tr(title), None)
        collection.objects.link(reference)
        reference.empty_display_type = "IMAGE"
        reference.data = image
        reference.empty_display_size = max(width, depth, 1)
        sized_plant = record.get("category") == "Plant" and record.get("referenceWidth") and record.get("referenceHeight") and min(image.size) > 0
        padding_height = 0
        if sized_plant:
            reference.empty_display_size = max(record["referenceWidth"], record["referenceHeight"])
            longest = max(*image.size, 1)
            reference.scale.x = record["referenceWidth"] / (reference.empty_display_size * image.size[0] / longest)
            reference.scale.y = record["referenceHeight"] / (reference.empty_display_size * image.size[1] / longest)
            padding_height = record["referenceHeight"] * reference_bottom_padding(image)
        reference.empty_image_depth = "BACK"
        reference.empty_image_side = "DOUBLE_SIDED"
        reference.use_empty_image_alpha = True
        reference.color = (1, 1, 1, scene.rimvision.reference_opacity)
        reference.rotation_euler = settings.reference_rotation
        half = reference.empty_display_size / 2
        if sized_plant:
            half = record["referenceHeight"] / 2
        gap = settings.reference_gap
        position = {
            "Right": (width / 2 + half + gap, 0, half),
            "Left": (-width / 2 - half - gap, 0, half),
            "North": (0, depth / 2 + half + gap, half),
            "South": (0, -depth / 2 - half - gap, half),
            "Above": (0, 0, reference.empty_display_size + gap),
            "Below": (0, 0, -half - gap),
        }[settings.reference_side]
        if direction:
            half_height = reference.empty_display_size * image.size[1] / max(*image.size, 1) * reference.scale.y / 2
            position, angle = {
                "South": ((0, -depth / 2 - gap, half_height), math.pi),
                "North": ((0, depth / 2 + gap, half_height), 0),
                "East": ((-width / 2 - gap, 0, half_height), math.pi / 2),
                "West": ((width / 2 + gap, 0, half_height), -math.pi / 2),
            }[direction]
            reference.rotation_euler = (math.pi / 2, 0, angle)
            reference.scale.x *= -1 if mirrored else 1
            reference.empty_image_side = "FRONT"
        reference.location = tuple(value + offset for value, offset in zip(position, settings.reference_offset))
        if sized_plant:
            reference.location.z -= padding_height
        reference.hide_select = reference.hide_render = True
        reference["rimvision_guide"] = True


def create_effect_marker(context, record, root, kind, node=None):
    if kind == "light":
        data = bpy.data.lights.new("RimVision light point", "POINT")
        marker = bpy.data.objects.new("💡 " + tr("Light") + " –  " + record["defName"], data)
    else:
        data = bpy.data.curves.new("RimVision fire marker", "CURVE")
        data.dimensions = "3D"
        data.bevel_depth = 0.008
        profile = [(0, 0), (-0.12, 0.1), (-0.13, 0.23), (-0.06, 0.34), (-0.03, 0.2), (0.05, 0.46), (0.14, 0.22), (0.1, 0.09)]
        for axis in (0, 1):
            spline = data.splines.new("POLY")
            spline.points.add(len(profile) - 1)
            for point, (x, z) in zip(spline.points, profile):
                point.co = (x if axis == 0 else 0, x if axis == 1 else 0, z, 1)
            spline.use_cyclic_u = True
        material = next((m for m in bpy.data.materials if m.get("rimvision_fire_marker")), None)
        if material is None:
            material = bpy.data.materials.new("RimVision fire marker orange")
            material["rimvision_fire_marker"] = True
            material.diffuse_color = (1, 0.2, 0.01, 1)
            material.use_nodes = True
            shader = material.node_tree.nodes.get("Principled BSDF")
            shader.inputs["Base Color"].default_value = material.diffuse_color
            shader.inputs["Emission Color"].default_value = material.diffuse_color
            shader.inputs["Emission Strength"].default_value = 2
        data.materials.append(material)
        marker = bpy.data.objects.new("🔥 " + tr("Fire") + " –  " + record["defName"], data)
        marker.hide_render = True
    context.collection.objects.link(marker)
    marker.parent = root
    marker.show_name = True
    marker.show_in_front = True
    marker["rimvision_role"] = "anchor"
    marker["rimvision_def"] = record["defName"]
    effect = marker.rimvision_effect
    effect.kind = kind
    if node is not None:
        marker["rimvision_anchor_xml"] = ET.tostring(node, encoding="unicode")
        effect.size = float(node.findtext("size", "1"))
        effect.radius = float(node.findtext("radius", "8"))
        effect.intensity = float(node.findtext("intensity", "1"))
        effect.color = tuple(float(c) for c in node.findtext("color", "1,1,1").split(","))
        effect.always = node.findtext("always", "false") == "true"
        marker.location = (float(node.findtext("x", "0")) - record["width"] / 2,
                           float(node.findtext("z", "0")) - record["depth"] / 2, float(node.findtext("height", "0")))
    else:
        corners = [root.matrix_world @ Vector(point) for point in root.bound_box]
        position = Vector(((min(p.x for p in corners) + max(p.x for p in corners)) / 2,
                           (min(p.y for p in corners) + max(p.y for p in corners)) / 2, max(p.z for p in corners) + 0.05))
        marker.location = root.matrix_world.inverted() @ position
    return marker


def import_model(context, record, file, index):
    model = read_model(file, index, record["defName"])
    legacy = any(parent.name == "RimWorld3DVR" and parent.parent.name == "OptionalContent" for parent in Path(file).parents)
    vertices = [(x - record["width"] / 2, z - record["depth"] / 2, height) for x, height, z in model["vertices"]]
    mesh = bpy.data.meshes.new(record["defName"] + " model")
    obj = None
    try:
        mesh.from_pydata(vertices, [], [list(reversed(face["indices"])) for face in model["faces"]])
        mesh.update()
        obj = bpy.data.objects.new(record["defName"], mesh)
        context.collection.objects.link(obj)
        obj["rimvision_def"] = record["defName"]
        obj["rimvision_source"] = str(file)
        obj["rimvision_filter"] = model["filter"]
        obj["rimvision_height_offset"] = model["height_offset"]
        uv_layer = mesh.uv_layers.new(name="UVMap")
        raw_u = mesh.attributes.new("rimvision_pixel_u", "FLOAT", "CORNER")
        raw_v = mesh.attributes.new("rimvision_pixel_v", "FLOAT", "CORNER")
        pending = mesh.attributes.new("rimvision_pending_uv", "BOOLEAN", "FACE")
        uv_layer = mesh.uv_layers.active
        raw_u = mesh.attributes["rimvision_pixel_u"]
        raw_v = mesh.attributes["rimvision_pixel_v"]
        materials = {}
        corner_normals = [(0, 0, 0)] * len(mesh.loops) if any(face["normals"] for face in model["faces"]) else None
        for polygon, face in zip(mesh.polygons, model["faces"]):
            polygon.use_smooth = face["smooth"]
            if face["normals"]:
                for loop, (x, height, z) in zip(polygon.loop_indices, reversed(face["normals"])):
                    corner_normals[loop] = (x, z, height)
            key = (face["texture_path"], face["direction"], face["color"], face["tint"], face["two_sided"], face["turret_top"], face["required_links"], face["forbidden_links"])
            if key not in materials:
                material = bpy.data.materials.new(record["defName"] + " " + ("solid" if face["color"] else face["direction"]))
                name_resource(material, record, "Material")
                surface = material.rimvision
                surface.mode = "Solid" if face["color"] else "Custom" if face["texture_path"] else "Game"
                if face["texture_path"]:
                    surface.texture_path = imported_texture(file, face["texture_path"])
                if face["color"]:
                    surface.color = face["color"]
                for prop in ("direction", "tint", "two_sided", "turret_top", "required_links", "forbidden_links"):
                    setattr(surface, prop, face[prop])
                material_preview(material, record)
                materials[key] = len(mesh.materials)
                mesh.materials.append(material)
            polygon.material_index = materials[key]
            coords, pixels = face_uvs(face, model["vertices"])
            image = surface_image(mesh.materials[polygon.material_index].rimvision, record) if pixels and not face["color"] else None
            uv_size = face["uv_size"] or (tuple(image.size) if image is not None else None)
            if legacy and pixels and not face["color"] and not face["texture_path"] and face["uv_size"] is None:
                original = image_for(record, face["direction"], original=True)
                if original is not None:
                    uv_size = tuple(original.size)
                elif record.get("packageId", "").lower().startswith("ludeon.rimworld"):
                    raise ValueError("Legacy model UVs require original game sprites. Prepare game sprites and load the model again.")
            ready = uv_size is not None and uv_size[0] > 0 and uv_size[1] > 0
            pending.data[polygon.index].value = pixels and not ready and not face["color"]
            for loop, (u, v) in zip(polygon.loop_indices, reversed(coords)):
                if pixels:
                    raw_u.data[loop].value = u
                    raw_v.data[loop].value = v
                    uv_layer.data[loop].uv = (u / uv_size[0], 1 - v / uv_size[1]) if ready else (0, 0)
                else:
                    uv_layer.data[loop].uv = (u, v)
        if corner_normals is not None:
            mesh.normals_split_custom_set(corner_normals)
        if model["cutout"]:
            cut_mesh = bpy.data.meshes.new("RimVision ground cutout")
            cut_mesh.from_pydata([vertices[i] for i in model["cutout"]], [], [list(reversed(range(len(model["cutout"]))))])
            cut = bpy.data.objects.new(record["defName"] + " ground cutout", cut_mesh)
            context.collection.objects.link(cut)
            cut.parent = obj
            cut.display_type = "WIRE"
            cut.hide_render = cut.hide_select = True
            cut["rimvision_role"] = "ground_cutout"
        for xml in model["anchors"]:
            node = ET.fromstring(xml)
            kind = (node.findtext("name") or "").strip()
            if kind in ("fire", "light"):
                create_effect_marker(context, record, obj, kind, node)
                continue
            anchor = bpy.data.objects.new(record["defName"] + " " + (node.findtext("name") or "anchor"), None)
            context.collection.objects.link(anchor)
            anchor.parent = obj
            anchor.empty_display_type = "SPHERE"
            anchor.empty_display_size = 0.08
            anchor.location = (float(node.findtext("x", "0")) - record["width"] / 2,
                               float(node.findtext("z", "0")) - record["depth"] / 2, float(node.findtext("height", "0")))
            anchor["rimvision_role"] = "anchor"
            anchor["rimvision_anchor_xml"] = xml
            anchor.hide_render = True
        for selected in context.selected_objects:
            selected.select_set(False)
        obj.select_set(True)
        context.view_layer.objects.active = obj
        context.scene.rimvision.height_offset = model["height_offset"]
        context.scene.rimvision.graphic_filter = model["filter"]
        show_reference(context.scene, record)
        frame_model(context, record, obj)
        return obj
    except Exception:
        if obj:
            for child in list(obj.children):
                data, kind = child.data, child.type
                bpy.data.objects.remove(child, do_unlink=True)
                if data and data.users == 0 and kind in ("MESH", "CURVE", "LIGHT"):
                    getattr(bpy.data, {"MESH": "meshes", "CURVE": "curves", "LIGHT": "lights"}[kind]).remove(data)
            bpy.data.objects.remove(obj, do_unlink=True)
        if mesh.users == 0:
            bpy.data.meshes.remove(mesh)
        raise


def refresh_model_textures(scene, records):
    definitions = {record["defName"]: record for record in records}
    for obj in scene.objects:
        record = definitions.get(obj.get("rimvision_def"))
        if obj.type != "MESH" or record is None:
            continue
        for material in obj.data.materials:
            if material:
                if material.rimvision.mode == "Object":
                    continue
                if material.rimvision.mode == "Custom" and not Path(bpy.path.abspath(material.rimvision.texture_path)).is_file():
                    continue
                material_preview(material, record)
        mesh = obj.data
        pending = mesh.attributes.get("rimvision_pending_uv")
        raw_u, raw_v = mesh.attributes.get("rimvision_pixel_u"), mesh.attributes.get("rimvision_pixel_v")
        if pending is None or raw_u is None or raw_v is None or mesh.uv_layers.active is None:
            continue
        for polygon in mesh.polygons:
            if not pending.data[polygon.index].value or polygon.material_index >= len(mesh.materials):
                continue
            material = mesh.materials[polygon.material_index]
            if material and material.rimvision.mode == "Object":
                continue
            if material and material.rimvision.mode == "Custom" and not Path(bpy.path.abspath(material.rimvision.texture_path)).is_file():
                continue
            image = surface_image(material.rimvision, record) if material else None
            if image and image.size[0] > 0 and image.size[1] > 0:
                for loop in polygon.loop_indices:
                    mesh.uv_layers.active.data[loop].uv = (raw_u.data[loop].value / image.size[0], 1 - raw_v.data[loop].value / image.size[1])
                pending.data[polygon.index].value = False
