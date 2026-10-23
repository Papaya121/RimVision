import ast
import argparse
import hashlib
import html
import json
import tomllib
import zipfile
from pathlib import Path


root = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument("--extension", action="store_true")
parser.add_argument("--output-dir", type=Path)
parser.add_argument("--repository-url", default="https://papaya121.github.io/RimVision-Blender/index.json")
args = parser.parse_args()
sources = sorted((root / "rimvision").glob("*.py"))
for file in sources:
    ast.parse(file.read_text(encoding="utf-8"), filename=str(file))
manifest = tomllib.loads((root / "blender_manifest.toml").read_text(encoding="utf-8"))
module = ast.parse((root / "rimvision/__init__.py").read_text(encoding="utf-8"))
info = next(ast.literal_eval(node.value) for node in module.body if isinstance(node, ast.Assign) and any(isinstance(target, ast.Name) and target.id == "bl_info" for target in node.targets))
if manifest["version"] != ".".join(str(value) for value in info["version"]):
    raise ValueError("Manifest and bl_info versions must match")
output = args.output_dir or (root / "dist" if args.extension else root)
output.mkdir(parents=True, exist_ok=True)
package = output / (manifest["id"] + "-" + manifest["version"] + ".zip" if args.extension else "RimVision-Blender.zip")
prefix = "" if args.extension else "rimvision/"
def write_file(archive, file, name):
    entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
    entry.compress_type = zipfile.ZIP_DEFLATED
    entry.external_attr = 0o100644 << 16
    data = file.read_bytes()
    if file.suffix in (".py", ".md", ".toml", ".json") or file.name == "LICENSE":
        data = data.replace(b"\r\n", b"\n")
    archive.writestr(entry, data)


with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as archive:
    for file in sources:
        write_file(archive, file, prefix + file.name)
    write_file(archive, root / "rimvision/translations.json", prefix + "translations.json")
    for file in sorted((root / "wheels").glob("*.whl")):
        write_file(archive, file, prefix + "wheels/" + file.name)
    for file in sorted((root / "rimvision/models").glob("*.xml")):
        write_file(archive, file, prefix + "models/" + file.name)
    for name in ("README.md", "THIRD_PARTY.md", "LICENSE"):
        write_file(archive, root / name, prefix + name)
    if args.extension:
        write_file(archive, root / "blender_manifest.toml", "blender_manifest.toml")
if args.extension:
    entry = {**manifest, "archive_url": "./" + package.name,
             "archive_size": package.stat().st_size,
             "archive_hash": "sha256:" + hashlib.sha256(package.read_bytes()).hexdigest()}
    (output / "index.json").write_text(json.dumps({"version": "v1", "blocklist": [], "data": [entry]}, ensure_ascii=False, indent=2), encoding="utf-8")
    address = html.escape(args.repository_url)
    page = f'''<!doctype html>
<html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>RimVision Model Tools</title>
<style>body{{font:18px system-ui;max-width:800px;margin:60px auto;padding:0 24px;background:#171b22;color:#eee;line-height:1.6}}a{{color:#8ecbff}}code{{overflow-wrap:anywhere}}li{{margin:12px 0}}</style>
<h1>RimVision Model Tools</h1><p>Версия {manifest["version"]}. Модели RimWorld прямо в Blender 4.2+.</p>
<ol><li>Откройте Blender → Preferences → Get Extensions → Repositories → Add Remote Repository.</li>
<li>Вставьте адрес репозитория: <code>{address}</code>.</li>
<li>Найдите RimVision Model Tools и нажмите Install. Разрешите доступ к файлам.</li>
<li>В настройках репозитория включите Check for Updates on Startup. Новые версии устанавливаются кнопкой Update.</li></ol>
<p>Если установлен старый обычный аддон rimvision, сначала отключите его, чтобы не регистрировать плагин дважды. Сохраните файл .blend перед переходом.</p>
<p>Извлечение игровых спрайтов из комплекта поддерживает Windows x64 и Python 3.11/3.13. На других платформах нужна UnityPy 1.20.26 в Python Blender.</p>
<p><a href="{html.escape(package.name)}">Скачать ZIP расширения</a> · <a href="https://github.com/Papaya121/RimVision-Blender">Исходный код и инструкции</a></p></html>'''
    (output / "index.html").write_text(page, encoding="utf-8")
    (output / ".nojekyll").touch()
print(package)
