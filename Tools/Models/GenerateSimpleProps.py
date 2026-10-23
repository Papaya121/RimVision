"""Generate authored low-poly props in Rim3D's existing optional model format.

Run from any directory with Python 3. No Unity assets, textures or game Defs are
generated. Existing hand-authored models and all plants remain untouched.
"""
from pathlib import Path
import math
import os
import re
import xml.etree.ElementTree as ET
from GenerateRecognizableProps import BUILDERS as AUTHORED_BUILDINGS
from GenerateModelRepairs import REPAIRS
AUTHORED_BUILDINGS = set(AUTHORED_BUILDINGS) | REPAIRS

ROOT = Path(__file__).resolve().parents[2]
MODELS = ROOT / 'Mods/RimVision/OptionalContent/RimWorld3DVR/Models'
FILE = 'Rim3D_SimpleProps.xml'
WOOD = (.40, .25, .13)
METAL = (.37, .41, .44)
DARK = (.16, .19, .21)
LIGHT = (.72, .75, .73)
FABRIC = (.65, .62, .53)
STONE = (.54, .52, .47)
GOLD = (.82, .65, .20)


class Mesh:
    def __init__(self, name, width, depth):
        self.name, self.width, self.depth = name, width, depth
        self.vertices, self.faces = [], []
        self.vertex_indices = {}

    def face(self, points, color=METAL, tint=False, artwork=False):
        indices = []
        for point in points:
            key = tuple(round(v, 5) for v in point)
            if key not in self.vertex_indices:
                self.vertex_indices[key] = len(self.vertices)
                self.vertices.append(point)
            indices.append(self.vertex_indices[key])
        self.faces.append((indices, color, tint, artwork))

    def box(self, x, y, z, width, height, depth, color=METAL, tint=False, artwork=False):
        a, b, c, d = x-width/2, x+width/2, z-depth/2, z+depth/2
        lo, hi = y, y+height
        # Outward winding, including the underside for placement outlines.
        for points in (
            [(a,lo,c),(a,hi,c),(b,hi,c),(b,lo,c)],
            [(b,lo,d),(b,hi,d),(a,hi,d),(a,lo,d)],
            [(a,lo,d),(a,hi,d),(a,hi,c),(a,lo,c)],
            [(b,lo,c),(b,hi,c),(b,hi,d),(b,lo,d)],
            [(a,lo,c),(b,lo,c),(b,lo,d),(a,lo,d)],
        ): self.face(points, color, tint)
        self.face([(a,hi,c),(a,hi,d),(b,hi,d),(b,hi,c)], color, tint, artwork)

    def lathe(self, x, z, rings, color=METAL, tint=False, sides=8, stretch=1):
        vertices = [[(x+math.cos(i*math.tau/sides)*r, y,
                      z+math.sin(i*math.tau/sides)*r*stretch)
                     for i in range(sides)] for y, r in rings]
        for lower, upper in zip(vertices, vertices[1:]):
            for i in range(sides):
                j = (i+1) % sides
                self.face([lower[i],upper[i],upper[j],lower[j]], color, tint)
        self.face(vertices[0], color, tint)
        self.face(list(reversed(vertices[-1])), color, tint)

    def decal(self, x, y, z, width, depth):
        self.face([(x-width/2,y,z-depth/2),(x-width/2,y,z+depth/2),
                   (x+width/2,y,z+depth/2),(x+width/2,y,z-depth/2)], artwork=True)

    def legs(self, width, depth, height, color=WOOD):
        for x in (-width*.38,width*.38):
            for z in (-depth*.38,depth*.38):
                self.box(x,0,z,.10,height,.10,color,True)

    def write(self, root):
        node = ET.SubElement(root, 'RimWorld3DVR.Render.Rim3DModelDef')
        ET.SubElement(node,'defName').text = 'Rim3D_Simple_' + self.name
        ET.SubElement(node,'thingDef').text = self.name
        ET.SubElement(node,'aiGenerated').text = 'false'
        verts = ET.SubElement(node,'vertices')
        for x,y,z in self.vertices:
            li=ET.SubElement(verts,'li')
            for key,value in [('x',x+self.width/2),('height',y),('z',z+self.depth/2)]:
                ET.SubElement(li,key).text = f'{value:.5f}'
        faces=ET.SubElement(node,'faces')
        for indices,color,tint,artwork in self.faces:
            li=ET.SubElement(faces,'li')
            ET.SubElement(li,'indices').text = ','.join(map(str,indices))
            ET.SubElement(li,'twoSided').text = 'false'
            if not artwork:
                ET.SubElement(li,'solidColor').text = ','.join(f'{c:.3f}' for c in color)
                if tint: ET.SubElement(li,'tintWithThing').text = 'true'


# Explicit assignments keep floor markers, linked walls, doors and vegetation
# on their dedicated renderers. Multiple defs share a silhouette only when their
# physical function is similar; each model uses the actual footprint from Defs.
BUILDINGS = {}
def group(profile, names):
    for name in names.split(): BUILDINGS[name] = profile

group('urn', 'Urn Burnbong IncenseShrine Autobong')
group('bedroll', 'Bedroll BedrollDouble AnimalBed KneelSheet KneelPillow')
group('crib', 'Crib AnimalSleepingBox')
group('couch', 'Couch Pew')
group('board', 'Blackboard')
group('lamp', 'WallLamp BabyDecoration SanguphageMeetingTorch DarktorchFungus')
group('boardgame', 'GameOfUrBoard')
group('brewery', 'Brewery')
group('switch', 'PowerSwitch BroadshieldProjector')
group('cell', 'VanometricPowerCell')
group('wreck', 'ShipChunk ShuttleCrashed ShuttleCrashed_Exitable ShuttleCrashed_Exitable_Mechanitor ChunkMechanoidSlag')
group('geyser', 'SteamGeyser')
group('cocoon', 'CocoonMegaspider CocoonMegascarab DryadCocoon DryadHealingPod GaumakerCocoon')
group('vat', 'GrowthVat GeneExtractor DeathrestCasket BiosculpterPod')
group('pump', 'Hemopump HemogenAmplifier GlucosoidPump PsychofluidPump DeathrestAccelerator PollutionPump SleepAccelerator')
group('console', 'GeneAssembler GeneProcessor SubcoreEncoder SubcoreSoftscanner SubcoreRipscanner')
group('cabinet', 'GeneBank')
group('generator', 'ToxifierGenerator WastepackAtomizer MechBooster')
group('antenna', 'BandNode BurnoutMechlinkBooster MechbandDish')
group('gestator', 'MechGestator LargeMechGestator')
group('recharger', 'BasicRecharger StandardRecharger NeuralSupercharger')
group('altar', 'Altar_Small Altar_Medium Altar_Large Altar_Grand NatureShrine_Small NatureShrine_Large')
group('lectern', 'Lectern')
group('reliquary', 'Reliquary')
group('cage', 'GibbetCage')
group('speaker', 'Loudspeaker')
group('drum', 'Drum')
group('ball', 'LightBall')
group('flag', 'SacrificialFlag Drape')
group('pyre', 'Pyre Effigy')
group('spike', 'Skullspike SculptureTerror')
group('stone', 'AnimusStone')
group('piano', 'Piano Harpsichord')
group('harp', 'Harp')
group('conditioner', 'SmokeSpewer WeatherController ClimateAdjuster PsychicDroner')
group('cube', 'DirtCubeSculpture StoneCubeSculpture SandCubeSculpture')


def building(m, profile):
    w,d = m.width*.86,m.depth*.86
    if profile == 'urn':
        r=min(w,d)*.39
        m.lathe(0,0,[(0,r*.65),(.15,r*.9),(.48,r),(.75,r*.65),(.88,r*.55)],STONE,True)
        m.lathe(0,0,[(.86,r*.6),(.93,r*.6)],DARK)
    elif profile == 'bedroll':
        h=.14 if 'Sheet' in m.name else .23
        m.box(0,0,0,w,h,d,FABRIC,True)
        m.box(0,h,-d*.32,w*.75,.10,d*.24,LIGHT,True)
        m.decal(0,h+.003,0,w,d)
    elif profile == 'crib':
        m.legs(w,d,.25)
        m.box(0,.23,0,w,.10,d,WOOD,True)
        m.box(0,.33,0,w*.8,.12,d*.8,FABRIC,True)
        for x in (-w/2,w/2):
            m.box(x,.69,0,.06,.06,d,WOOD,True)
            for z in (-d*.43,-d*.14,d*.14,d*.43):m.box(x,.34,z,.05,.35,.05,WOOD,True)
        for z in (-d/2,d/2):m.box(0,.34,z,w,.42,.07,WOOD,True)
    elif profile == 'couch':
        m.legs(w,d,.20)
        m.box(0,.18,0,w,.30,d,FABRIC,True)
        m.box(0,.48,d*.39,w,.55,d*.22,FABRIC,True)
        for x in (-w*.46,w*.46):m.box(x,.35,0,w*.08,.28,d,FABRIC,True)
        m.decal(0,.483,0,w*.80,d*.70)
    elif profile == 'board':
        for x in (-w*.35,w*.35):m.box(x,0,0,.10,1.55,.10,WOOD,True)
        m.box(0,.62,0,w,1.00,.12,WOOD,True)
        m.box(0,.69,-.067,w*.92,.86,.014,(.13,.23,.20))
        m.box(0,.60,-.15,w,.06,.25,WOOD,True)
    elif profile in ('lamp','switch','cell'):
        if m.name=='WallLamp':
            m.box(0,1.22,.43,.28,.34,.08,DARK)
            m.box(0,1.28,.35,.24,.24,.14,LIGHT)
            return
        h=.13 if profile=='switch' else .7
        m.box(0,0,0,w*.7,h,d*.7,METAL,True,True)
        if profile!='switch':m.lathe(0,0,[(h,.18),(h+.18,.22),(h+.30,.08)],LIGHT)
    elif profile == 'boardgame':
        m.legs(w,d,.52)
        m.box(0,.50,0,w,.10,d,WOOD,True,True)
    elif profile == 'brewery':
        m.legs(w,d,.50)
        m.box(0,.48,0,w,.15,d,WOOD,True,True)
        for x in (-w*.3,w*.3):
            m.lathe(x,0,[(.63,.25),(.74,.31),(1.03,.31),(1.10,.25)],WOOD)
            for y in (.73,1.00):m.lathe(x,0,[(y,.315),(y+.035,.315)],METAL)
    elif profile == 'wreck':
        m.box(-w*.15,0,0,w*.6,.45,d*.7,DARK)
        m.lathe(w*.22,-d*.15,[(0,d*.24),(.30,d*.31),(.65,d*.16)],METAL,sides=6,stretch=.75)
        m.box(-w*.1,.43,d*.12,w*.78,.09,d*.38,METAL,artwork=True)
        m.box(w*.33,.15,d*.20,w*.12,.53,d*.24,DARK)
    elif profile == 'geyser':
        m.lathe(0,0,[(.01,.78),(.14,.66),(.20,.45),(.12,.28)],STONE,sides=10)
        m.lathe(0,0,[(.121,.28),(.122,.28)],DARK)
    elif profile == 'cocoon':
        m.lathe(0,0,[(0,.23),(.15,.39),(.55,.37),(.86,.20),(.94,.03)],(.55,.47,.31),sides=8)
    elif profile in ('vat','gestator'):
        m.box(0,0,0,w,.23,d,DARK)
        if profile=='vat':
            r=min(w,d)*.4
            m.lathe(0,0,[(.23,r),(.37,r*1.03),(1.10,r*1.03),(1.33,r*.8)],(.38,.54,.49),stretch=min(1.7,d/w))
            m.lathe(0,0,[(1.28,r*.82),(1.42,r*.82)],METAL)
        else:
            m.box(0,.23,0,w*.70,.42,d*.80,METAL,artwork=True)
            for x in (-w*.42,w*.42):m.box(x,.23,0,w*.13,1.1,d*.85,METAL)
            m.box(0,1.30,0,w,.13,d*.25,METAL)
    elif profile == 'pump':
        m.box(0,0,0,w*.90,.16,d*.85,DARK)
        m.lathe(0,-d*.12,[(.16,w*.29),(.82,w*.29),(.96,w*.20)],METAL)
        m.box(w*.27,.28,d*.22,w*.23,.46,d*.34,LIGHT,artwork=True)
    elif profile in ('console','cabinet','generator','conditioner'):
        h=1.45 if profile=='cabinet' else 1.1 if profile=='conditioner' else .83
        m.box(0,0,0,w,.12,d,DARK)
        m.box(0,.12,0,w*.91,h,d*.89,METAL,True,True)
        if profile=='console':
            m.box(0,h+.12,d*.26,w*.72,.48,d*.15,DARK)
            m.box(0,h+.17,d*.175,w*.61,.33,.018,(.24,.49,.48))
        elif profile=='cabinet':
            for x in (-w*.23,w*.23):m.box(x,.24,-d*.455,w*.40,h*.79,.025,DARK)
        else:
            for x in (-w*.25,w*.25):m.lathe(x,0,[(h+.12,w*.13),(h+.48,w*.13),(h+.53,w*.16)],DARK)
    elif profile == 'antenna':
        m.box(0,0,0,w*.75,.28,d*.75,DARK)
        m.box(0,.28,0,.18,1.2,.18,METAL)
        if 'Dish' in m.name:
            m.lathe(0,0,[(1.2,.15),(1.35,min(w,d)*.43),(1.43,min(w,d)*.43)],LIGHT)
        else:
            for y in (.9,1.3):m.box(0,y,0,w*.80,.07,.07,METAL)
    elif profile == 'recharger':
        m.box(0,0,0,w,.12,d,DARK,artwork=True)
        m.box(0,.12,d*.36,w,.60,d*.22,METAL)
        for x in (-w*.38,w*.38):m.box(x,.12,0,.15,.40,d*.80,METAL)
    elif profile in ('altar','lectern','reliquary'):
        m.box(0,0,0,w,.14,d,STONE,True)
        m.box(0,.14,0,w*.75,.57,d*.70,WOOD,True)
        m.box(0,.71,0,w,.12,d,WOOD,True,True)
        if profile=='reliquary':
            for x in (-w*.43,w*.43):
                for z in (-d*.4,d*.4):m.box(x,.83,z,.08,.68,.08,GOLD)
            m.box(0,1.49,0,w,.08,d,GOLD)
        elif profile=='lectern':m.box(0,.83,d*.36,w*.85,.30,.08,WOOD,True)
    elif profile == 'cage':
        m.box(0,.04,0,w,.10,d,DARK)
        m.box(0,1.34,0,w,.10,d,DARK)
        for x in (-w*.45,0,w*.45):
            for z in (-d*.45,d*.45):m.box(x,.14,z,.045,1.20,.045,METAL)
        for z in (-d*.15,d*.15):
            for x in (-w*.45,w*.45):m.box(x,.14,z,.045,1.20,.045,METAL)
    elif profile == 'speaker':
        m.box(0,0,0,w*.6,1.05,d*.6,DARK)
        for y in (.30,.73):m.box(0,y,-d*.305,w*.43,.30,.015,METAL)
    elif profile in ('drum','ball'):
        if profile=='drum':
            m.lathe(0,0,[(0,.28),(.6,.35),(.65,.35)],WOOD)
            m.lathe(0,0,[(.65,.36),(.70,.36)],FABRIC)
        else:
            m.lathe(0,0,[(0,.2),(.05,.2),(.08,.06),(.70,.06)],METAL)
            m.lathe(0,0,[(.70,.04),(.80,.24),(1.02,.32),(1.25,.24),(1.33,.04)],LIGHT)
    elif profile == 'flag':
        for x in (-w*.43,w*.43):m.box(x,0,0,.06,1.65,.06,WOOD)
        m.box(0,.65,0,w*.86,.95,.025,FABRIC,True)
        m.box(0,1.61,0,w,.06,.06,WOOD)
    elif profile == 'pyre':
        for z in (-d*.28,0,d*.28):m.box(0,.04,z,w,.15,.16,WOOD)
        for x in (-w*.30,w*.30):m.box(x,.19,0,.17,.16,d,WOOD)
        if m.name=='Effigy':
            m.box(0,.35,0,.22,1.25,.18,WOOD)
            m.box(0,1.05,0,w*.8,.10,.12,WOOD)
    elif profile == 'spike':
        m.box(0,0,0,w*.65,.12,d*.65,STONE,True)
        m.lathe(0,0,[(.12,.08),(1.20,.07),(1.40,.001)],WOOD,sides=6)
        m.lathe(0,0,[(1.02,.16),(1.18,.24),(1.38,.15)],LIGHT,sides=6)
    elif profile == 'stone':
        m.lathe(0,0,[(0,.34),(.35,.44),(.9,.25),(1.15,.03)],STONE,True,sides=7)
    elif profile == 'piano':
        m.legs(w,d,.65,DARK)
        m.box(0,.63,d*.10,w,.32,d*.72,DARK)
        m.box(0,.66,-d*.36,w*.82,.09,d*.2,LIGHT,artwork=True)
        m.box(0,.95,d*.16,w,.08,d*.68,DARK)
    elif profile == 'harp':
        m.box(0,0,0,w*.8,.12,d*.6,WOOD)
        for x in (-w*.32,w*.32):m.box(x,.12,0,.08,1.18,.12,WOOD)
        m.box(0,1.26,0,w*.75,.09,.12,WOOD)
        for x in (-w*.22,-w*.11,0,w*.11,w*.22):m.box(x,.18,0,.012,1.08,.012,GOLD)
    elif profile == 'cube':
        m.box(0,0,0,w*.8,.15,d*.8,STONE)
        m.box(0,.15,0,w*.64,.65,d*.64,STONE,True,True)
    else: raise ValueError(profile)


def main():
    covered=set()
    for path in MODELS.rglob('*.xml'):
        if path.name==FILE:continue
        covered.update(n.text for n in ET.parse(path).iter('thingDef'))
    data = Path(os.environ.get('RIMWORLD_DATA', 'D:/SteamLibrary/steamapps/common/RimWorld/Data'))
    if not (data / 'Core/Defs').is_dir():
        raise FileNotFoundError('Set RIMWORLD_DATA to RimWorld/Data before generating models')
    parents={}; defs=[]
    for path in sorted((data).glob('*/Defs/**/*.xml')):
        for node in ET.parse(path).getroot():
            if node.tag!='ThingDef':continue
            if node.get('Name'):parents[node.get('Name')]=node
            if node.findtext('defName'):defs.append((path.relative_to(data).parts[0],node))
    def value(node,key,default=''):
        found=node.findtext(key)
        if found is not None:return found
        parent=parents.get(node.get('ParentName'))
        return value(parent,key,default) if parent is not None else default
    documents={}; counts={}; names=set()
    # Preserve individually authored replacements when regenerating simple props.
    for path in MODELS.rglob(FILE):
        for node in ET.parse(path).getroot():
            if node.findtext('thingDef') in AUTHORED_BUILDINGS:
                documents.setdefault(path.parent.name,ET.Element('Defs')).append(node)
    for dlc,node in defs:
        name=node.findtext('defName')
        if name in covered or name in names or name in AUTHORED_BUILDINGS:continue
        category=value(node,'category')
        profile=BUILDINGS.get(name) if category=='Building' else None
        if profile is None:continue
        if category=='Building' and value(node,'useHitPoints','true')=='false' and value(node,'altitudeLayer') in ('Floor','FloorEmplacement'):continue
        dims=[float(n) for n in re.findall(r'[\d.]+',value(node,'size','(1,1)'))]
        mesh=Mesh(name,*dims)
        building(mesh,profile)
        mesh.write(documents.setdefault(dlc,ET.Element('Defs')))
        names.add(name); counts[category]=counts.get(category,0)+1
    for dlc,root in documents.items():
        ET.indent(root,space='  ')
        ET.ElementTree(root).write(MODELS/dlc/FILE,encoding='utf-8',xml_declaration=True)
    print('Generated:',counts)
    print('Defs:',', '.join(sorted(names)))


if __name__=='__main__':main()
