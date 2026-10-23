import copy
import json
import math
import xml.etree.ElementTree as ET
from collections import ChainMap
from pathlib import Path


def text(node, path, default=""):
    value = node.findtext(path)
    return value.strip() if value else default


def merge(parent, child):
    result = copy.deepcopy(parent)
    result.attrib.update(child.attrib)
    for value in child:
        existing = result.find(value.tag)
        replacement = merge(existing, value) if existing is not None and len(value) and value.attrib.get("Inherit", "True").lower() != "false" else copy.deepcopy(value)
        if existing is not None:
            result.remove(existing)
        result.append(replacement)
    return result


def content_folders(root, version, packages):
    load_file = root / "LoadFolders.xml"
    if load_file.is_file():
        document = ET.parse(load_file).getroot()
        section = document.find("v" + version)
        if section is None:
            section = document.find("default")
        if section is None:
            return []
        folders = []
        for entry in section.findall("li"):
            required = entry.get("IfModActive")
            forbidden = entry.get("IfModNotActive")
            if required and required.lower() not in packages:
                continue
            if forbidden and forbidden.lower() in packages:
                continue
            path = (root / (entry.text or ".").strip()).resolve()
            if path.is_relative_to(root.resolve()) and path.is_dir():
                folders.append(path)
        return folders
    folders = [root]
    versioned = root / version
    if versioned.is_dir():
        folders.append(versioned)
    return folders


def discover(game, workshop):
    roots = []
    workshops = workshop if isinstance(workshop, (list, tuple)) else [workshop]
    for base in (game / "Data", game / "Mods", *workshops):
        base = Path(base) if base else None
        if base and base.is_dir():
            roots.extend(sorted((p for p in base.iterdir() if p.is_dir()), key=lambda p: p.name.lower()))
    mods, seen, warnings = [], set(), []
    for root in roots:
        if root.resolve() in seen:
            continue
        seen.add(root.resolve())
        about = root / "About" / "About.xml"
        if not about.is_file():
            continue
        try:
            node = ET.parse(about).getroot()
            mods.append((root, text(node, "name", root.name), text(node, "packageId", root.name)))
        except (ET.ParseError, OSError) as error:
            warnings.append(str(about) + ": " + str(error))
    return mods, warnings


def active_mods(config):
    if not config or not Path(config).is_file():
        return None
    return [node.text.strip().lower() for node in ET.parse(config).getroot().findall("activeMods/li") if node.text]


def sprite_folders(mods, version, active, include_inactive=False):
    if active is not None and not include_inactive:
        order = {package: index for index, package in enumerate(active)}
        mods = sorted((mod for mod in mods if mod[2].lower() in order), key=lambda mod: order[mod[2].lower()])
    packages = set(active) if active is not None else {package.lower() for _, _, package in mods}
    return list(dict.fromkeys(folder for root, _, _ in mods for folder in content_folders(root, version, packages)))


def scan(game, workshop=None, version="1.6", cached=None, config=None, active_only=False, model_root=None, base_cached=None, supplemental_cached=None):
    mods, warnings = discover(Path(game), workshop)
    if not mods:
        raise ValueError("No content found. Select the RimWorld folder containing Data and Mods.")
    active = active_mods(config)
    if active_only:
        if not active:
            raise ValueError("Active mod list not found. Disable Only active mods or select ModsConfig.xml.")
        mods = [mod for mod in mods if mod[2].lower() in active]
    if active:
        order = {package: index for index, package in enumerate(active)}
        mods.sort(key=lambda mod: order.get(mod[2].lower(), len(order)))
    packages = set(active) if active is not None else {package.lower() for _, _, package in mods}
    templates, definitions, folders, pawn_kinds, body_types = {}, [], [], [], []
    inactive_folders = {}
    for root, name, package in mods:
        try:
            loaded = content_folders(root, version, packages)
            if active is None or package.lower() in active:
                folders.extend(loaded)
            else:
                inactive_folders[package] = loaded
            for folder in loaded:
                if (folder / "Patches").is_dir():
                    warnings.append(name + ": XML patches are not applied")
                for file in sorted((folder / "Defs").rglob("*.xml")):
                    try:
                        for node in ET.parse(file).getroot():
                            if node.get("Name"):
                                templates[node.get("Name")] = node
                            if node.tag == "ThingDef" and text(node, "defName") and node.get("Abstract", "false").lower() != "true":
                                definitions.append((node, name, package))
                            elif node.tag == "PawnKindDef" and text(node, "defName") and node.get("Abstract", "false").lower() != "true":
                                pawn_kinds.append((node, package))
                            elif node.tag == "BodyTypeDef" and text(node, "defName") and (active is None or package.lower() in active):
                                body_types.append(node)
                    except (ET.ParseError, OSError) as error:
                        warnings.append(str(file) + ": " + str(error))
        except (ET.ParseError, OSError) as error:
            warnings.append(str(root) + ": " + str(error))

    textures, variants = dict(cached or {}), {}
    originals, original_variants = dict(base_cached or {}), {}
    for key, path in sorted(originals.items()):
        if "/" in key:
            original_variants.setdefault(key.rsplit("/", 1)[0], path)
    for key, path in sorted(textures.items()):
        if "/" in key:
            variants.setdefault(key.rsplit("/", 1)[0], (None, path))
    for folder in folders:
        base = folder / "Textures"
        for file in sorted(base.rglob("*.png")):
            key = file.relative_to(base).with_suffix("").as_posix().casefold()
            textures[key] = str(file.resolve())
            parent = file.relative_to(base).parent.as_posix().casefold()
            if parent not in variants or variants[parent][0] != folder:
                variants[parent] = (folder, str(file.resolve()))

    inactive_textures = {}
    for package, loaded in inactive_folders.items():
        own_textures = {}
        for folder in loaded:
            own_textures.update((supplemental_cached or {}).get(str(folder.resolve()).casefold(), {}))
            base = folder / "Textures"
            for file in sorted(base.rglob("*.png")):
                own_textures[file.relative_to(base).with_suffix("").as_posix().casefold()] = str(file.resolve())
        own_variants = {}
        for key, value in sorted(own_textures.items()):
            if "/" in key:
                own_variants.setdefault(key.rsplit("/", 1)[0], value)
        inactive_textures[package] = (
            ChainMap(own_textures, textures),
            ChainMap({key: (None, value) for key, value in own_variants.items()}, variants),
            ChainMap(own_textures, originals),
            ChainMap(own_variants, original_variants))

    def resolve(node, visiting=()):
        parent = node.get("ParentName")
        if not parent:
            return copy.deepcopy(node)
        if parent in visiting or parent not in templates:
            raise ValueError("Unresolved or cyclic parent: " + parent)
        return merge(resolve(templates[parent], visiting + (parent,)), node)

    pawn_graphics = {}
    for node, package in pawn_kinds:
        try:
            kind = resolve(node)
            race = text(kind, "race")
            stages = kind.findall("lifeStages/li")
            path = next((text(stage, "bodyGraphicData/texPath") or text(stage, "femaleGraphicData/texPath") for stage in reversed(stages)
                         if text(stage, "bodyGraphicData/texPath") or text(stage, "femaleGraphicData/texPath")), "")
            if race and path:
                pawn_graphics.setdefault(race, []).append((package, path))
        except ValueError as error:
            warnings.append(text(node, "defName") + ": " + str(error))
    human_body_path = ""
    for node in body_types:
        if text(node, "defName") == "Male":
            try:
                body_type = resolve(node)
                human_body_path = text(body_type, "bodyNakedGraphicPath") or text(body_type, "nakedGraphicPath")
            except ValueError as error:
                warnings.append("BodyTypeDef Male: " + str(error))

    model_sources = [(root, mod) for root, mod, _ in mods]
    if model_root:
        root = Path(model_root).resolve()
        if not any((root / "OptionalContent" / namespace / "Models").is_dir() for namespace in ("RimWorld3DVR", "RimVision")):
            raise ValueError("Select a mod folder containing OptionalContent/RimVision/Models or OptionalContent/RimWorld3DVR/Models")
        model_sources = [(path, mod) for path, mod in model_sources if path.resolve() != root]
        about = root / "About/About.xml"
        mod = text(ET.parse(about).getroot(), "name", root.name) if about.is_file() else root.name
        model_sources.append((root, mod))
    models = {}
    for root, mod in model_sources:
        for namespace in ("RimWorld3DVR", "RimVision"):
            for file in sorted((root / "OptionalContent" / namespace / "Models").rglob("*.xml")):
                try:
                    for index, node in enumerate(ET.parse(file).getroot()):
                        definition = text(node, "thingDef")
                        if definition:
                            models.setdefault(definition, []).append(dict(file=str(file.resolve()), index=index,
                                filter=text(node, "graphicPathContains"), mod=mod, legacy=namespace == "RimWorld3DVR"))
                except (OSError, ET.ParseError) as error:
                    warnings.append(str(file) + ": " + str(error))
    records = {}
    for node, mod, package in definitions:
        try:
            resolved = resolve(node)
            size = text(resolved, "size")
            if size:
                parts = size.strip("()").split(",")
                if len(parts) != 2:
                    raise ValueError("Invalid footprint")
                width, depth = (int(part.strip()) for part in parts)
            else:
                width = int(text(resolved, "size/x", "1"))
                depth = int(text(resolved, "size/z", "1"))
            if width < 1 or depth < 1:
                raise ValueError("Invalid footprint")
            record = dict(defName=text(resolved, "defName"), label=text(resolved, "label", text(resolved, "defName")),
                          mod=mod, packageId=package, width=width, depth=depth,
                          category=text(resolved, "category"), texturePath=text(resolved, "graphicData/texPath"),
                          animal=text(resolved, "category") == "Pawn" and resolved.find("race") is not None and text(resolved, "race/intelligence", "Animal") == "Animal",
                          door="Building_Door" in text(resolved, "thingClass"),
                          models=models.get(text(resolved, "defName"), []))
            if record["category"] == "Plant":
                draw = text(resolved, "graphicData/drawSize", "1").strip("()").split(",")
                draw_x, draw_y = float(draw[0]), float(draw[-1])
                size_range = text(resolved, "plant/visualSizeRange")
                maximum = float(size_range.split("~")[-1]) if size_range else float(text(resolved, "plant/visualSizeRange/max", "1"))
                if any(not math.isfinite(value) or value <= 0 for value in (draw_x, draw_y, maximum)):
                    raise ValueError("Invalid plant reference size")
                adult_scale = max(.001, draw_x * maximum)
                tree = text(resolved, "plant/treeCategory", "None") != "None"
                grass = "grass" in record["defName"].lower()
                if tree:
                    reference_width = max(1.2, min(draw_x * 2.3, 6))
                    reference_height = max(2, min(draw_y * 3.5, 9))
                else:
                    plant_scale = 1.2 if grass else 1.6
                    reference_width = max(.12, min(draw_x * (.45 if grass else .8), 1.4)) * plant_scale
                    reference_height = max(.08, min(draw_y * (.16 if grass else .4), .32 if grass else .85)) * plant_scale
                    if not grass:
                        reference_height *= .9
                        reference_width = reference_height * draw_x / max(draw_y, .001)
                record["referenceWidth"] = reference_width / adult_scale
                record["referenceHeight"] = reference_height / adult_scale
            if record["category"] == "Pawn" and not record["texturePath"]:
                candidates = pawn_graphics.get(record["defName"], [])
                matching = [entry for entry in candidates if entry[0].lower() == package.lower()]
                enabled = [entry for entry in candidates if active is None or entry[0].lower() in active]
                choices = matching or enabled or candidates
                if choices:
                    record["texturePath"] = choices[-1][1]
            builtin, locked = "", False
            if record["door"]:
                builtin, locked = "Door", True
            elif record["defName"] == "Vent":
                builtin, locked = "Vent", True
            elif record["defName"] == "TorchWallLamp":
                builtin = "Wall torch"
            elif text(resolved, "building/isNaturalRock").lower() == "true":
                builtin = "Rock"
            elif "/chunk/" in record["texturePath"].replace("\\", "/").lower():
                builtin = "Stone chunk"
            elif record["defName"] == "Wall" or (record["category"] == "Building" and
                text(resolved, "graphicData/linkType", "None") != "None" and
                (float(text(resolved, "fillPercent", "0")) >= 1 or text(resolved, "mineable").lower() == "true")):
                builtin = "Wall / linked building"
            record["builtin3d"] = builtin
            record["builtin3dLocked"] = locked
            template = Path(__file__).parent / "models" / (record["defName"] + ".xml")
            if record["defName"] == "TorchWallLamp" and not record["models"] and template.is_file():
                record["models"] = [dict(file=str(template.resolve()), index=0, filter="", mod="RimVision · template", legacy=False)]
            record["inactiveContent"] = active is not None and package.lower() not in active
            record_textures, record_variants = textures, variants
            record_originals, record_original_variants = originals, original_variants
            if package in inactive_textures:
                record_textures, record_variants, record_originals, record_original_variants = inactive_textures[package]
            record["directionalSprites"] = {}
            for direction in ("North", "East", "South", "West"):
                sprite = ""
                path = record["texturePath"].replace("\\", "/").casefold()
                if path:
                    directional = record_textures.get(path + "_" + direction.lower(), "")
                    if directional:
                        record["directionalSprites"][direction] = directional
                    sprite = record_textures.get(path + "_" + direction.lower(), "") or record_textures.get(path, "")
                    if not sprite:
                        sprite = record_textures.get(path + "_south", "") or record_variants.get(path, (None, ""))[1]
                record["sprite" + direction] = sprite
                original = record_originals.get(path + "_" + direction.lower(), "") or record_originals.get(path, "")
                if not original:
                    original = record_originals.get(path + "_south", "") or record_original_variants.get(path, "")
                record["baseSprite" + direction] = original
            icon_path = text(resolved, "uiIconPath").replace("\\", "/").casefold()
            record["previewSprite"] = record_textures.get(icon_path, "") if icon_path else ""
            if record["category"] == "Pawn" and not record["previewSprite"]:
                record["previewSprite"] = record["spriteSouth"] or record["spriteNorth"]
                if not record["previewSprite"] and text(resolved, "race/intelligence") == "Humanlike" and human_body_path:
                    body_path = human_body_path.replace("\\", "/").casefold()
                    record["previewSprite"] = record_textures.get(body_path + "_south", "") or record_textures.get(body_path, "")
                    if record["previewSprite"]:
                        record["previewNote"] = "Base body; the game assembles the portrait"
            records[(package, record["defName"])] = record
        except (ValueError, OSError) as error:
            warnings.append(text(node, "defName") + ": " + str(error))
    return sorted(records.values(), key=lambda r: (r["mod"].lower(), r["defName"].lower())), warnings


def load_snapshot(file):
    file = Path(file)
    data = json.loads(file.read_text(encoding="utf-8-sig"))
    if not isinstance(data, dict) or data.get("schemaVersion") != 1:
        raise ValueError("Unsupported catalog version")
    records = data["things"]
    if not isinstance(records, list):
        raise ValueError("Invalid catalog entries")
    for record in records:
        if not isinstance(record, dict) or not isinstance(record.get("defName"), str):
            raise ValueError("Invalid catalog entry")
        if not record["defName"] or not isinstance(record["width"], int) or not isinstance(record["depth"], int) or record["width"] < 1 or record["depth"] < 1:
            raise ValueError("Invalid catalog entry")
        for key in ("label", "mod", "packageId", "category", "texturePath"):
            if not isinstance(record.get(key, ""), str):
                raise ValueError("Invalid catalog text field")
        if not isinstance(record.get("door", False), bool):
            raise ValueError("Invalid door flag")
        for key in ("referenceWidth", "referenceHeight"):
            if key in record and (not isinstance(record[key], (int, float)) or not math.isfinite(record[key]) or record[key] <= 0):
                raise ValueError("Invalid reference size")
        if not isinstance(record.get("animal", False), bool):
            raise ValueError("Invalid animal flag")
        for direction in ("North", "East", "South", "West"):
            key = "sprite" + direction
            if record.get(key):
                if not isinstance(record[key], str):
                    raise ValueError("Invalid sprite path")
                record[key] = str((file.parent / record[key]).resolve())
        if record.get("previewSprite"):
            if not isinstance(record["previewSprite"], str):
                raise ValueError("Invalid preview sprite path")
            record["previewSprite"] = str((file.parent / record["previewSprite"]).resolve())
        if "directionalSprites" in record:
            directional = record["directionalSprites"]
            if not isinstance(directional, dict):
                raise ValueError("Invalid directional sprites")
            for direction, path in directional.items():
                if direction not in ("North", "East", "South", "West") or not isinstance(path, str):
                    raise ValueError("Invalid directional sprite path")
                if path:
                    directional[direction] = str((file.parent / path).resolve())
    return records
