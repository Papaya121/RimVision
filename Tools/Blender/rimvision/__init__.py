bl_info = {"name": "RimVision Model Tools", "author": "RimVision", "version": (0, 4, 35),
           "blender": (4, 2, 0), "location": "View3D > Sidebar > RimVision", "category": "Import-Export"}

import importlib
import math
import queue
import re
import threading
import tempfile
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import bpy
import bpy.utils.previews
from bpy.props import BoolProperty, CollectionProperty, EnumProperty, FloatProperty, FloatVectorProperty, IntProperty, PointerProperty, StringProperty
from bpy_extras.io_utils import ImportHelper
for _module_name in ("i18n", "addon", "catalog", "paths", "model", "sprites", "scene_tools"):
    _module = sys.modules.get(__name__ + "." + _module_name)
    if _module is not None:
        importlib.reload(_module)
from . import i18n
from .i18n import CONTEXT, language_changed, language_get, language_set, preferences, tr, translated_items, ui
from .catalog import active_mods, discover, load_snapshot, scan, sprite_folders
from .paths import find_installations
from .scene_tools import create_effect_marker, frame_model, image_for, import_model, material_preview, name_material, name_resource, object_surface, prepare_texture, refresh_model_textures, show_catalog_models, show_reference, sprite_path
from .sprites import cached_textures, extract
from .addon import add_dependency, create as create_addon, ensure_content, find_addons, read as read_addon, update_metadata, write_xml

_records = []
_previews = None
_mod_items = [("All", "All mods", "")]
_model_items = {}
_empty_model_items = [("None", "No models found", "")]
_file_model_items = [("0", "Default model", "")]
_extracting = None
_loading = False
_restoring_scene = False
_known_meshes = {}
_pending_meshes = []
_directions = [(value, value, "") for value in ("Thing", "North", "East", "South", "West")]
_section_properties = ("setup_options", "addon_options", "addon_models_options", "catalog_options", "model_options",
                       "surface_options", "effect_options", "reference_options", "export_options")


def section_changed(prop):
    def update(self, context):
        if getattr(self, prop):
            for other in _section_properties:
                if other != prop and getattr(self, other):
                    setattr(self, other, False)
    return update


def selection_record(settings):
    if 0 <= settings.index < len(settings.entries):
        index = settings.entries[settings.index].record_index
        if 0 <= index < len(_records):
            return _records[index]
    return None


def selected(context):
    return selection_record(context.scene.rimvision)


def model_root(obj):
    while obj.parent and obj.parent.get("rimvision_def") == obj.get("rimvision_def"):
        obj = obj.parent
    return obj


@bpy.app.handlers.persistent
def reset_mesh_tracking(*args):
    _pending_meshes.clear()
    _known_meshes.clear()
    for scene in bpy.data.scenes:
        _known_meshes[scene.as_pointer()] = (len(scene.objects), {obj.as_pointer() for obj in scene.objects if obj.type == "MESH"})


@bpy.app.handlers.persistent
def begin_scene_restore(*args):
    global _restoring_scene
    _restoring_scene = True
    _pending_meshes.clear()


@bpy.app.handlers.persistent
def finish_scene_restore(*args):
    global _restoring_scene
    try:
        reset_mesh_tracking()
    finally:
        _restoring_scene = False


@bpy.app.handlers.persistent
def track_new_meshes(scene, depsgraph):
    if _restoring_scene:
        return
    previous = _known_meshes.get(scene.as_pointer())
    count = len(scene.objects)
    if previous is not None and previous[0] == count:
        return
    meshes = {obj.as_pointer(): obj for obj in scene.objects if obj.type == "MESH"}
    previous = previous[1] if previous is not None else set(meshes)
    _known_meshes[scene.as_pointer()] = (count, set(meshes))
    if _loading or scene != bpy.context.scene:
        return
    settings = scene.rimvision
    record = selection_record(settings)
    root = settings.current_model
    active = bpy.context.view_layer.objects.active
    if active and active.get("rimvision_def") and not active.get("rimvision_role"):
        root = model_root(active)
    if not settings.auto_attach_meshes or record is None or root is None or root.get("rimvision_def") != record["defName"]:
        return
    for pointer in meshes.keys() - previous:
        obj = meshes[pointer]
        if not obj.get("rimvision_def") and not obj.get("rimvision_role"):
            _pending_meshes.append((scene, obj, root))
    if _pending_meshes and not bpy.app.timers.is_registered(attach_new_meshes):
        bpy.app.timers.register(attach_new_meshes, first_interval=0.05)


def attach_new_meshes():
    if _restoring_scene:
        return 0.1 if _pending_meshes else None
    if bpy.context.mode != "OBJECT":
        return 0.1 if _pending_meshes else None
    pending = list(_pending_meshes)
    _pending_meshes.clear()
    for scene, obj, root in pending:
        try:
            if scene != bpy.context.scene or not scene.rimvision.auto_attach_meshes or obj.get("rimvision_def") or obj.get("rimvision_role"):
                continue
            if scene.objects.get(obj.name) is None or scene.objects.get(root.name) is None:
                continue
            record = selected(bpy.context)
            if record is None or root.get("rimvision_def") != record["defName"] or obj.parent is not None:
                continue
            world = obj.matrix_world.copy()
            obj.parent = root
            obj.matrix_world = world
            for key in ("rimvision_def", "rimvision_filter", "rimvision_height_offset", "rimvision_source"):
                if key in root:
                    obj[key] = root[key]
            if not len(obj.data.materials):
                material = bpy.data.materials.new(obj.name + " surface")
                name_resource(material, record, "Material")
                material.rimvision.use_uv = False
                obj.data.materials.append(material)
            attach_to_addon(bpy.context, obj)
            scene.rimvision.status = "Added to model: " + root.name
        except (ReferenceError, RuntimeError, ValueError) as error:
            print("[RimVision] Mesh attachment: " + str(error))
    return None


def refresh(self, context):
    if _restoring_scene:
        return
    self.entries.clear()
    needle = self.search.casefold()
    content_state, model_status = self.content_state, self.model_status
    for index, record in enumerate(_records):
        if needle not in " ".join(str(record.get(key, "")) for key in ("defName", "label", "mod", "packageId")).casefold():
            continue
        if self.category != "All" and record.get("category") != self.category:
            continue
        if self.mod_filter != "All" and record.get("packageId") != self.mod_filter:
            continue
        inactive = record.get("inactiveContent", False)
        if content_state == "Enabled" and inactive or content_state == "Disabled" and not inactive:
            continue
        ready = bool(record.get("models"))
        builtin = bool(record.get("builtin3d")) or record["defName"] == "Wall"
        locked = bool(record.get("builtin3dLocked") or record.get("door"))
        editable = not locked and (record.get("category") in ("Building", "Item", "Plant") or record.get("animal", False))
        matches = (model_status == "All" or model_status == "Ready" and ready or model_status == "Missing" and not ready or
                   model_status == "BuiltinEditable" and builtin and not locked or model_status == "Locked" and locked or
                   model_status == "Editable" and editable or model_status == "Unsupported" and not editable)
        if not matches:
            continue
        entry = self.entries.add()
        entry.name = record["defName"]
        entry.label = record.get("label", entry.name)
        entry.record_index = index
    self.index = 0
    record = selection_record(self)
    models = record.get("models", []) if record else []
    if models:
        defaults = [i for i, model in enumerate(models) if not model["filter"]]
        self.model_choice = str(defaults[-1] if defaults else len(models) - 1)


def mod_filter_changed(self, context):
    if _restoring_scene:
        return
    index = next((i for i, entry in enumerate(self.mod_entries) if entry.name == self.mod_filter), 0)
    if self.mod_index != index:
        self.mod_index = index
    refresh(self, context)


def mod_index_changed(self, context):
    if not _loading and not _restoring_scene and 0 <= self.mod_index < len(self.mod_entries):
        package = self.mod_entries[self.mod_index].name
        if self.mod_filter != package:
            self.mod_filter = package


def selection_changed(self, context):
    if _loading or _restoring_scene or not self.auto_reference or context is None or context.scene != self.id_data or self.workflow_stage in ("Setup", "Addons"):
        return
    record = selected(context)
    if record:
        try:
            show_reference(context.scene, record)
        except (OSError, RuntimeError, ValueError) as error:
            self.status = "Reference: " + str(error)


def reference_transform_changed(self, context):
    if _loading or _restoring_scene or context is None or context.scene != self.id_data:
        return
    if not self.auto_reference and (self.guides is None or not any(obj and obj.type == "EMPTY" and obj.get("rimvision_guide") for obj in self.guides.objects)):
        return
    record = selected(context)
    if record:
        try:
            show_reference(context.scene, record)
        except (OSError, RuntimeError, ValueError) as error:
            self.status = "Reference: " + str(error)


def index_changed(self, context):
    if _restoring_scene:
        return
    if not _loading and context and context.scene == self.id_data:
        record = selected(context)
        active = context.view_layer.objects.active
        if active and active.get("rimvision_def") and active["rimvision_def"] != (record["defName"] if record else "") and not active.hide_get(view_layer=context.view_layer):
            root = model_root(active)
            root["rimvision_filter"] = self.graphic_filter
            root["rimvision_height_offset"] = self.height_offset
        show_catalog_models(context, record)
        active = context.view_layer.objects.active
        self.current_model = model_root(active) if active and record and active.get("rimvision_def") == record["defName"] else None
        models = record.get("models", []) if record else []
        if models:
            defaults = [i for i, model in enumerate(models) if not model["filter"]]
            self.model_choice = str(defaults[-1] if defaults else len(models) - 1)
    selection_changed(self, context)


def model_items(self, context):
    record = selection_record(self)
    return _model_items.get(record["defName"] if record else "", _empty_model_items)


def cache_root():
    return Path(bpy.utils.user_resource("SCRIPTS", path="rimvision_cache", create=True))


def sprite_status_context(settings):
    try:
        config_time = Path(bpy.path.abspath(settings.config)).stat().st_mtime_ns if settings.config else 0
    except OSError:
        config_time = -1
    return repr((settings.game, settings.workshop, settings.config, settings.version, config_time))


def set_sprite_status(settings, textures, originals):
    settings.sprites_ready = bool(textures and originals)
    settings.sprite_count = len(textures) if settings.sprites_ready else 0
    settings.sprite_status_context = sprite_status_context(settings)


def refresh_sprite_status(settings):
    if not settings.game:
        set_sprite_status(settings, {}, {})
        return
    game = Path(bpy.path.abspath(settings.game))
    _, workshops, _ = find_installations(str(game))
    if settings.workshop:
        workshops.append(Path(bpy.path.abspath(settings.workshop)))
    mods, _ = discover(game, list(dict.fromkeys(workshops)))
    config = bpy.path.abspath(settings.config) if settings.config else None
    roots = sprite_folders(mods, settings.version, active_mods(config))
    set_sprite_status(settings, cached_textures(cache_root(), game, roots), cached_textures(cache_root(), game, roots, base=True))


def locate(settings):
    game, workshops, config = find_installations(bpy.path.abspath(settings.game) if settings.game else "")
    if game:
        settings.game = str(game)
    if workshops and not settings.workshop:
        settings.workshop = str(workshops[0])
    if config and not settings.config:
        settings.config = str(config)
    return game


def preview_icon(record):
    path = record.get("previewSprite") or sprite_path(record, "Thing") or sprite_path(record, "South")
    if not path:
        return 0
    try:
        if path not in _previews:
            _previews.load(path, path, "IMAGE")
        return _previews[path].icon_id
    except (OSError, RuntimeError, KeyError):
        return 0


class RVEntry(bpy.types.PropertyGroup):
    label: StringProperty(translation_context=CONTEXT)
    record_index: IntProperty(translation_context=CONTEXT)


class RVAddonModel(bpy.types.PropertyGroup):
    definition: StringProperty(translation_context=CONTEXT)
    file: StringProperty(translation_context=CONTEXT)
    index: IntProperty(translation_context=CONTEXT)
    graphic_filter: StringProperty(translation_context=CONTEXT)


class RVFoundAddon(bpy.types.PropertyGroup):
    path: StringProperty(subtype="DIR_PATH", translation_context=CONTEXT)
    package: StringProperty(translation_context=CONTEXT)
    count: IntProperty(translation_context=CONTEXT)
    state: StringProperty(translation_context=CONTEXT)
    origin: StringProperty(translation_context=CONTEXT)


class RVModEntry(bpy.types.PropertyGroup):
    label: StringProperty(translation_context=CONTEXT)
    inactive: BoolProperty(translation_context=CONTEXT)


class RVPreferences(bpy.types.AddonPreferences):
    bl_idname = __name__
    language_index: IntProperty(default=0, min=0, max=2, options={"HIDDEN"}, translation_context=CONTEXT)
    language: EnumProperty(name="Language", description="Use Blender's language, or choose the addon interface language",
        items=translated_items([("AUTO", "Blender language", "", 0, 0), ("EN", "English", "", 0, 1), ("RU", "Русский", "", 0, 2)]),
        default=0, get=language_get, set=language_set, update=language_changed, translation_context=CONTEXT)

    def draw(self, context):
        ui(self.layout).prop(self, "language")


def effect_changed(self, context):
    if _restoring_scene:
        return
    obj = self.id_data
    if obj.type == "LIGHT" and self.kind == "light":
        obj.data.color = self.color
        obj.data.energy = self.intensity * 100
        obj.data.use_custom_distance = True
        obj.data.cutoff_distance = self.radius


class RVEffectSettings(bpy.types.PropertyGroup):
    kind: EnumProperty(items=translated_items([("None", "None", ""), ("fire", "Fire", ""), ("light", "Light", "")]), default=0, update=effect_changed, translation_context=CONTEXT)
    size: FloatProperty(name="Fire size", default=1, min=0.05, max=5, translation_context=CONTEXT)
    radius: FloatProperty(name="Visual light radius", default=8, min=0.1, max=32, update=effect_changed, translation_context=CONTEXT)
    intensity: FloatProperty(name="Intensity", default=1, min=0, max=8, update=effect_changed, translation_context=CONTEXT)
    color: FloatVectorProperty(name="Game light tint", subtype="COLOR", size=3, min=0, max=1, default=(1, 1, 1), update=effect_changed, translation_context=CONTEXT)
    always: BoolProperty(name="Decorative fire –  always burning", default=False, translation_context=CONTEXT)


class RVSettings(bpy.types.PropertyGroup):
    workflow_stage: EnumProperty(items=translated_items([("Auto", "Automatic", ""), ("Setup", "Setup", ""), ("Addons", "Addon selection", ""), ("Ready", "Working", "")]), default=0, translation_context=CONTEXT)
    game: StringProperty(name="RimWorld folder", subtype="DIR_PATH", translation_context=CONTEXT)
    workshop: StringProperty(name="Workshop 294100 folder", subtype="DIR_PATH", translation_context=CONTEXT)
    model_root: StringProperty(name="Mod folder with models", description="Optional working mod copy containing OptionalContent; its models take priority", subtype="DIR_PATH", translation_context=CONTEXT)
    version: StringProperty(name="RimWorld version", default="1.6", translation_context=CONTEXT)
    snapshot: StringProperty(name="Loaded catalog JSON", subtype="FILE_PATH", translation_context=CONTEXT)
    config: StringProperty(name="ModsConfig.xml", subtype="FILE_PATH", translation_context=CONTEXT)
    active_only: BoolProperty(name="Only active mods", default=False, translation_context=CONTEXT)
    advanced: BoolProperty(name="Paths and advanced settings", translation_context=CONTEXT)
    surface_options: BoolProperty(name="Active material settings", update=section_changed("surface_options"), translation_context=CONTEXT)
    addon_options: BoolProperty(name="Addon", update=section_changed("addon_options"), translation_context=CONTEXT)
    addon_models_options: BoolProperty(name="Addon models", default=True, update=section_changed("addon_models_options"), translation_context=CONTEXT)
    setup_options: BoolProperty(name="Game and paths", update=section_changed("setup_options"), translation_context=CONTEXT)
    catalog_options: BoolProperty(name="Catalog", update=section_changed("catalog_options"), translation_context=CONTEXT)
    model_options: BoolProperty(name="Current model", update=section_changed("model_options"), translation_context=CONTEXT)
    reference_options: BoolProperty(name="Reference", update=section_changed("reference_options"), translation_context=CONTEXT)
    effect_options: BoolProperty(name="Fire and light", update=section_changed("effect_options"), translation_context=CONTEXT)
    export_options: BoolProperty(name="Save and export", update=section_changed("export_options"), translation_context=CONTEXT)
    mod_filter: EnumProperty(name="Mod", items=translated_items(lambda self, context: _mod_items), update=mod_filter_changed, translation_context=CONTEXT)
    mod_entries: CollectionProperty(type=RVModEntry, translation_context=CONTEXT)
    mod_index: IntProperty(update=mod_index_changed, translation_context=CONTEXT)
    content_state: EnumProperty(name="Content state", items=translated_items([("All", "All content", "Show content from any installed mod or DLC"),
        ("Enabled", "Enabled mods / DLC", "Content enabled in ModsConfig.xml"), ("Disabled", "Disabled mods / DLC", "Installed content disabled in ModsConfig.xml")]), default=0, update=refresh, translation_context=CONTEXT)
    model_status: EnumProperty(name="Model status", items=translated_items([("All", "All models", ""),
        ("Ready", "Ready XML model", "At least one XML model or editable template exists"), ("Missing", "No XML model", "No saved XML model exists"),
        ("BuiltinEditable", "Built-in: replaceable", "Built-in geometry can be replaced with an XML model"), ("Locked", "Built-in: locked", "The built-in renderer bypasses XML replacement"),
        ("Editable", "Editable items", "Item types supported by XML model export"), ("Unsupported", "Replacement unsupported", "Doors, unsupported pawns and other separate renderers")]), default=0, update=refresh, translation_context=CONTEXT)
    model_choice: EnumProperty(name="Model", items=translated_items(model_items), translation_context=CONTEXT)
    reference_direction: EnumProperty(name="Reference direction", items=translated_items(_directions), default=3, update=selection_changed, translation_context=CONTEXT)
    reference_all_sides: BoolProperty(name="All available sides", default=True, update=reference_transform_changed, translation_context=CONTEXT)
    reference_opacity: FloatProperty(name="Reference opacity", min=0.05, max=1, default=0.8, update=selection_changed, translation_context=CONTEXT)
    reference_side: EnumProperty(name="Placement side", items=translated_items([("Right", "Right (+X)", ""), ("Left", "Left (−X)", ""),
        ("North", "North (+Y)", ""), ("South", "South (−Y)", ""), ("Above", "Above (+Z)", ""), ("Below", "Below (−Z)", "")]), default=0, update=reference_transform_changed, translation_context=CONTEXT)
    reference_gap: FloatProperty(name="Distance from model", default=0.5, min=0, update=reference_transform_changed, translation_context=CONTEXT)
    reference_offset: FloatVectorProperty(name="Additional offset", size=3, subtype="TRANSLATION", default=(0, 0, 0), update=reference_transform_changed, translation_context=CONTEXT)
    reference_rotation: FloatVectorProperty(name="Rotation", size=3, subtype="EULER", unit="ROTATION", default=(math.pi / 2, 0, -math.pi / 2), update=reference_transform_changed, translation_context=CONTEXT)
    auto_reference: BoolProperty(name="Reference on item selection", default=True, update=selection_changed, translation_context=CONTEXT)
    guides: PointerProperty(type=bpy.types.Collection, translation_context=CONTEXT)
    output: StringProperty(name="Output mod folder", subtype="DIR_PATH", translation_context=CONTEXT)
    addon_path: StringProperty(subtype="DIR_PATH", translation_context=CONTEXT)
    addon_name: StringProperty(translation_context=CONTEXT)
    found_addons: CollectionProperty(type=RVFoundAddon, translation_context=CONTEXT)
    found_addon_index: IntProperty(translation_context=CONTEXT)
    addon_discovery_status: StringProperty(default="Addons have not been searched yet", translation_context=CONTEXT)
    addon_edit_name: StringProperty(name="Mod name", translation_context=CONTEXT)
    addon_description: PointerProperty(type=bpy.types.Text, translation_context=CONTEXT)
    addon_preview: StringProperty(name="Preview", subtype="FILE_PATH", translation_context=CONTEXT)
    addon_metadata_options: BoolProperty(name="Name, description and preview", translation_context=CONTEXT)
    addon_models: CollectionProperty(type=RVAddonModel, translation_context=CONTEXT)
    addon_index: IntProperty(translation_context=CONTEXT)
    addon_collection: PointerProperty(type=bpy.types.Collection, translation_context=CONTEXT)
    current_model: PointerProperty(type=bpy.types.Object, translation_context=CONTEXT)
    auto_attach_meshes: BoolProperty(name="Attach new Shift+A meshes", default=True, translation_context=CONTEXT)
    search: StringProperty(name="Search (name / mod)", update=refresh, translation_context=CONTEXT)
    category: EnumProperty(name="Category", items=translated_items([(v, v, "") for v in ("All", "Building", "Item", "Plant", "Pawn")]), default=0, update=refresh, translation_context=CONTEXT)
    entries: CollectionProperty(type=RVEntry, translation_context=CONTEXT)
    index: IntProperty(update=index_changed, translation_context=CONTEXT)
    status: StringProperty(default="Find the game and load the catalog", translation_context=CONTEXT)
    sprites_ready: BoolProperty(options={"SKIP_SAVE"}, translation_context=CONTEXT)
    sprite_count: IntProperty(options={"SKIP_SAVE"}, translation_context=CONTEXT)
    sprite_status_context: StringProperty(options={"SKIP_SAVE"}, translation_context=CONTEXT)
    graphic_filter: StringProperty(name="Graphic path filter", translation_context=CONTEXT)
    height_offset: FloatProperty(name="Height offset", default=0, translation_context=CONTEXT)
    overwrite: BoolProperty(name="Replace existing model file", default=False, translation_context=CONTEXT)


class RVSurface(bpy.types.PropertyGroup):
    mode: EnumProperty(name="Surface", items=translated_items([("Game", "Game texture", ""), ("Solid", "Solid color", ""), ("Custom", "Custom texture", ""), ("Object", "Object material", "Use the color and texture from the assigned Blender material"), ("Atlas", "All-view atlas", "Combine game sprites into one 2×2 texture for UV mapping")]), default=0, translation_context=CONTEXT)
    texture_path: StringProperty(name="Texture file", subtype="FILE_PATH", translation_context=CONTEXT)
    direction: EnumProperty(name="Texture direction", items=translated_items(_directions), default=0, translation_context=CONTEXT)
    color: FloatVectorProperty(name="Color", subtype="COLOR", size=3, min=0, max=1, default=(0.5, 0.5, 0.5), translation_context=CONTEXT)
    tint: BoolProperty(name="Tint with thing color", translation_context=CONTEXT)
    two_sided: BoolProperty(name="Two sided", default=True, translation_context=CONTEXT)
    turret_top: BoolProperty(name="Turret top", translation_context=CONTEXT)
    required_links: StringProperty(name="Required links", translation_context=CONTEXT)
    forbidden_links: StringProperty(name="Forbidden links", translation_context=CONTEXT)
    use_uv: BoolProperty(name="Export active UV map", default=True, translation_context=CONTEXT)


class RV_UL_catalog(bpy.types.UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_propname, index):
        layout = ui(layout)
        record = _records[item.record_index] if item.record_index < len(_records) else {}
        icon_id = preview_icon(record) if record else 0
        row = layout.row()
        row.active = not record.get("inactiveContent", False)
        columns = row.split(factor=0.25)
        title = columns.column()
        if icon_id:
            title.label(text=item.label, icon_value=icon_id)
        else:
            title.label(text=item.label, icon="MESH_CUBE")
        remaining = columns.split(factor=1 / 3)
        remaining.column().label(text=item.name, translate=False)
        flags = remaining.split(factor=0.5)
        flags.column().label(text="Disabled" if record.get("inactiveContent") else "", icon="CHECKBOX_DEHLT" if record.get("inactiveContent") else "NONE")
        status = flags.column()
        if record.get("builtin3dLocked") or record.get("door"):
            status.label(text="3D • replacement unsupported", icon="LOCKED")
        elif record.get("models"):
            status.label(text="Custom 3D", icon="CHECKMARK")
        elif record.get("builtin3d") or record.get("defName") == "Wall":
            status.label(text="Built-in 3D", icon="MESH_CUBE")
        else:
            status.label(text="")


class RV_UL_mod_filter(bpy.types.UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_propname, index):
        layout = ui(layout)
        row = layout.row()
        row.active = not item.inactive
        row.label(text=item.label, icon="CHECKBOX_DEHLT" if item.inactive else "CHECKBOX_HLT")
        if item.inactive:
            row.label(text="Disabled")


class RVModFilterPanel(bpy.types.Panel):
    bl_translation_context = CONTEXT
    bl_label = "Mod"
    bl_idname = "RIMVISION_PT_mod_filter"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_options = {"INSTANCED"}
    bl_ui_units_x = 20

    def draw(self, context):
        settings = context.scene.rimvision
        if settings.mod_entries:
            ui(self.layout).template_list("RV_UL_mod_filter", "", settings, "mod_entries", settings, "mod_index", rows=min(len(settings.mod_entries), 12))
        else:
            ui(self.layout).label(text="Load the catalog first")


class RVLoad(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.load_catalog"
    bl_label = "Load catalog"
    source: EnumProperty(items=translated_items([("Files", "Files", ""), ("Snapshot", "Snapshot", "")]), translation_context=CONTEXT)

    def execute(self, context):
        global _records, _mod_items, _model_items, _loading
        settings = context.scene.rimvision
        try:
            if self.source == "Snapshot":
                if not settings.snapshot:
                    raise ValueError("Select a JSON catalog")
                records = load_snapshot(bpy.path.abspath(settings.snapshot))
                status = "Loaded definitions: " + str(len(records))
            else:
                if not settings.game:
                    locate(settings)
                if not settings.game:
                    raise ValueError("Game not found. Set the RimWorld folder in path settings.")
                game = Path(bpy.path.abspath(settings.game))
                _, auto_workshops, _ = find_installations(str(game))
                workshop = list(dict.fromkeys([*auto_workshops, *([Path(bpy.path.abspath(settings.workshop))] if settings.workshop else [])]))
                config = bpy.path.abspath(settings.config) if settings.config else None
                mods, _ = discover(game, workshop)
                roots = sprite_folders(mods, settings.version, active_mods(config))
                textures = cached_textures(cache_root(), game, roots)
                originals = cached_textures(cache_root(), game, roots, base=True)
                set_sprite_status(settings, textures, originals)
                records, warnings = scan(game, workshop, settings.version, textures,
                                         config, settings.active_only,
                                         bpy.path.abspath(settings.model_root) if settings.model_root else None,
                                         base_cached=originals,
                                         supplemental_cached=cached_textures(cache_root(), game, roots, supplemental=True))
                status = str(len(records)) + " objects • " + str(sum(bool(r.get("models")) for r in records)) + " with models • XML patches are not applied"
                for warning in warnings:
                    print("[RimVision] " + warning)
            _records = records
            inactive_packages = {record.get("packageId") for record in records if record.get("inactiveContent", False)}
            mods = {r.get("packageId", ""): r.get("mod", "") for r in records}
            _mod_items = [("All", "All mods", "")] + [(package, name or package, package) for package, name in sorted(mods.items(), key=lambda p: p[1].casefold()) if package]
            _model_items = {r["defName"]: [(str(i), (m["filter"] or "Default") + " • " + m["mod"], m["file"], i) for i, m in enumerate(r.get("models", []))] for r in records if r.get("models")}
            _previews.clear()
            _loading = True
            try:
                for scene in bpy.data.scenes:
                    scene.rimvision.mod_entries.clear()
                    for package, name, _ in _mod_items:
                        entry = scene.rimvision.mod_entries.add()
                        entry.name, entry.label = package, name
                        entry.inactive = package in inactive_packages
                    scene.rimvision.mod_index = 0
                    scene.rimvision.mod_filter = "All"
                    refresh(scene.rimvision, context)
                    scene.rimvision.status = status
                    refresh_model_textures(scene, records)
            finally:
                _loading = False
            index_changed(settings, context)
            try:
                refresh_found_addons(settings)
            except (OSError, ValueError, ET.ParseError, RuntimeError) as error:
                settings.addon_discovery_status = str(error)
            if settings.workflow_stage == "Setup" and _records and settings.sprites_ready and settings.sprite_status_context == sprite_status_context(settings):
                settings.workflow_stage = "Addons"
        except (OSError, ValueError, KeyError, ET.ParseError, TypeError, RuntimeError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        return {"FINISHED"}


class RVFind(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.find_game"
    bl_label = "Find game and load catalog"

    def execute(self, context):
        if not locate(context.scene.rimvision):
            context.scene.rimvision.advanced = True
            self.report({"ERROR"}, tr("RimWorld was not found in Steam libraries; select its folder manually"))
            return {"CANCELLED"}
        try:
            return bpy.ops.rimvision.load_catalog(source="Files")
        except RuntimeError as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}


class RVPrepareSprites(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.prepare_sprites"
    bl_label = "Prepare game sprites"
    _timer = None

    def execute(self, context):
        global _extracting
        if _extracting:
            self.report({"WARNING"}, tr("Extraction is already running"))
            return {"CANCELLED"}
        settings = context.scene.rimvision
        if not settings.game:
            locate(settings)
        if not settings.game:
            self.report({"ERROR"}, tr("Find the game folder first"))
            return {"CANCELLED"}
        game = bpy.path.abspath(settings.game)
        _, workshops, _ = find_installations(game)
        if settings.workshop:
            workshops.append(Path(bpy.path.abspath(settings.workshop)))
        try:
            mods, _ = discover(Path(game), list(dict.fromkeys(workshops)))
            config = bpy.path.abspath(settings.config) if settings.config else None
            active = active_mods(config)
            roots = sprite_folders(mods, settings.version, active)
            extra_roots = [root for root in sprite_folders(mods, settings.version, active, include_inactive=True) if root not in roots]
        except (OSError, ValueError, ET.ParseError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        cache = cache_root()
        self._messages = queue.Queue()
        self._cancelled = threading.Event()
        self._scene = context.scene
        self._game = game
        messages, cancelled = self._messages, self._cancelled

        def worker():
            try:
                textures, errors = extract(cache, game, roots, lambda message: messages.put(("status", message)), cancelled, extra_roots)
                messages.put(("done", (len(textures), errors)))
            except Exception as error:
                messages.put(("error", str(error)))

        threading.Thread(target=worker, daemon=True).start()
        _extracting = self
        self._timer = context.window_manager.event_timer_add(0.2, window=context.window)
        context.window_manager.modal_handler_add(self)
        settings.status = "Extracting sprites… Esc –  cancel"
        return {"RUNNING_MODAL"}

    def cancel(self, context):
        global _extracting
        self._cancelled.set()
        if self._timer:
            context.window_manager.event_timer_remove(self._timer)
            self._timer = None
        _extracting = None

    def modal(self, context, event):
        if event.type == "ESC":
            self._cancelled.set()
            self._scene.rimvision.status = "Cancelling extraction…"
        if event.type != "TIMER":
            return {"PASS_THROUGH"}
        while not self._messages.empty():
            kind, value = self._messages.get_nowait()
            if kind == "status":
                self._scene.rimvision.status = "Sprites: " + value
            else:
                cancelled = self._cancelled.is_set()
                self.cancel(context)
                if kind == "error":
                    self.report({"ERROR"}, tr(value))
                    self._scene.rimvision.status = value
                    return {"CANCELLED"}
                for error in value[1]:
                    print("[RimVision] " + error)
                if not cancelled and self._scene == context.scene and bpy.path.abspath(context.scene.rimvision.game) == self._game:
                    bpy.ops.rimvision.load_catalog(source="Files")
                self.report({"INFO"}, tr(("Extraction cancelled. " if cancelled else "") + "Cached sprites: " + str(value[0])))
                return {"FINISHED"}
        if context.area:
            context.area.tag_redraw()
        return {"PASS_THROUGH"}


class RVReference(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.show_reference"
    bl_label = "Show reference and cell grid"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        record = selected(context)
        if record is None:
            return {"CANCELLED"}
        try:
            show_reference(context.scene, record)
        except (OSError, RuntimeError, ValueError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        return {"FINISHED"}


class RVImport(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.import_model"
    bl_label = "Add ready-made model to scene"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        record = selected(context)
        try:
            if context.mode != "OBJECT" or record is None:
                raise ValueError("Select an item in Object Mode")
            models = record.get("models", [])
            index = int(context.scene.rimvision.model_choice)
            source = models[index]
            obj = import_model(context, record, source["file"], source["index"])
            attach_to_addon(context, obj)
        except (OSError, RuntimeError, ValueError, IndexError, ET.ParseError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        return {"FINISHED"}


class RVImportFile(bpy.types.Operator, ImportHelper):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.import_model_file"
    bl_label = "Open XML model…"
    bl_options = {"REGISTER", "UNDO"}
    filename_ext = ".xml"
    filter_glob: StringProperty(default="*.xml", options={"HIDDEN"}, translation_context=CONTEXT)
    variant: EnumProperty(name="Model variant", items=translated_items(lambda self, context: _file_model_items), translation_context=CONTEXT)
    pick_variant: BoolProperty(default=False, options={"HIDDEN", "SKIP_SAVE"}, translation_context=CONTEXT)

    def draw(self, context):
        ui(self.layout).prop(self, "variant")

    def execute(self, context):
        global _file_model_items
        record = selected(context)
        try:
            if context.mode != "OBJECT" or record is None:
                raise ValueError("Select a catalog item in Object Mode first")
            nodes = list(ET.parse(self.filepath).getroot())
            candidates = [i for i, node in enumerate(nodes) if node.findtext("thingDef", "").strip() == record["defName"]]
            if not candidates:
                raise ValueError("No XML model for " + record["defName"])
            if len(candidates) > 1 and not self.pick_variant:
                _file_model_items = [(str(i), nodes[i].findtext("graphicPathContains") or "Default model", str(i), i) for i in candidates]
                self.variant = str(candidates[0])
                self.pick_variant = True
                return context.window_manager.invoke_props_dialog(self, title=tr(self.bl_label))
            index = int(self.variant) if self.pick_variant else candidates[0]
            obj = import_model(context, record, self.filepath, index)
            attach_to_addon(context, obj)
        except (OSError, RuntimeError, ValueError, ET.ParseError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        return {"FINISHED"}


class RVTemplate(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.create_template"
    bl_label = "Add model to scene"
    bl_options = {"REGISTER", "UNDO"}
    force_new: BoolProperty(default=False, options={"HIDDEN", "SKIP_SAVE"}, translation_context=CONTEXT)
    primitive: EnumProperty(name="Starting shape", items=translated_items([("Cube", "Cube", ""), ("Cylinder", "Cylinder", ""),
        ("Cone", "Cone", ""), ("Sphere", "UV sphere", ""), ("Icosphere", "Icosphere", ""),
        ("Plane", "Plane", ""), ("Torus", "Torus", "")]), default=0, translation_context=CONTEXT)

    def invoke(self, context, event):
        record = selected(context)
        if record is None or context.mode != "OBJECT" or (record.get("models") and not self.force_new):
            return self.execute(context)
        return context.window_manager.invoke_props_dialog(self, title=tr(self.bl_label))

    def draw(self, context):
        ui(self.layout).prop(self, "primitive")

    def execute(self, context):
        record = selected(context)
        if record is None:
            return {"CANCELLED"}
        if context.mode != "OBJECT":
            self.report({"ERROR"}, tr("Switch to Object Mode"))
            return {"CANCELLED"}
        if record.get("models") and not self.force_new:
            return RVImport.execute(self, context)
        primitive, options = {
            "Cube": (bpy.ops.mesh.primitive_cube_add, {"size": 1}),
            "Cylinder": (bpy.ops.mesh.primitive_cylinder_add, {"radius": 0.5, "depth": 1}),
            "Cone": (bpy.ops.mesh.primitive_cone_add, {"radius1": 0.5, "radius2": 0, "depth": 1}),
            "Sphere": (bpy.ops.mesh.primitive_uv_sphere_add, {"radius": 0.5}),
            "Icosphere": (bpy.ops.mesh.primitive_ico_sphere_add, {"radius": 0.5}),
            "Plane": (bpy.ops.mesh.primitive_plane_add, {"size": 1}),
            "Torus": (bpy.ops.mesh.primitive_torus_add, {"mode": "MAJOR_MINOR", "major_radius": 0.35, "minor_radius": 0.15}),
        }[self.primitive]
        primitive(align="WORLD", location=(0, 0, 0), rotation=(0, 0, 0), **options)
        obj = context.object
        obj.name = record["defName"]
        if self.primitive == "Cube":
            obj.scale = (record["width"], record["depth"], 1)
        else:
            points = [vertex.co for vertex in obj.data.vertices]
            width = max(point.x for point in points) - min(point.x for point in points)
            depth = max(point.y for point in points) - min(point.y for point in points)
            scale = min(record["width"] / width, record["depth"] / depth)
            obj.scale = (scale, scale, scale)
        obj.location.z = -min(vertex.co.z for vertex in obj.data.vertices) * obj.scale.z
        material = bpy.data.materials.new(record["defName"] + " surface")
        name_resource(material, record, "Material")
        material.rimvision.use_uv = False
        obj.data.materials.append(material)
        obj["rimvision_def"] = record["defName"]
        context.scene.rimvision.graphic_filter = ""
        context.scene.rimvision.height_offset = 0
        obj["rimvision_filter"] = ""
        obj["rimvision_height_offset"] = 0.0
        attach_to_addon(context, obj)
        show_reference(context.scene, record)
        frame_model(context, record, obj)
        return {"FINISHED"}


class RVTexture(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.use_sprite"
    bl_label = "Assign game sprite"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        record = selected(context)
        obj = context.object
        if record is None or obj is None or obj.type != "MESH" or context.mode != "OBJECT":
            self.report({"ERROR"}, tr("Select a catalog entry and a mesh in Object Mode"))
            return {"CANCELLED"}
        direction = obj.active_material.rimvision.direction if obj.active_material else "Thing"
        path = sprite_path(record, direction)
        if not path or not Path(path).is_file():
            self.report({"ERROR"}, tr("Sprite unavailable. Prepare game sprites first."))
            return {"CANCELLED"}
        try:
            image = image_for(record, direction)
        except RuntimeError as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        material = obj.active_material
        if material is None or material.get("rimvision_auto_name") is None or material.users > 1 or material.rimvision.mode != "Game":
            material = bpy.data.materials.new(record["defName"] + " game texture")
            name_resource(material, record, "Material")
        else:
            material.rimvision.mode = "Game"
            material.rimvision.use_uv = True
        material.rimvision.direction = direction
        material_preview(material, record)
        if len(obj.data.materials):
            obj.data.materials[obj.active_material_index] = material
        else:
            obj.data.materials.append(material)
        self.report({"INFO"}, tr("Game sprite assigned to the active material slot"))
        return {"FINISHED"}


def number(value):
    if not math.isfinite(value):
        raise ValueError("Non-finite coordinate")
    return format(value, ".9g")


def field(node, name, value):
    ET.SubElement(node, name).text = str(value)


def refresh_found_addons(settings):
    previous = settings.found_addons[settings.found_addon_index].path if 0 <= settings.found_addon_index < len(settings.found_addons) else settings.addon_path
    game = locate(settings)
    _, workshops, _ = find_installations(str(game) if game else "")
    if settings.workshop:
        workshops.append(Path(bpy.path.abspath(settings.workshop)))
    workshops = [root for root in dict.fromkeys(workshops) if root.is_dir()]
    roots = [root for root, _, _ in discover(game, workshops)[0]] if game else [root for workshop in workshops for root in workshop.iterdir() if root.is_dir()]
    local = bpy.utils.user_resource("SCRIPTS", path="rimvision_addons", create=False)
    if local and Path(local).is_dir():
        roots.extend(root for root in Path(local).iterdir() if root.is_dir())
    roots.extend(Path(bpy.path.abspath(path)) for path in (settings.addon_path, settings.model_root) if path)
    found, warnings = find_addons(roots)
    active = active_mods(bpy.path.abspath(settings.config) if settings.config else None)
    settings.found_addons.clear()
    for addon in found:
        entry = settings.found_addons.add()
        entry.name, entry.path, entry.package, entry.count = addon["name"], addon["path"], addon["package"], addon["count"]
        entry.state = "Status unknown" if active is None else "Enabled" if entry.package.lower() in active else "Not enabled"
        entry.origin = "Workshop" if any(Path(entry.path).is_relative_to(root.resolve()) for root in workshops) else "Mod folder"
    settings.found_addon_index = next((i for i, entry in enumerate(settings.found_addons) if entry.path.casefold() == previous.casefold()), 0)
    settings.addon_discovery_status = "Addons found: " + str(len(found)) + (" • errors: " + str(len(warnings)) if warnings else "")
    for warning in warnings:
        print("[RimVision] " + warning)


def refresh_addon(settings):
    name, models = read_addon(bpy.path.abspath(settings.addon_path))
    settings.addon_name = name
    settings.addon_models.clear()
    for model in models:
        entry = settings.addon_models.add()
        entry.name = model["definition"] + (" • " + model["filter"] if model["filter"] else "")
        entry.definition = model["definition"]
        entry.file = model["file"]
        entry.index = model["index"]
        entry.graphic_filter = model["filter"]
    settings.addon_index = min(settings.addon_index, max(len(models) - 1, 0))


def open_addon(context, root):
    settings = context.scene.rimvision
    name, _ = read_addon(root)
    root = str(Path(root).resolve())
    add_dependency(root, "papaya.rimvision", "RimVision")
    ensure_content(root)
    collection = next((c for c in context.scene.collection.children if c.get("rimvision_addon") == root), None)
    if collection is None:
        collection = bpy.data.collections.new(name)
        context.scene.collection.children.link(collection)
        collection["rimvision_addon"] = root
    settings.addon_path = settings.output = root
    settings.addon_collection = collection
    if any((Path(root) / "OptionalContent" / n / "Models").is_dir() for n in ("RimVision", "RimWorld3DVR")):
        settings.model_root = root
    else:
        settings.model_root = ""
    refresh_addon(settings)
    load_addon_metadata(settings)
    if settings.workflow_stage in ("Setup", "Addons"):
        settings.workflow_stage = "Ready"
        settings.catalog_options = True
        settings.model_options = True
        settings.addon_options = False


def load_addon_metadata(settings):
    root = str(Path(bpy.path.abspath(settings.addon_path)).resolve())
    about = ET.parse(Path(root) / "About/About.xml").getroot()
    name = about.findtext("name", Path(root).name)
    settings.addon_edit_name = name
    description = next((text for text in bpy.data.texts if text.get("rimvision_addon") == root), None)
    if description is None:
        description = bpy.data.texts.new(name + " –  description")
        description["rimvision_addon"] = root
    description.from_string(about.findtext("description", ""))
    settings.addon_description = description
    preview = Path(root) / "About/Preview.png"
    settings.addon_preview = str(preview) if preview.is_file() else ""


def attach_to_addon(context, obj):
    settings = context.scene.rimvision
    record = selected(context)
    if record and obj.get("rimvision_def") == record["defName"]:
        settings.current_model = model_root(obj)
    if not settings.addon_path or settings.addon_collection is None:
        show_catalog_models(context, selected(context))
        return
    collection = settings.addon_collection
    for member in (obj, *obj.children_recursive):
        if collection.objects.get(member.name) is None:
            collection.objects.link(member)
        for old in list(member.users_collection):
            if old != collection:
                old.objects.unlink(member)
    show_catalog_models(context, selected(context))


class RVEditAddonDescription(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.edit_addon_description"
    bl_label = "Edit description"

    def execute(self, context):
        description = context.scene.rimvision.addon_description
        if description is None and context.scene.rimvision.addon_path:
            try:
                load_addon_metadata(context.scene.rimvision)
                description = context.scene.rimvision.addon_description
            except (OSError, ValueError, ET.ParseError, RuntimeError) as error:
                self.report({"ERROR"}, tr(str(error)))
                return {"CANCELLED"}
        if description is None or context.area is None:
            return {"CANCELLED"}
        existing = next((area for area in context.screen.areas if area.type == "TEXT_EDITOR" and area.spaces.active.text == description), None)
        if existing is None:
            before = {area.as_pointer() for area in context.screen.areas}
            try:
                region = next(region for region in context.area.regions if region.type == "WINDOW")
                with context.temp_override(area=context.area, region=region):
                    result = bpy.ops.screen.area_split(direction="HORIZONTAL", factor=0.65)
                existing = next((area for area in context.screen.areas if area.as_pointer() not in before), None)
                if result != {"FINISHED"} or existing is None:
                    self.report({"ERROR"}, tr("Could not open the description editor"))
                    return {"CANCELLED"}
                existing.type = "TEXT_EDITOR"
            except RuntimeError as error:
                self.report({"ERROR"}, tr(str(error)))
                return {"CANCELLED"}
        existing.spaces.active.text = description
        existing.spaces.active.show_word_wrap = True
        return {"FINISHED"}


class RVSaveAddonMetadata(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.save_addon_metadata"
    bl_label = "Save addon metadata"

    def execute(self, context):
        settings = context.scene.rimvision
        image = temporary = None
        try:
            if not settings.addon_path or not settings.addon_edit_name.strip():
                raise ValueError("Open an addon and enter its name")
            root = Path(bpy.path.abspath(settings.addon_path))
            read_addon(root)
            destination = root / "About/Preview.png"
            if settings.addon_preview:
                source = Path(bpy.path.abspath(settings.addon_preview)).resolve()
                if not source.is_file():
                    raise ValueError("Preview file not found")
                if source != destination.resolve():
                    image = bpy.data.images.load(str(source), check_existing=False)
                    if image.size[0] <= 0 or image.size[1] <= 0:
                        raise ValueError("Could not read the preview image")
                    with tempfile.NamedTemporaryFile(dir=destination.parent, suffix=".png", delete=False) as output:
                        temporary = Path(output.name)
                    image.file_format = "PNG"
                    image.filepath_raw = str(temporary)
                    image.save()
            description = settings.addon_description.as_string() if settings.addon_description else ET.parse(root / "About/About.xml").getroot().findtext("description", "")
            update_metadata(root, settings.addon_edit_name, description)
            if temporary:
                temporary.replace(destination)
            if destination.is_file():
                settings.addon_preview = str(destination)
                if str(destination) in _previews:
                    del _previews[str(destination)]
            ensure_content(root)
            refresh_addon(settings)
            settings.addon_edit_name = settings.addon_name
            if settings.addon_collection:
                settings.addon_collection.name = settings.addon_name
            if settings.addon_description:
                settings.addon_description.name = settings.addon_name + " –  description"
            self.report({"INFO"}, tr("Name, description and preview saved"))
        except (OSError, RuntimeError, ValueError, ET.ParseError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        finally:
            if image:
                bpy.data.images.remove(image)
            if temporary:
                temporary.unlink(missing_ok=True)
        return {"FINISHED"}


class RVCreateAddon(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.create_addon"
    bl_label = "Create addon"
    name: StringProperty(name="Name", translation_context=CONTEXT)

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self, title=tr(self.bl_label))

    def draw(self, context):
        ui(self.layout).prop(self, "name")

    def execute(self, context):
        settings = context.scene.rimvision
        try:
            game = locate(settings)
            parent = game / "Mods" if game else Path(bpy.utils.user_resource("SCRIPTS", path="rimvision_addons", create=True))
            root = create_addon(parent, self.name, settings.version)
            open_addon(context, root)
            try:
                refresh_found_addons(settings)
            except (OSError, ValueError, ET.ParseError, RuntimeError) as error:
                settings.addon_discovery_status = str(error)
            settings.status = "Created addon: " + settings.addon_name
            self.report({"INFO"}, tr("Addon created: " + str(root)))
        except (OSError, ValueError, ET.ParseError, RuntimeError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        return {"FINISHED"}


class RV_UL_found_addons(bpy.types.UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_propname, index):
        layout = ui(layout)
        row = layout.row()
        row.label(text=item.name, icon="OUTLINER_COLLECTION", translate=False)
        row.label(text=str(item.count) + " models")
        row.label(text=item.state)


class RVFindAddons(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.find_addons"
    bl_label = "Find addons"

    def execute(self, context):
        try:
            refresh_found_addons(context.scene.rimvision)
        except (OSError, ValueError, ET.ParseError, RuntimeError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        return {"FINISHED"}


class RVOpenFoundAddon(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.open_found_addon"
    bl_label = "Open selected addon"

    def execute(self, context):
        settings = context.scene.rimvision
        if not 0 <= settings.found_addon_index < len(settings.found_addons):
            self.report({"ERROR"}, tr("Select an addon in the list"))
            return {"CANCELLED"}
        return bpy.ops.rimvision.open_addon(directory=settings.found_addons[settings.found_addon_index].path)


class RVOpenAddon(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.open_addon"
    bl_label = "Open addon"
    directory: StringProperty(subtype="DIR_PATH", translation_context=CONTEXT)
    filter_folder: BoolProperty(default=True, options={"HIDDEN"}, translation_context=CONTEXT)

    def invoke(self, context, event):
        settings = context.scene.rimvision
        game = Path(bpy.path.abspath(settings.game)) if settings.game else locate(settings)
        if game and (game / "Mods").is_dir():
            self.directory = (game / "Mods").resolve().as_posix() + "/"
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        try:
            open_addon(context, bpy.path.abspath(self.directory))
            if context.scene.rimvision.game:
                bpy.ops.rimvision.load_catalog(source="Files")
            else:
                refresh_found_addons(context.scene.rimvision)
        except (OSError, ValueError, ET.ParseError, RuntimeError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        return {"FINISHED"}


class RVRefreshAddon(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.refresh_addon"
    bl_label = "Refresh addon model list"

    def execute(self, context):
        try:
            refresh_addon(context.scene.rimvision)
        except (OSError, ValueError, ET.ParseError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        return {"FINISHED"}


class RVLoadAddonModel(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.load_addon_model"
    bl_label = "Load addon model"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        settings = context.scene.rimvision
        try:
            if context.mode != "OBJECT" or not 0 <= settings.addon_index < len(settings.addon_models):
                raise ValueError("Select a model in Object Mode")
            entry = settings.addon_models[settings.addon_index]
            record = next((r for r in _records if r["defName"] == entry.definition), None)
            if record is None:
                raise ValueError("Load a game catalog with the mod defining " + entry.definition)
            settings.search = entry.definition
            settings.mod_filter = "All"
            settings.category = "All"
            settings.model_status = "All"
            settings.content_state = "All"
            settings.index = next(i for i, item in enumerate(settings.entries) if _records[item.record_index] is record)
            obj = import_model(context, record, entry.file, entry.index)
            attach_to_addon(context, obj)
        except (OSError, ValueError, RuntimeError, ET.ParseError, StopIteration) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        return {"FINISHED"}


class RV_UL_addon(bpy.types.UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_propname, index):
        layout = ui(layout)
        layout.label(text=item.name, icon="MESH_DATA", translate=False)


class RVExport(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.export_model"
    bl_label = "Export model"

    def execute(self, context):
        return self._export(context)

    def _export(self, context, record=None, objects=None):
        if _pending_meshes:
            attach_new_meshes()
        settings = context.scene.rimvision
        record = record if record is not None else selected(context)
        if objects is None:
            roots = {model_root(obj) if obj.get("rimvision_def") else obj for obj in context.selected_objects if obj.type == "MESH" and not obj.get("rimvision_role")}
            if not roots and context.object and context.object.get("rimvision_role") == "anchor" and context.object.parent and context.object.parent.type == "MESH":
                roots.add(model_root(context.object.parent))
            objects = sorted({part for obj in roots for part in (obj, *obj.children_recursive) if part.type == "MESH" and not part.get("rimvision_role")}, key=lambda obj: obj.name)
        if context.mode != "OBJECT" or not objects or record is None:
            self.report({"ERROR"}, tr("Select a catalog entry and meshes in Object Mode"))
            return {"CANCELLED"}
        if (record.get("category") not in ("Building", "Item", "Plant") and not
                (record.get("category") == "Pawn" and record.get("animal"))) or record.get("door", False):
            self.report({"ERROR"}, tr("Supports buildings, items, plants and animals; doors and other pawns use separate renderers"))
            return {"CANCELLED"}
        try:
            if not settings.addon_path and not settings.output:
                raise ValueError("Select an output mod folder")
            if any(obj.get("rimvision_def") and obj["rimvision_def"] != record["defName"] for obj in objects):
                raise ValueError("Selected meshes belong to another item. Select it in the catalog or save the addon collection.")
            if not re.fullmatch(r"[A-Za-z0-9_]+", record["defName"]):
                raise ValueError("ThingDef name is not safe for a filename")
            suffix = ""
            if settings.graphic_filter:
                import hashlib
                suffix = "_" + hashlib.sha256(settings.graphic_filter.encode("utf-8")).hexdigest()[:12]
            output_root = Path(bpy.path.abspath(settings.addon_path or settings.output))
            destination = output_root / "OptionalContent" / "RimVision" / "Models" / (record["defName"] + suffix + ".xml")
            owned_source = False
            sources = {obj.get("rimvision_source", "") for obj in objects}
            if settings.addon_path and len(sources) == 1 and "" not in sources:
                source = Path(next(iter(sources))).resolve()
                if source.is_file() and any(source.is_relative_to((output_root / "OptionalContent" / n / "Models").resolve()) for n in ("RimVision", "RimWorld3DVR")):
                    document = ET.parse(source).getroot()
                    if any(node.findtext("thingDef", "").strip() == record["defName"] and node.findtext("graphicPathContains", "").strip() == settings.graphic_filter for node in document):
                        destination = source
                        owned_source = True
            if destination.exists() and not settings.overwrite and not owned_source:
                raise ValueError("File already exists; enable Replace existing model file to overwrite it")
            root = ET.Element("Defs")
            model = ET.SubElement(root, "RimVisionModel")
            field(model, "thingDef", record["defName"])
            field(model, "heightOffset", number(settings.height_offset))
            if settings.graphic_filter:
                field(model, "graphicPathContains", settings.graphic_filter)
            vertices = ET.SubElement(model, "vertices")
            faces = ET.SubElement(model, "faces")
            vertex_count = 0
            graph = context.evaluated_depsgraph_get()
            sizes = {}
            textures = {}
            object_surfaces = {}
            for obj in objects:
                evaluated = obj.evaluated_get(graph)
                mesh = evaluated.to_mesh(preserve_all_data_layers=True, depsgraph=graph)
                if mesh is None:
                    raise ValueError(obj.name + ": cannot obtain mesh")
                try:
                    mesh.calc_loop_triangles()
                    points = [evaluated.matrix_world @ vertex.co for vertex in mesh.vertices]
                    for point in points:
                        vertex = ET.SubElement(vertices, "li")
                        field(vertex, "x", number(point.x + record["width"] * 0.5))
                        field(vertex, "height", number(point.z))
                        field(vertex, "z", number(point.y + record["depth"] * 0.5))
                    reverse_winding = evaluated.matrix_world.to_3x3().determinant() > 0
                    normal_matrix = evaluated.matrix_world.to_3x3().inverted().transposed()
                    for triangle in mesh.loop_triangles:
                        loops = list(triangle.loops)
                        if reverse_winding:
                            loops.reverse()
                        positions = [points[mesh.loops[i].vertex_index] for i in loops]
                        if (positions[1] - positions[0]).cross(positions[2] - positions[0]).length_squared <= 1e-16:
                            raise ValueError(obj.name + ": face #" + str(triangle.polygon_index + 1) + " after modifiers has zero or almost zero area. Check overlapping vertices and model scale.")
                        material = mesh.materials[triangle.material_index] if triangle.material_index < len(mesh.materials) else None
                        surface = material.rimvision if material else None
                        from_object = surface is not None and surface.mode == "Object"
                        image, color, uv_name = None, None, ""
                        if from_object:
                            if material.as_pointer() not in object_surfaces:
                                object_surfaces[material.as_pointer()] = object_surface(material)
                            image, color, uv_name = object_surfaces[material.as_pointer()]
                        face = ET.SubElement(faces, "li")
                        field(face, "indices", ",".join(str(vertex_count + mesh.loops[i].vertex_index) for i in loops))
                        normals = [(normal_matrix @ mesh.corner_normals[i].vector).normalized() for i in loops]
                        if any(normal.length_squared < 0.5 for normal in normals):
                            raise ValueError(obj.name + ": invalid corner normal")
                        field(face, "normals", ";".join(number(normal.x) + "," + number(normal.z) + "," + number(normal.y) for normal in normals))
                        field(face, "smooth", str(mesh.polygons[triangle.polygon_index].use_smooth).lower())
                        field(face, "twoSided", str(not material.use_backface_culling if from_object else surface.two_sided if surface else True).lower())
                        if surface and (surface.mode == "Solid" or (from_object and image is None)):
                            field(face, "solidColor", ",".join(number(c) for c in (color if from_object else surface.color)))
                            field(face, "tintWithThing", str(surface.tint).lower())
                        else:
                            direction = surface.direction if surface else "Thing"
                            custom = surface is not None and (surface.mode in ("Custom", "Atlas") or from_object)
                            use_uv = from_object or surface is None or surface.mode == "Atlas" or surface.use_uv
                            uv_layer = mesh.uv_layers.get(uv_name) if uv_name else mesh.uv_layers.active
                            if custom:
                                if from_object:
                                    source = ("Object", image.as_pointer())
                                elif surface.mode == "Atlas":
                                    source = ("Atlas", record["defName"])
                                else:
                                    source = bpy.path.abspath(surface.texture_path)
                                if source not in textures:
                                    textures[source] = prepare_texture(surface, record, image)
                                texture_path, data, size = textures[source]
                                field(face, "texturePath", texture_path)
                                field(face, "tintWithThing", str(surface.tint).lower())
                                if use_uv and uv_layer is None:
                                    raise ValueError(obj.name + ": UV map not found " + (uv_name or "for the texture"))
                            else:
                                field(face, "texture", direction)
                            pending = mesh.attributes.get("rimvision_pending_uv")
                            raw_u = mesh.attributes.get("rimvision_pixel_u")
                            raw_v = mesh.attributes.get("rimvision_pixel_v")
                            preserve_pixels = not custom and pending is not None and raw_u is not None and raw_v is not None and pending.data[triangle.polygon_index].value
                            if preserve_pixels and use_uv:
                                field(face, "uvPoints", ";".join(number(raw_u.data[i].value) + "," + number(raw_v.data[i].value) for i in loops))
                            elif uv_layer and use_uv:
                                if custom:
                                    width, height = size
                                elif direction not in sizes:
                                    path = sprite_path(record, direction)
                                    if not path or not Path(path).is_file():
                                        raise ValueError("Prepare game sprites to export UVs, or disable UV export on the material.")
                                    image = image_for(record, direction)
                                    sizes[direction] = tuple(image.size)
                                if not custom:
                                    width, height = sizes[direction]
                                if width <= 0 or height <= 0:
                                    raise ValueError("Invalid sprite size")
                                coords = [uv_layer.data[i].uv for i in loops]
                                field(face, "uvPoints", ";".join(number(uv.x * width) + "," + number((1 - uv.y) * height) for uv in coords))
                                if not custom:
                                    field(face, "uvTextureSize", number(width) + "," + number(height))
                        if surface:
                            if surface.turret_top:
                                field(face, "turretTop", "true")
                            for prop, tag in (("required_links", "requiredLinks"), ("forbidden_links", "forbiddenLinks")):
                                value = getattr(surface, prop)
                                if value:
                                    if any(p.strip() not in ("North", "East", "South", "West") for p in value.split(",")):
                                        raise ValueError("Links must be North,East,South,West")
                                    field(face, tag, value)
                    vertex_count += len(mesh.vertices)
                finally:
                    evaluated.to_mesh_clear()
            helpers = {child for obj in objects for child in obj.children_recursive if child.get("rimvision_role")}
            cutouts = [obj for obj in helpers if obj.get("rimvision_role") == "ground_cutout"]
            if len(cutouts) > 1:
                raise ValueError("Only one ground cutout may be exported per model")
            for cutout in cutouts:
                if cutout.type != "MESH" or len(cutout.data.polygons) != 1:
                    raise ValueError("Ground cutout must contain one face")
                polygon = cutout.data.polygons[0]
                indices = list(polygon.vertices)
                if cutout.matrix_world.to_3x3().determinant() > 0:
                    indices.reverse()
                field(model, "groundCutout", ",".join(str(vertex_count + i) for i in range(len(indices))))
                for i in indices:
                    point = cutout.matrix_world @ cutout.data.vertices[i].co
                    vertex = ET.SubElement(vertices, "li")
                    field(vertex, "x", number(point.x + record["width"] * 0.5))
                    field(vertex, "height", number(point.z))
                    field(vertex, "z", number(point.y + record["depth"] * 0.5))
                vertex_count += len(indices)
            anchors = sorted((obj for obj in helpers if obj.get("rimvision_role") == "anchor"), key=lambda obj: obj.name)
            if anchors:
                anchor_node = ET.SubElement(model, "anchors")
                effect_kinds = set()
                for obj in anchors:
                    node = ET.fromstring(obj.get("rimvision_anchor_xml", "<li/>"))
                    effect = obj.rimvision_effect
                    if effect.kind in ("fire", "light"):
                        if effect.kind in effect_kinds:
                            raise ValueError("A model may have one fire point and one light point")
                        effect_kinds.add(effect.kind)
                        for key, value in (("name", effect.kind), ("size", number(effect.size)), ("radius", number(effect.radius)),
                                           ("intensity", number(effect.intensity)), ("color", ",".join(number(c) for c in effect.color)),
                                           ("always", str(effect.always).lower())):
                            existing = node.find(key)
                            if existing is None:
                                existing = ET.SubElement(node, key)
                            existing.text = value
                    point = obj.matrix_world.translation
                    for key, coordinate in (("x", point.x + record["width"] * 0.5), ("height", point.z), ("z", point.y + record["depth"] * 0.5)):
                        existing = node.find(key)
                        if existing is None:
                            existing = ET.SubElement(node, key)
                        existing.text = number(coordinate)
                    anchor_node.append(node)
            if not len(faces):
                raise ValueError("No faces to export")
            ET.indent(root, space="  ")
            destination.parent.mkdir(parents=True, exist_ok=True)
            if settings.addon_path and destination.is_file():
                document = ET.parse(destination).getroot()
                matches = [i for i, node in enumerate(document) if node.findtext("thingDef", "").strip() == record["defName"] and node.findtext("graphicPathContains", "").strip() == settings.graphic_filter]
                if matches:
                    index = matches[-1]
                    document.remove(document[index])
                    document.insert(index, model)
                else:
                    document.append(model)
                root = document
                ET.indent(root, space="  ")
            for texture_path, data, size in textures.values():
                target = output_root / "Textures" / (texture_path + ".png")
                target.parent.mkdir(parents=True, exist_ok=True)
                if not target.is_file():
                    target.write_bytes(data)
            if settings.addon_path:
                write_xml(root, destination)
            else:
                with destination.open("wb" if settings.overwrite else "xb") as output:
                    ET.ElementTree(root).write(output, encoding="utf-8", xml_declaration=True)
            for obj in objects:
                obj["rimvision_def"] = record["defName"]
                obj["rimvision_filter"] = settings.graphic_filter
                obj["rimvision_height_offset"] = settings.height_offset
                obj["rimvision_source"] = str(destination)
                attach_to_addon(context, obj)
            if settings.addon_path:
                add_dependency(output_root, record.get("packageId", ""), record.get("mod", ""))
                ensure_content(output_root)
                refresh_addon(settings)
                models = record.setdefault("models", [])
                source = dict(file=str(destination.resolve()), index=list(root).index(model), filter=settings.graphic_filter,
                              mod=settings.addon_name, legacy=destination.resolve().is_relative_to((output_root / "OptionalContent/RimWorld3DVR/Models").resolve()))
                existing = next((i for i, entry in enumerate(models) if Path(entry["file"]).resolve() == destination.resolve() and entry["filter"] == settings.graphic_filter), None)
                if existing is None:
                    models.append(source)
                else:
                    models[existing] = source
                _model_items[record["defName"]] = [(str(i), (entry["filter"] or "Default") + " • " + entry["mod"], entry["file"], i) for i, entry in enumerate(models)]
                if selected(context) is record:
                    settings.model_choice = str(existing if existing is not None else len(models) - 1)
            self.report({"INFO"}, tr("Exported " + str(len(faces)) + " triangles to " + str(destination)))
        except (OSError, ValueError, RuntimeError, ET.ParseError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        return {"FINISHED"}


class RVAddToAddon(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.add_to_addon"
    bl_label = "Add selection to addon collection"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        settings = context.scene.rimvision
        record = selected(context)
        objects = [o for o in context.selected_objects if o.type == "MESH" and not o.get("rimvision_role")]
        if not settings.addon_path or record is None or not objects or context.mode != "OBJECT":
            self.report({"ERROR"}, tr("Open an addon, select a catalog item and meshes in Object Mode"))
            return {"CANCELLED"}
        for obj in objects:
            if not obj.get("rimvision_def"):
                obj["rimvision_def"] = record["defName"]
                obj["rimvision_filter"] = settings.graphic_filter
                obj["rimvision_height_offset"] = settings.height_offset
            attach_to_addon(context, obj)
        return {"FINISHED"}


class RVSaveAddon(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.save_addon"
    bl_label = "Save entire collection"

    def execute(self, context):
        if _pending_meshes:
            attach_new_meshes()
        settings = context.scene.rimvision
        collection = settings.addon_collection
        if context.mode != "OBJECT" or not settings.addon_path or collection is None:
            self.report({"ERROR"}, tr("Open an addon in Object Mode"))
            return {"CANCELLED"}
        groups = {}
        for obj in collection.all_objects:
            if obj.type == "MESH" and obj.get("rimvision_def") and not obj.get("rimvision_role"):
                root = model_root(obj)
                key = (root["rimvision_def"], root.get("rimvision_filter", ""), root.get("rimvision_height_offset", 0.0))
                groups.setdefault(key, []).append(obj)
        if not groups:
            self.report({"ERROR"}, tr("The addon collection has no models"))
            return {"CANCELLED"}
        records = {record["defName"]: record for record in _records}
        if any(key[0] not in records for key in groups):
            self.report({"ERROR"}, tr("Load the game catalog containing all collection model definitions"))
            return {"CANCELLED"}
        targets = [(key[0], key[1]) for key in groups]
        if len(set(targets)) != len(targets):
            self.report({"ERROR"}, tr("One model has different height offsets; save it separately"))
            return {"CANCELLED"}
        previous = (settings.graphic_filter, settings.height_offset)
        saved = 0
        try:
            for (definition, graphic_filter, height), objects in sorted(groups.items()):
                settings.graphic_filter = graphic_filter
                settings.height_offset = height
                if RVExport._export(self, context, records[definition], objects) != {"FINISHED"}:
                    self.report({"WARNING"}, tr("Models saved: " + str(saved) + ". Fix the error and save again."))
                    return {"CANCELLED"}
                saved += 1
        finally:
            settings.graphic_filter, settings.height_offset = previous
        self.report({"INFO"}, tr("Models saved to addon: " + str(saved)))
        return {"FINISHED"}


def draw_catalog(layout, context, rows=7):
    settings = context.scene.rimvision
    layout.prop(settings, "search", text="Search", icon="VIEWZOOM")
    row = layout.row(align=True)
    row.prop(settings, "category", text="Type")
    caption = next((entry.label + (" · disabled" if entry.inactive else "") for entry in settings.mod_entries if entry.name == settings.mod_filter), "All mods")
    row.popover(panel="RIMVISION_PT_mod_filter", text=caption)
    layout.prop(settings, "content_state")
    layout.prop(settings, "model_status")
    layout.template_list("RV_UL_catalog", "", settings, "entries", settings, "index", rows=rows)


def draw_item_card(layout, context):
    settings = context.scene.rimvision
    record = selected(context)
    if record is None:
        layout.label(text="Select a catalog item")
        return
    box = layout.box()
    icon = preview_icon(record)
    if icon:
        row = box.row()
        row.alignment = "CENTER"
        row.template_icon(icon_value=icon, scale=7)
    box.label(text=record.get("label", record["defName"]), icon="OBJECT_DATA")
    box.label(text=record["defName"], translate=False)
    box.label(text=record.get("mod", ""))
    if record.get("inactiveContent"):
        box.label(text="Mod / DLC is disabled in the game", icon="INFO")
    if record.get("previewNote"):
        box.label(text=record["previewNote"], icon="INFO")
    box.label(text=str(record["width"]) + " × " + str(record["depth"]) + " cells")
    builtin = record.get("builtin3d") or ("Door" if record.get("door") else "Wall" if record["defName"] == "Wall" else "")
    if builtin:
        locked = record.get("builtin3dLocked") or record.get("door")
        box.label(text="Built-in 3D: " + builtin, icon="LOCKED" if locked else "MESH_CUBE")
        if locked:
            box.label(text="Model replacement is not supported yet")
            box.label(text="RimVision uses a separate renderer for this geometry")
            box.label(text="XML export does not replace it in the game", icon="INFO")
        else:
            box.label(text="Built-in geometry can be replaced with XML")
            box.label(text="Load the ready-made torch template below" if record["defName"] == "TorchWallLamp" else "Original code-generated geometry cannot be imported")
    if not icon:
        box.label(text="Sprite not found", icon="IMAGE_DATA")
        if record.get("texturePath"):
            box.label(text="Prepare sprites again", icon="INFO")
        else:
            box.label(text="The XML has no path to a separate sprite", icon="INFO")
    if record.get("models"):
        box.label(text="XML model found –  take a look", icon="CHECKMARK")
        box.label(text="Ready models: " + str(len(record["models"])), icon="CHECKMARK")
        box.prop(settings, "model_choice", text="Variant")
        box.operator("rimvision.create_template", text="Add ready-made model", icon="IMPORT")
    else:
        box.label(text="XML model not found")
        box.operator("rimvision.create_template", text="Create model template", icon="ADD")
    row = box.row(align=True)
    if record.get("models"):
        row.operator("rimvision.create_template", text="New model", icon="ADD").force_new = True
    row.operator("rimvision.import_model_file", text="XML…", icon="FILE_FOLDER")


class RVBrowser(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.browse_catalog"
    bl_label = "RimVision model catalog"

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self, width=850, title=tr(self.bl_label))

    def draw(self, context):
        split = ui(self.layout).split(factor=0.65)
        draw_catalog(split.column(), context, 15)
        draw_item_card(split.column(), context)

    def execute(self, context):
        return {"FINISHED"}


def startup_objects(scene):
    settings = scene.rimvision
    result = []
    for collection in scene.collection.children:
        if collection.name != "Collection" or collection.users != 1 or collection in (settings.addon_collection, settings.guides):
            continue
        for obj in collection.objects:
            expected = {"Cube": "MESH", "Camera": "CAMERA", "Light": "LIGHT"}.get(obj.name)
            if obj.type != expected or obj.get("rimvision_def") or obj.get("rimvision_role"):
                continue
            if len(obj.users_collection) != 1 or len(obj.users_scene) != 1 or obj.parent or obj.children or obj.animation_data:
                continue
            if obj.type == "MESH" and (obj.modifiers or len(obj.data.vertices) != 8 or len(obj.data.polygons) != 6 or
                any(abs(abs(c) - 1) > 1e-6 for vertex in obj.data.vertices for c in vertex.co)):
                continue
            result.append(obj)
    return result


class RVAddEffect(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.add_effect"
    bl_label = "Add effect point"
    bl_options = {"REGISTER", "UNDO"}
    kind: EnumProperty(items=translated_items([("fire", "Fire", ""), ("light", "Light", "")]), default=0, translation_context=CONTEXT)

    def execute(self, context):
        record = selected(context)
        root = context.scene.rimvision.current_model
        if context.mode != "OBJECT" or record is None or root is None or root.get("rimvision_def") != record["defName"]:
            self.report({"ERROR"}, tr("Create or load a model in Object Mode first"))
            return {"CANCELLED"}
        try:
            marker = next((obj for obj in root.children_recursive if obj.get("rimvision_role") == "anchor" and obj.rimvision_effect.kind == self.kind), None)
            if marker is None:
                marker = create_effect_marker(context, record, root, self.kind)
                attach_to_addon(context, marker)
            for obj in context.selected_objects:
                obj.select_set(False)
            marker.select_set(True)
            context.view_layer.objects.active = marker
            context.scene.rimvision.effect_options = True
            if self.kind == "light" and context.area and context.area.type == "VIEW_3D":
                context.space_data.shading.type = "MATERIAL"
                context.space_data.shading.use_scene_lights = True
        except (OSError, ValueError, RuntimeError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        return {"FINISHED"}


class RVSelectEffect(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.select_effect"
    bl_label = "Select effect point"
    object_name: StringProperty(translation_context=CONTEXT)

    @classmethod
    def poll(cls, context):
        return context.mode == "OBJECT"

    def execute(self, context):
        obj = context.scene.objects.get(self.object_name)
        if obj is None or context.view_layer.objects.get(obj.name) is None:
            return {"CANCELLED"}
        for selected_obj in context.selected_objects:
            selected_obj.select_set(False)
        obj.select_set(True)
        context.view_layer.objects.active = obj
        return {"FINISHED"}


class RVStartWork(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.start_work"
    bl_label = "Start working"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return context.mode == "OBJECT"

    def execute(self, context):
        for obj in startup_objects(context.scene):
            obj.select_set(False, view_layer=context.view_layer)
            if context.view_layer.objects.active == obj:
                context.view_layer.objects.active = None
            obj.hide_set(True, view_layer=context.view_layer)
            obj.hide_render = True
        context.view_layer.active_layer_collection = context.view_layer.layer_collection
        context.scene.rimvision.workflow_stage = "Setup"
        context.scene.rimvision.setup_options = True
        return {"FINISHED"}


class RVBackToSetup(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.back_to_setup"
    bl_label = "Back to game setup"

    def execute(self, context):
        context.scene.rimvision.workflow_stage = "Setup"
        return {"FINISHED"}


class RVClearStartup(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.clear_startup"
    bl_label = "Remove cube, light and camera"
    bl_description = "Remove startup objects and the empty default collection; undo with Ctrl+Z"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return context.mode == "OBJECT"

    def execute(self, context):
        settings = context.scene.rimvision
        removed = 0
        for collection in list(context.scene.collection.children):
            if collection.name != "Collection" or collection.users != 1 or collection in (settings.addon_collection, settings.guides):
                continue
            for obj in startup_objects(context.scene):
                if collection.objects.get(obj.name) is None:
                    continue
                data, kind = obj.data, obj.type
                bpy.data.objects.remove(obj, do_unlink=True)
                if data.users == 0:
                    getattr(bpy.data, {"MESH": "meshes", "CAMERA": "cameras", "LIGHT": "lights"}[kind]).remove(data)
                removed += 1
            if not collection.objects and not collection.children:
                bpy.data.collections.remove(collection)
        context.view_layer.update()
        reset_mesh_tracking()
        self.report({"INFO"}, tr("Startup objects removed: " + str(removed)))
        return {"FINISHED"}


class RVPreviewMaterial(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.preview_material"
    bl_label = "Update scene material"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        record = selected(context)
        obj = context.object
        if record is None or obj is None or obj.type != "MESH" or obj.active_material is None:
            return {"CANCELLED"}
        try:
            material_preview(obj.active_material, record)
        except (OSError, RuntimeError, ValueError) as error:
            self.report({"ERROR"}, tr(str(error)))
            return {"CANCELLED"}
        return {"FINISHED"}


class RVCleanResources(bpy.types.Operator):
    bl_translation_context = CONTEXT
    bl_idname = "rimvision.clean_resources"
    bl_label = "Clean up RimVision resources"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        paths, old_materials = {}, {}
        definitions = {record["defName"]: record for record in _records}
        for record in _records:
            for direction in ("North", "East", "South", "West"):
                for prefix in ("sprite", "baseSprite"):
                    path = record.get(prefix + direction)
                    if path:
                        paths.setdefault(str(Path(path).resolve()).casefold(), (record, direction))
            for suffix in ("surface", "game texture", "solid", "Thing", "North", "East", "South", "West"):
                old_materials.setdefault(record["defName"] + " " + suffix, record)
        for material in bpy.data.materials:
            record = old_materials.get(re.sub(r"\.\d+$", "", material.name))
            if record is not None and material.library is None and material.rimvision.mode != "Object":
                name_resource(material, record, "Material")
            definition = material.get("rimvision_resource_def")
            record = definitions.get(definition)
            if record is not None:
                name_material(material, record)
            if definition and material.use_nodes:
                if record:
                    for node in material.node_tree.nodes:
                        if (node.type == "TEX_IMAGE" and node.image and node.image.get("rimvision_atlas") and
                            (node.image.get("rimvision_auto_name") or node.image.name.startswith("RimVision atlas "))):
                            name_resource(node.image, record, "Atlas 2×2")
        for image in bpy.data.images:
            if image.library is not None or not image.filepath:
                continue
            entry = paths.get(str(Path(bpy.path.abspath(image.filepath)).resolve()).casefold())
            if entry and re.fullmatch(r"[a-f0-9]{64}\.png(?:\.\d+)?", image.name):
                record, direction = entry
                actual = next((key for key, value in record.get("directionalSprites", {}).items()
                               if str(Path(value).resolve()).casefold() == str(Path(bpy.path.abspath(image.filepath)).resolve()).casefold()), "")
                kind = {"North": "North", "South": "South", "East": "East", "West": "West"}.get(actual, "Sprite")
                name_resource(image, record, kind)
        removed_materials, removed_images = 0, 0
        for material in list(bpy.data.materials):
            if material.get("rimvision_auto_name") == material.name and material.users == 0 and not material.use_fake_user and material.library is None:
                bpy.data.materials.remove(material)
                removed_materials += 1
        for image in list(bpy.data.images):
            managed = image.get("rimvision_auto_name") == image.name or (image.get("rimvision_atlas") and image.name.startswith("RimVision atlas "))
            if managed and image.users == 0 and not image.use_fake_user and not image.is_dirty and image.library is None:
                bpy.data.images.remove(image)
                removed_images += 1
        self.report({"INFO"}, tr("Unused materials removed: " + str(removed_materials) + "; images: " + str(removed_images)))
        return {"FINISHED"}


def foldout(layout, settings, prop, title):
    box = layout.box()
    expanded = getattr(settings, prop)
    box.prop(settings, prop, text=title, icon="TRIA_DOWN" if expanded else "TRIA_RIGHT", emboss=False, toggle=True)
    return box.column() if expanded else None


def draw_setup(layout, context):
    settings = context.scene.rimvision
    setup = layout
    game_found = bool(settings.game and (Path(bpy.path.abspath(settings.game)) / "Data/Core").is_dir())
    setup.enabled = _extracting is None
    workshop_found = bool(settings.workshop and Path(bpy.path.abspath(settings.workshop)).is_dir())
    config_found = bool(settings.config and Path(bpy.path.abspath(settings.config)).is_file())
    setup.label(text="Game found" if game_found else "Game not found –  select a path", icon="CHECKMARK" if game_found else "ERROR")
    setup.label(text="Workshop found" if workshop_found else "Workshop not found", icon="CHECKMARK" if workshop_found else "INFO")
    setup.label(text="Active mod list found" if config_found else "Active mod list not found", icon="CHECKMARK" if config_found else "INFO")
    setup.label(text="Catalog loaded: " + str(len(_records)) + " items" if _records else "Catalog is not loaded yet", icon="CHECKMARK" if _records else "INFO")
    sprites_ready = settings.sprites_ready and settings.sprite_status_context == sprite_status_context(settings)
    if _extracting and _extracting._scene == context.scene:
        setup.label(text="Preparing sprites…", icon="TIME")
    else:
        setup.label(text="Sprites prepared: " + str(settings.sprite_count) if sprites_ready else "Sprites are not prepared for the current mods", icon="CHECKMARK" if sprites_ready else "INFO")
    setup.operator("rimvision.find_game", icon="VIEWZOOM")
    setup.operator("rimvision.prepare_sprites", icon="IMAGE_DATA")
    setup.prop(settings, "advanced", icon="PREFERENCES")
    if settings.advanced:
        for prop in ("game", "workshop", "model_root", "config", "version", "active_only"):
            setup.prop(settings, prop)
        setup.operator("rimvision.load_catalog", text="Rescan folders", icon="FILE_REFRESH").source = "Files"


def draw_addon_tools(layout, context):
    settings = context.scene.rimvision
    addon = layout
    row = addon.row(align=True)
    row.operator("rimvision.create_addon", icon="ADD")
    row.operator("rimvision.open_addon", icon="FILE_FOLDER")
    addon.operator("rimvision.find_addons", icon="FILE_REFRESH")
    addon.label(text=settings.addon_discovery_status)
    if settings.found_addons:
        addon.template_list("RV_UL_found_addons", "", settings, "found_addons", settings, "found_addon_index", rows=3)
        if 0 <= settings.found_addon_index < len(settings.found_addons):
            found = settings.found_addons[settings.found_addon_index]
            addon.label(text=found.origin + ": " + found.path, icon="FILE_FOLDER")
        addon.operator("rimvision.open_found_addon", icon="IMPORT")
    if settings.addon_path:
        addon.label(text=settings.addon_name, translate=False)
        addon.prop(settings, "addon_metadata_options", icon="PREFERENCES")
        if settings.addon_metadata_options:
            addon.prop(settings, "addon_edit_name")
            addon.operator("rimvision.edit_addon_description", icon="TEXT")
            addon.prop(settings, "addon_preview")
            preview = bpy.path.abspath(settings.addon_preview) if settings.addon_preview else ""
            if preview and Path(preview).is_file():
                try:
                    if preview not in _previews:
                        _previews.load(preview, preview, "IMAGE")
                    addon.template_icon(icon_value=_previews[preview].icon_id, scale=6)
                except (OSError, RuntimeError, KeyError):
                    addon.label(text="Could not display the preview", icon="ERROR")
            addon.operator("rimvision.save_addon_metadata", icon="FILE_TICK")
        addon.label(text=bpy.path.abspath(settings.addon_path), icon="FILE_FOLDER")


def draw_addon_models(layout, context):
    settings = context.scene.rimvision
    layout.label(text=settings.addon_name, icon="OUTLINER_COLLECTION", translate=False)
    layout.template_list("RV_UL_addon", "", settings, "addon_models", settings, "addon_index", rows=4)
    row = layout.row(align=True)
    row.operator("rimvision.load_addon_model", text="Load model", icon="IMPORT")
    row.operator("rimvision.refresh_addon", text="", icon="FILE_REFRESH")
    layout.operator("rimvision.add_to_addon", text="Add selection to collection", icon="ADD")
    layout.operator("rimvision.save_addon", icon="EXPORT")


class RVPanel(bpy.types.Panel):
    bl_translation_context = CONTEXT
    bl_label = "RimVision Models"
    bl_idname = "RIMVISION_PT_models"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "RimVision"

    def draw(self, context):
        layout = ui(self.layout)
        settings = context.scene.rimvision
        language_preferences = preferences()
        if language_preferences:
            layout.prop(language_preferences, "language")
        if settings.workflow_stage == "Auto" and not settings.addon_path and not settings.current_model and not any(obj.get("rimvision_def") for obj in context.scene.objects) and startup_objects(context.scene):
            layout.label(text="The cube, camera and light will be hidden", icon="INFO")
            row = layout.row()
            row.scale_y = 1.6
            row.operator("rimvision.start_work", icon="PLAY")
            if context.mode != "OBJECT":
                layout.label(text="Switch to Object Mode", icon="INFO")
            return
        if settings.workflow_stage == "Setup":
            layout.label(text="1. Game and sprites", icon="VIEWZOOM")
            draw_setup(layout.column(), context)
            layout.label(text=settings.status)
            if _extracting:
                layout.label(text="Esc –  cancel extraction")
            return
        if settings.workflow_stage == "Addons":
            layout.label(text="2. Select or create an addon", icon="OUTLINER_COLLECTION")
            draw_addon_tools(layout, context)
            layout.operator("rimvision.back_to_setup", icon="BACK")
            return
        game_found = bool(settings.game and (Path(bpy.path.abspath(settings.game)) / "Data/Core").is_dir())
        layout.label(text="Game found" if game_found else "Game not found", icon="CHECKMARK" if game_found else "ERROR")
        record = selected(context)
        if record:
            layout.label(text=record.get("label", record["defName"]), icon="OBJECT_DATA")
        layout.label(text=settings.status)
        if _extracting:
            layout.label(text="Esc –  cancel extraction")
        setup = foldout(layout, settings, "setup_options", "Game and paths")
        if setup is not None:
            draw_setup(setup, context)
        addon = foldout(layout, settings, "addon_options", "Addon: " + settings.addon_name if settings.addon_path else "Model addon")
        if addon is not None:
            draw_addon_tools(addon, context)
        if settings.addon_path:
            addon_models = foldout(layout, settings, "addon_models_options", "Addon models (" + str(len(settings.addon_models)) + ")")
            if addon_models is not None:
                draw_addon_models(addon_models, context)
        catalog = foldout(layout, settings, "catalog_options", "Catalog (" + str(len(_records)) + ")")
        if catalog is not None:
            catalog.operator("rimvision.browse_catalog", text="Open full catalog", icon="VIEWZOOM")
            draw_catalog(catalog, context, rows=4)
        model = foldout(layout, settings, "model_options", "Current model")
        if model is not None:
            model.operator("rimvision.clear_startup", icon="TRASH")
            draw_item_card(model, context)
            model.prop(settings, "auto_attach_meshes")
            if settings.current_model:
                model.label(text="Current model: " + settings.current_model.name, icon="MESH_DATA")
            else:
                model.label(text="Create or load a model to use Shift+A", icon="INFO")
        material = context.object.active_material if context.object and context.object.type == "MESH" else None
        box = foldout(layout, settings, "surface_options", "Materials and textures")
        if box is not None:
            box.operator("rimvision.clean_resources", icon="BRUSH_DATA")
            if material is None:
                box.label(text="Select a mesh with a material", icon="INFO")
            else:
                box.label(text=material.name, translate=False)
                surface = material.rimvision
                box.prop(surface, "mode")
                if surface.mode == "Solid":
                    box.prop(surface, "color")
                    box.prop(surface, "tint")
                elif surface.mode == "Custom":
                    box.prop(surface, "texture_path")
                    box.prop(surface, "use_uv")
                    box.prop(surface, "tint")
                    box.label(text="The image will be saved to the addon as PNG")
                elif surface.mode == "Object":
                    box.label(text="Color and image from the material Base Color")
                    box.label(text="Uses the texture UV map")
                    box.prop(surface, "tint")
                elif surface.mode == "Atlas":
                    box.label(text="One 2×2 texture for all views")
                    box.label(text="Top: North | East")
                    box.label(text="Bottom: South | West")
                    box.label(text="Place UVs on the desired tiles")
                    box.label(text="The atlas will be saved to the addon as PNG")
                    box.prop(surface, "tint")
                else:
                    box.prop(surface, "direction")
                    box.prop(surface, "use_uv")
                    box.operator("rimvision.use_sprite")
                for prop in ("two_sided", "turret_top", "required_links", "forbidden_links"):
                    if surface.mode == "Object" and prop == "two_sided":
                        continue
                    box.prop(surface, prop)
                if surface.mode != "Object":
                    box.operator("rimvision.preview_material")
        effects = foldout(layout, settings, "effect_options", "Fire and light")
        if effects is not None:
            row = effects.row(align=True)
            row.enabled = settings.current_model is not None
            row.operator("rimvision.add_effect", text="🔥 Fire", icon="MOD_FLUIDSIM").kind = "fire"
            row.operator("rimvision.add_effect", text="💡 Light", icon="LIGHT_DATA").kind = "light"
            if settings.current_model is None:
                effects.label(text="Create or load a model first", icon="INFO")
            if settings.current_model:
                for marker in settings.current_model.children_recursive:
                    if marker.get("rimvision_role") == "anchor" and marker.rimvision_effect.kind in ("fire", "light"):
                        effects.operator("rimvision.select_effect", text=marker.name, icon="LIGHT_DATA" if marker.rimvision_effect.kind == "light" else "MOD_FLUIDSIM").object_name = marker.name
            marker = context.object
            if marker and marker.get("rimvision_role") == "anchor" and marker.rimvision_effect.kind in ("fire", "light"):
                effect = marker.rimvision_effect
                effects.prop(marker, "location", text="Position")
                if effect.kind == "fire":
                    effects.prop(effect, "size")
                    effects.prop(effect, "always")
                else:
                    effects.prop(effect, "color")
                    effects.prop(effect, "intensity")
                    effects.prop(effect, "radius")
                    effects.label(text="The in-game item must have a light source", icon="INFO")
                effects.label(text="Move the point with G; delete with X")
        reference = foldout(layout, settings, "reference_options", "Scene references")
        if reference is not None:
            reference.prop(settings, "auto_reference")
            reference.prop(settings, "reference_all_sides")
            manual = reference.column()
            manual.enabled = not settings.reference_all_sides
            manual.prop(settings, "reference_direction", text="View")
            manual.prop(settings, "reference_side", text="Side")
            manual.prop(settings, "reference_rotation", text="XYZ rotation")
            reference.prop(settings, "reference_gap")
            reference.prop(settings, "reference_offset", text="Offset")
            reference.prop(settings, "reference_opacity", slider=True)
            reference.operator("rimvision.show_reference")
        output = foldout(layout, settings, "export_options", "Save and export")
        if output is not None:
            if settings.addon_path:
                output.label(text="Addon: " + settings.addon_name)
            else:
                output.prop(settings, "output", text="Mod folder")
            if settings.advanced or settings.graphic_filter or settings.height_offset:
                output.prop(settings, "graphic_filter", text="Graphic path filter")
                output.prop(settings, "height_offset", text="Height offset")
            output.prop(settings, "overwrite", text="Replace existing XML")
            output.operator("rimvision.export_model", text="Save model to addon" if settings.addon_path else "Export model", icon="EXPORT")


_classes = (RVPreferences, RVEntry, RVAddonModel, RVFoundAddon, RVModEntry, RVEffectSettings, RVSettings, RVSurface, RV_UL_catalog, RV_UL_mod_filter, RVModFilterPanel, RV_UL_addon, RV_UL_found_addons, RVFindAddons, RVOpenFoundAddon, RVLoad, RVFind, RVPrepareSprites, RVReference,
            RVImport, RVImportFile, RVTemplate, RVTexture, RVCreateAddon, RVOpenAddon, RVRefreshAddon, RVLoadAddonModel,
            RVEditAddonDescription, RVSaveAddonMetadata, RVExport, RVAddToAddon, RVSaveAddon, RVBrowser, RVAddEffect, RVSelectEffect, RVStartWork, RVBackToSetup, RVClearStartup, RVPreviewMaterial, RVCleanResources, RVPanel)


def initial_paths():
    scenes = getattr(bpy.data, "scenes", None)
    if scenes is None:
        return 0.1
    reset_mesh_tracking()
    for scene in scenes:
        if not scene.rimvision.get("rimvision_filters_v2", False):
            if scene.rimvision.get("models_only", False):
                scene.rimvision.model_status = "Ready"
            elif scene.rimvision.get("missing_models_only", False):
                scene.rimvision.model_status = "Missing"
            scene.rimvision["rimvision_filters_v2"] = True
        if scene.rimvision.addon_path and scene.rimvision.addon_description is None:
            try:
                load_addon_metadata(scene.rimvision)
            except (OSError, ValueError, ET.ParseError, RuntimeError) as error:
                scene.rimvision.status = str(error)
        if not scene.rimvision.game:
            try:
                locate(scene.rimvision)
            except (OSError, ValueError) as error:
                scene.rimvision.status = str(error)
        try:
            refresh_found_addons(scene.rimvision)
        except (OSError, ValueError, ET.ParseError, RuntimeError) as error:
            scene.rimvision.addon_discovery_status = str(error)
        try:
            refresh_sprite_status(scene.rimvision)
        except (OSError, ValueError, ET.ParseError, RuntimeError) as error:
            set_sprite_status(scene.rimvision, {}, {})
            print("[RimVision] Sprites: " + str(error))


def register():
    global _previews
    for cls in _classes:
        bpy.utils.register_class(cls)
    i18n.register()
    bpy.types.Scene.rimvision = PointerProperty(type=RVSettings, translation_context=CONTEXT)
    bpy.types.Material.rimvision = PointerProperty(type=RVSurface, translation_context=CONTEXT)
    bpy.types.Object.rimvision_effect = PointerProperty(type=RVEffectSettings, translation_context=CONTEXT)
    _previews = bpy.utils.previews.new()
    _pending_meshes.clear()
    _known_meshes.clear()
    bpy.app.handlers.depsgraph_update_post.append(track_new_meshes)
    for handlers in (bpy.app.handlers.undo_pre, bpy.app.handlers.redo_pre):
        handlers.append(begin_scene_restore)
    for handlers in (bpy.app.handlers.undo_post, bpy.app.handlers.redo_post, bpy.app.handlers.load_post):
        handlers.append(finish_scene_restore)
    bpy.app.timers.register(initial_paths, first_interval=0.0)


def unregister():
    global _previews
    i18n.unregister()
    if track_new_meshes in bpy.app.handlers.depsgraph_update_post:
        bpy.app.handlers.depsgraph_update_post.remove(track_new_meshes)
    for handlers in (bpy.app.handlers.undo_pre, bpy.app.handlers.redo_pre):
        if begin_scene_restore in handlers:
            handlers.remove(begin_scene_restore)
    for handlers in (bpy.app.handlers.undo_post, bpy.app.handlers.redo_post, bpy.app.handlers.load_post):
        if finish_scene_restore in handlers:
            handlers.remove(finish_scene_restore)
    if bpy.app.timers.is_registered(attach_new_meshes):
        bpy.app.timers.unregister(attach_new_meshes)
    _pending_meshes.clear()
    _known_meshes.clear()
    if bpy.app.timers.is_registered(initial_paths):
        bpy.app.timers.unregister(initial_paths)
    if _extracting:
        _extracting.cancel(bpy.context)
    del bpy.types.Material.rimvision
    del bpy.types.Object.rimvision_effect
    del bpy.types.Scene.rimvision
    for cls in reversed(_classes):
        bpy.utils.unregister_class(cls)
    bpy.utils.previews.remove(_previews)
    _previews = None
    _records.clear()
