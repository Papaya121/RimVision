import json
import re
from pathlib import Path

import bpy


CONTEXT = "RimVision"
MESSAGES = json.loads((Path(__file__).parent / "translations.json").read_text(encoding="utf-8"))
REVERSE = {value: key for key, value in MESSAGES.items()}
_enum_cache = {}
_patterns = {}


def preferences():
    addon = bpy.context.preferences.addons.get(__package__)
    return addon.preferences if addon else None


def language():
    settings = preferences()
    choice = settings.language_index if settings else 0
    choice = {0: "AUTO", 1: "EN", 2: "RU"}.get(choice, choice)
    if choice != "AUTO":
        return choice
    return "RU" if bpy.app.translations.locale.lower().startswith("ru") else "EN"


def tr(value):
    if not value or not isinstance(value, str):
        return value
    if re.match(r"^(?:[A-Za-z]:[/\\]|[/\\])", value):
        return value
    russian = language() == "RU"
    if value in REVERSE:
        value = REVERSE[value]
    if not russian and value in MESSAGES:
        return value
    mapping = MESSAGES if russian else REVERSE
    if value in mapping:
        return mapping[value]
    key = "RU" if russian else "EN"
    pattern = _patterns.get(key)
    if pattern is None:
        parts = []
        for message in sorted(mapping, key=len, reverse=True):
            parts.append((r"(?<!\w)" if message[0].isalnum() else "") + re.escape(message) + (r"(?!\w)" if message[-1].isalnum() else ""))
        pattern = _patterns[key] = re.compile("|".join(parts))
    return pattern.sub(lambda match: mapping[match.group()], value)


def translated_items(source):
    def items(self, context):
        values = source(self, context) if callable(source) else source
        key = (language(), tuple(values))
        if key not in _enum_cache:
            _enum_cache[key] = [None if value is None else (value[0], tr(value[1]), tr(value[2]), *value[3:],
                                *([index] if len(value) == 3 else [])) for index, value in enumerate(values)]
        return _enum_cache[key]
    return items


def register():
    try:
        bpy.app.translations.unregister(__name__)
    except ValueError:
        pass
    settings = preferences()
    choice = settings.language_index if settings else 0
    automatic = choice in (0, "AUTO")
    english = {(CONTEXT, key): key for key in MESSAGES}
    russian = {(CONTEXT, key): value for key, value in MESSAGES.items()}
    tables = {locale: russian if (locale.lower().startswith("ru") if automatic else language() == "RU") else english
              for locale in bpy.app.translations.locales}
    bpy.app.translations.register(__name__, tables)


def unregister():
    try:
        bpy.app.translations.unregister(__name__)
    except ValueError:
        pass
    _enum_cache.clear()


def language_get(self):
    return self.language_index


def language_set(self, value):
    self.language_index = value


def language_changed(self, context):
    register()
    for scene in getattr(bpy.data, "scenes", ()):
        for obj in scene.objects:
            if obj.type == "FONT" and obj.get("rimvision_guide"):
                definition, separator, caption = obj.data.body.partition("  |  ")
                if separator:
                    obj.data.body = definition + separator + tr(caption)
    if context is not None:
        for window in context.window_manager.windows:
            for area in window.screen.areas:
                area.tag_redraw()


class Layout:
    def __init__(self, layout):
        object.__setattr__(self, "_layout", layout)

    def __setattr__(self, name, value):
        setattr(self._layout, name, value)

    def __getattr__(self, name):
        attribute = getattr(self._layout, name)
        if not callable(attribute):
            return attribute

        def call(*args, **kwargs):
            if name in ("label", "prop", "prop_search", "operator", "popover", "menu"):
                translate = kwargs.get("translate", True)
                if "text" not in kwargs and name in ("prop", "prop_search"):
                    data = args[0].properties if isinstance(args[0], bpy.types.Operator) else args[0]
                    kwargs["text"] = data.bl_rna.properties[args[1]].name
                if "text" not in kwargs and name == "operator":
                    module, operator = args[0].split(".", 1)
                    kwargs["text"] = getattr(getattr(bpy.ops, module), operator).get_rna_type().name
                if "text" in kwargs and translate:
                    kwargs["text"] = tr(kwargs["text"])
                kwargs["translate"] = False
            result = attribute(*args, **kwargs)
            return ui(result) if isinstance(result, bpy.types.UILayout) else result
        return call


def ui(layout):
    return layout if isinstance(layout, Layout) else Layout(layout)
