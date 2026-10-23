import os
import re
from pathlib import Path


def read_vdf(file):
    tokens = re.findall(r'"((?:\\.|[^"\\])*)"|([{}])', Path(file).read_text(encoding="utf-8-sig", errors="replace"))
    stack, current, key = [], {}, None
    root = current
    for quoted, brace in tokens:
        if brace == "{":
            child = {}
            current[key] = child
            stack.append(current)
            current, key = child, None
        elif brace == "}":
            if not stack:
                raise ValueError("Invalid Steam configuration")
            current, key = stack.pop(), None
        elif key is None:
            key = quoted.replace('\\"', '"').replace("\\\\", "\\")
        else:
            current[key] = quoted.replace('\\"', '"').replace("\\\\", "\\")
            key = None
    return root


def find_installations(preferred=""):
    home = Path.home()
    steam = [home / ".steam/steam", home / ".local/share/Steam", home / "Library/Application Support/Steam"]
    if os.name == "nt":
        import winreg
        for hive, key, value in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath"),
                                 (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")):
            try:
                with winreg.OpenKey(hive, key) as handle:
                    steam.append(Path(winreg.QueryValueEx(handle, value)[0]))
            except OSError:
                pass
        for variable in ("ProgramFiles(x86)", "ProgramFiles"):
            if os.environ.get(variable):
                steam.append(Path(os.environ[variable]) / "Steam")
        for letter in "CDEFGHIJKLMNOPQRSTUVWXYZ":
            steam.extend((Path(letter + ":/SteamLibrary"), Path(letter + ":/Steam")))
    libraries = []
    for root in steam:
        if not root.is_dir() or root in libraries:
            continue
        libraries.append(root)
        configuration = root / "steamapps/libraryfolders.vdf"
        if configuration.is_file():
            try:
                document = read_vdf(configuration)
                for value in document.get("libraryfolders", document.get("LibraryFolders", {})).values():
                    path = value.get("path") if isinstance(value, dict) else value
                    if path and Path(path).is_dir() and Path(path) not in libraries:
                        libraries.append(Path(path))
            except (OSError, ValueError):
                pass
    games, workshops = [], []
    if preferred:
        games.append(Path(preferred))
    for library in libraries:
        apps = library / "steamapps"
        manifest = apps / "appmanifest_294100.acf"
        if manifest.is_file():
            try:
                folder = read_vdf(manifest).get("AppState", {}).get("installdir", "RimWorld")
                games.append(apps / "common" / folder)
            except (OSError, ValueError):
                pass
        games.append(apps / "common/RimWorld")
        workshop = apps / "workshop/content/294100"
        if workshop.is_dir() and workshop not in workshops:
            workshops.append(workshop)
    game = next((p.resolve() for p in games if (p / "Data/Core").is_dir()), None)
    if game:
        same_library = game.parent.parent / "workshop/content/294100"
        if same_library.is_dir() and same_library not in workshops:
            workshops.append(same_library)
    configs = [home / "AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml",
               home / ".config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml",
               home / "Library/Application Support/RimWorld/Config/ModsConfig.xml"]
    return game, workshops, next((p for p in configs if p.is_file()), None)
