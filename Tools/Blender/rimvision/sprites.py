import hashlib
import json
import platform
import sys
import zipfile
from pathlib import Path


def extractor(cache):
    try:
        import UnityPy
        from UnityPy.helpers import TypeTreeHelper
        TypeTreeHelper.read_typetree_boost = None
        return UnityPy
    except ImportError:
        pass
    if platform.system() != "Windows" or platform.machine().lower() not in ("amd64", "x86_64") or sys.maxsize <= 2**32:
        raise ValueError("Bundled sprite extractor requires Windows x64. Other platforms require UnityPy 1.20.26 in Blender's Python.")
    wheels = Path(__file__).parent / "wheels"
    if not wheels.is_dir():
        raise ValueError("Sprite extractor is missing; install the complete RimVision-Blender.zip")
    runtime = "cp" + str(sys.version_info.major) + str(sys.version_info.minor)
    selected, distributions = {}, set()
    for wheel in sorted(wheels.glob("*.whl")):
        distribution, version, python_tag, abi, target = wheel.stem.rsplit("-", 4)
        distributions.add(distribution)
        score = 0
        if target == "win_amd64" and python_tag == runtime and abi == runtime:
            score = 3
        elif target == "win_amd64" and abi == "abi3" and python_tag.startswith("cp") and python_tag[2:].isdigit():
            minimum = (int(python_tag[2]), int(python_tag[3:]))
            if minimum <= sys.version_info[:2]:
                score = 2
        elif target == "any" and abi == "none" and "py3" in python_tag.split("."):
            score = 1
        if score and (distribution not in selected or score > selected[distribution][0]):
            selected[distribution] = (score, wheel)
    missing = distributions - selected.keys()
    if not distributions or missing:
        raise ValueError("No bundled sprite extractor for Python " + str(sys.version_info.major) + "." + str(sys.version_info.minor) + "; install UnityPy 1.20.26 in Blender's Python or use the complete addon with Python 3.11 or 3.13.")
    compatible = [entry[1] for entry in selected.values()]
    fingerprint = hashlib.sha256("\n".join(wheel.name for wheel in compatible).encode("utf-8")).hexdigest()[:16]
    destination = Path(cache) / ("extractor-1.20.26-" + runtime + "-" + fingerprint)
    marker = destination / ".ready"
    if not marker.is_file():
        destination.mkdir(parents=True, exist_ok=True)
        for wheel in compatible:
            with zipfile.ZipFile(wheel) as archive:
                archive.extractall(destination)
        marker.write_text("1.20.26", encoding="utf-8")
    sys.path.insert(0, str(destination))
    import UnityPy
    from UnityPy.helpers import TypeTreeHelper
    TypeTreeHelper.read_typetree_boost = None
    return UnityPy


def cache_folder(cache, game, roots):
    identity = "\n".join(["active-sprites-v2", str(Path(game).resolve()).casefold(), *(str(Path(root).resolve()).casefold() for root in roots)])
    key = hashlib.sha256(identity.encode("utf-8")).hexdigest()[:16]
    return Path(cache) / "sprites" / key


def cached_textures(cache, game, roots, base=False, supplemental=False):
    file = cache_folder(cache, game, roots) / ("supplemental_index.json" if supplemental else "base_index.json" if base else "index.json")
    if not file.is_file():
        return {}
    try:
        entries = json.loads(file.read_text(encoding="utf-8"))
        if supplemental:
            return {root: {key: path for key, path in textures.items() if Path(path).is_file()} for root, textures in entries.items()}
        return {key: path for key, path in entries.items() if Path(path).is_file()}
    except (OSError, ValueError):
        return {}


def resource_key(path):
    value = path.replace("\\", "/").lower()
    if "textures/" not in value:
        return ""
    value = value.split("textures/", 1)[1]
    if value.endswith((".png", ".tga", ".psd", ".jpg")):
        value = value.rsplit(".", 1)[0]
    return value


def extract(cache, game, roots, report, cancelled, extra_roots=()):
    UnityPy = extractor(cache)
    output = cache_folder(cache, game, roots)
    output.mkdir(parents=True, exist_ok=True)
    sources = []
    for directory in (Path(game) / "RimWorldWin64_Data", Path(game) / "RimWorldLinux_Data", Path(game) / "RimWorldMac.app/Contents/Resources/Data"):
        if directory.is_dir():
            manager = directory / "globalgamemanagers"
            if manager.is_file():
                sources.append(manager)
            sources.extend(directory.glob("*.assets"))
    base_sources = set(sources)
    sources = [(source, "") for source in sources]
    for root in (*roots, *extra_roots):
        owner = str(Path(root).resolve()).casefold() if root in extra_roots else ""
        for folder in (Path(root) / "AssetBundles", Path(root) / "Bundles"):
            if folder.is_dir():
                for file in folder.rglob("*"):
                    if file.is_file() and file.suffix.lower() not in (".meta", ".manifest"):
                        sources.append((file, owner))
    index, base_index, supplemental_index, errors = {}, {}, {}, []
    for source, owner in dict.fromkeys(sources):
        if cancelled.is_set():
            break
        report("Reading " + source.name)
        try:
            environment = UnityPy.load(str(source))
            entries = list(environment.container.items())
            for reader in environment.objects:
                if reader.type.name == "ResourceManager":
                    for path, pointer in reader.read().m_Container:
                        try:
                            entries.append((path, pointer.deref()))
                        except Exception as error:
                            errors.append(path + ": " + str(error))
            for path, reader in entries:
                if cancelled.is_set():
                    break
                key = resource_key(path)
                if not key or reader.type.name not in ("Texture2D", "Sprite"):
                    continue
                if any(part in ("..", "") for part in key.split("/")):
                    continue
                destination = output / (hashlib.sha256((str(source) + ":" + key).encode("utf-8")).hexdigest() + ".png")
                try:
                    if not destination.is_file() or destination.stat().st_mtime < source.stat().st_mtime:
                        reader.read().image.save(destination)
                    target = supplemental_index.setdefault(owner, {}) if owner else index
                    target[key] = str(destination)
                    if not owner and (source in base_sources or source.resolve().is_relative_to((Path(game) / "Data").resolve())):
                        base_index[key] = str(destination)
                except Exception as error:
                    errors.append(key + ": " + str(error))
            del environment
        except Exception as error:
            errors.append(str(source) + ": " + str(error))
    if cancelled.is_set():
        return cached_textures(cache, game, roots), errors
    for name, entries in (("index", index), ("base_index", base_index), ("supplemental_index", supplemental_index)):
        temporary = output / (name + ".tmp")
        temporary.write_text(json.dumps(entries, ensure_ascii=False), encoding="utf-8")
        temporary.replace(output / (name + ".json"))
    if not index:
        raise ValueError("No texture paths extracted from game resources. " + (errors[0] if errors else "Check the selected game directory."))
    return index, errors
