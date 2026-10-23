import re
import tempfile
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path


def write_xml(root, path):
    ET.indent(root, space="  ")
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=path.name + ".", suffix=".tmp", delete=False) as output:
        temporary = Path(output.name)
    try:
        ET.ElementTree(root).write(temporary, encoding="utf-8", xml_declaration=True)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def create(parent, name, version):
    name = name.strip()
    if not name:
        raise ValueError("Enter an addon name")
    folder = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name).strip(" .")
    if not folder or folder.split(".")[0].upper() in {"CON", "PRN", "AUX", "NUL", *["COM" + str(i) for i in range(1, 10)], *["LPT" + str(i) for i in range(1, 10)]}:
        raise ValueError("The name cannot be used for a folder")
    root = Path(parent) / folder
    root.mkdir(parents=True, exist_ok=False)
    (root / "About").mkdir()
    (root / "OptionalContent/RimVision/Models").mkdir(parents=True)
    about = ET.Element("ModMetaData")
    for tag, value in (("name", name), ("author", "Model author"), ("packageId", "rimvision.models." + uuid.uuid4().hex),
                       ("description", "Models for RimVision")):
        ET.SubElement(about, tag).text = value
    ET.SubElement(ET.SubElement(about, "supportedVersions"), "li").text = version
    dependency = ET.SubElement(ET.SubElement(about, "modDependencies"), "li")
    ET.SubElement(dependency, "packageId").text = "papaya.rimvision"
    ET.SubElement(dependency, "displayName").text = "RimVision"
    ET.SubElement(ET.SubElement(about, "loadAfter"), "li").text = "papaya.rimvision"
    write_xml(about, root / "About/About.xml")
    ensure_content(root)
    return root


def read(root):
    root = Path(root).resolve()
    about = root / "About/About.xml"
    if not about.is_file():
        raise ValueError("Select the addon root containing About/About.xml")
    name = ET.parse(about).getroot().findtext("name", root.name)
    models = []
    for namespace in ("RimWorld3DVR", "RimVision"):
        for file in sorted((root / "OptionalContent" / namespace / "Models").rglob("*.xml")):
            for index, node in enumerate(ET.parse(file).getroot()):
                definition = node.findtext("thingDef", "").strip()
                if definition:
                    models.append(dict(definition=definition, file=str(file), index=index,
                                       filter=node.findtext("graphicPathContains", "").strip()))
    return name, models


def find_addons(roots):
    found, warnings, seen = [], [], set()
    for candidate in roots:
        root = Path(candidate).resolve()
        if root in seen:
            continue
        seen.add(root)
        if not (root / "About/About.xml").is_file() or not any((root / "OptionalContent" / namespace / "Models").is_dir() for namespace in ("RimVision", "RimWorld3DVR")):
            continue
        try:
            about = ET.parse(root / "About/About.xml").getroot()
            package = about.findtext("packageId", "").strip()
            if not package:
                raise ValueError("About.xml is missing packageId")
            if package.lower() == "papaya.rimvision":
                continue
            name, models = read(root)
            found.append(dict(name=name.strip() or root.name, path=str(root), package=package, count=len(models)))
        except (OSError, ValueError, ET.ParseError) as error:
            warnings.append(str(root) + ": " + str(error))
    return sorted(found, key=lambda entry: (entry["name"].casefold(), entry["path"].casefold())), warnings


def ensure_content(root):
    root = Path(root).resolve()
    name, models = read(root)
    about = ET.parse(root / "About/About.xml").getroot()
    package = about.findtext("packageId", "").strip()
    if not package:
        raise ValueError("About.xml is missing packageId")
    namespace = re.sub(r"[^A-Za-z0-9_.-]", "_", package)
    path = root / "Strings/RimVision" / namespace / "Models.txt"
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = [name, "RimVision model addon", "Models are loaded by RimVision from OptionalContent.", ""]
    for model in models:
        lines.append(model["definition"] + (" | " + model["filter"] if model["filter"] else "") + " | " + Path(model["file"]).relative_to(root).as_posix())
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, prefix=path.name + ".", suffix=".tmp", delete=False) as output:
        temporary = Path(output.name)
        output.write("\n".join(lines) + "\n")
    try:
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def add_dependency(root, package, name):
    if not package or package.lower() == "ludeon.rimworld":
        return
    path = Path(root) / "About/About.xml"
    about = ET.parse(path).getroot()
    if package.lower() == about.findtext("packageId", "").strip().lower():
        return
    changed = False
    dependencies = about.find("modDependencies")
    if dependencies is None:
        dependencies = ET.SubElement(about, "modDependencies")
    if package.lower() not in {entry.findtext("packageId", "").strip().lower() for entry in dependencies}:
        entry = ET.SubElement(dependencies, "li")
        ET.SubElement(entry, "packageId").text = package
        ET.SubElement(entry, "displayName").text = name or package
        changed = True
    order = about.find("loadAfter")
    if order is None:
        order = ET.SubElement(about, "loadAfter")
    if package.lower() not in {(entry.text or "").strip().lower() for entry in order}:
        ET.SubElement(order, "li").text = package
        changed = True
    if changed:
        write_xml(about, path)


def update_metadata(root, name, description):
    name = name.strip()
    if not name:
        raise ValueError("Enter an addon name")
    path = Path(root) / "About/About.xml"
    about = ET.parse(path).getroot()
    for tag, value in (("name", name), ("description", description)):
        node = about.find(tag)
        if node is None:
            node = ET.SubElement(about, tag)
        node.text = value
    write_xml(about, path)
