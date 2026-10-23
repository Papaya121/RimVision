"""Author the requested props as actual low-poly parts, never sprite decals.

Run with Python 3. Uses the checked-in UV swatches from the original game art;
does not export or redistribute game textures. Unrelated model nodes are kept.
"""
from pathlib import Path
import json
import math
import re
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
MODELS = ROOT / 'Mods/RimVision/OptionalContent/RimWorld3DVR/Models'
MATERIALS = Path(__file__).with_name('RecognizablePropsMaterials.json')
BUILDERS = {
    'ToxifierGenerator': 'toxifier',
    'ArchotechTower': 'archtower', 'ChristmasTree': 'tree',
    'PsychicEmanator': 'emanator',
    'AncientFuelNode': 'fuel', 'AncientGenerator': 'generator',
    'AncientGiantWheel': 'wheel', 'Defoliator': 'defoliator',
    'SmokeSpewer': 'smoke', 'ClimateAdjuster': 'climate',
    'AncientLamppost': 'lamppost',
    'MajorArchotechStructure': 'archmajor',
    'MajorArchotechStructureStudiable': 'archmajor',
    'GrandArchotechStructure': 'archgrand', 'ArchonexusCore': 'archcore',
    'VanometricPowerCell': 'vanometric', 'EMIDynamo': 'emi',
    'BroadshieldProjector': 'shield', 'AncientMechDropBeacon': 'beacon',
    'AncientMegaCannonTripod': 'tripod', 'Harpsichord': 'harpsichord',
    'Spacedrone': 'drone', 'InfiniteChemreactor': 'chemreactor',
    'Piano': 'piano', 'AncientPipes': 'pipes', 'BurningPowerCell': 'cell',
    'AncientRazorWire': 'wire', 'WatermillGenerator': 'watermill',
    'Hopper': 'hopper', 'NeuralSupercharger': 'neural',
}
ACCENTS = {
    'glow': (.43, .78, .73), 'gold': (.88, .88, .23),
    'keywhite': (.87, .87, .83), 'keyblack': (.075, .08, .075),
    'red': (.63, .13, .09), 'blue': (.18, .32, .68),
    'yellow': (.93, .73, .15),
}


def add(a, b): return tuple(x+y for x, y in zip(a, b))
def sub(a, b): return tuple(x-y for x, y in zip(a, b))
def mul(a, s): return tuple(x*s for x in a)
def cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def dot(a, b): return sum(x*y for x, y in zip(a, b))
def unit(a): return mul(a, 1/math.sqrt(dot(a, a)))


class Mesh:
    def __init__(self, name, style, path_filter=''):
        self.name, self.style, self.path_filter = name, style, path_filter
        self.w, self.d = style['size']
        self.vertices, self.faces, self.lookup = [], [], {}
        self.links = ''

    def face(self, points, material='metal', uv=None):
        # Each part samples only its own metal/wood/fabric region of the sprite.
        # No face is ever mapped to the complete source image.
        indices = []
        for p in points:
            key = tuple(round(v, 6) for v in p)
            if key not in self.lookup:
                self.lookup[key] = len(self.vertices)
                self.vertices.append(key)
            indices.append(self.lookup[key])
        self.faces.append((indices, material, uv, self.links))

    def box(self, x, y, z, w, h, d, material='metal', yaw=0):
        a, b, c, e = -w/2, w/2, -d/2, d/2
        angle = math.radians(yaw)
        def p(xx, yy, zz):
            return (x+xx*math.cos(angle)+zz*math.sin(angle),
                    y+yy, z-xx*math.sin(angle)+zz*math.cos(angle))
        for points in (
            [(a,0,c),(a,h,c),(b,h,c),(b,0,c)],
            [(b,0,e),(b,h,e),(a,h,e),(a,0,e)],
            [(a,0,e),(a,h,e),(a,h,c),(a,0,c)],
            [(b,0,c),(b,h,c),(b,h,e),(b,0,e)],
            [(a,0,c),(b,0,c),(b,0,e),(a,0,e)],
            [(a,h,c),(a,h,e),(b,h,e),(b,h,c)],
        ): self.face([p(*q) for q in points], material)

    def lathe(self, x, z, profile, material='metal', sides=12, stretch=1,
              cap_bottom=True, cap_top=True, uv_radii=None, uv_center=None):
        rings = [[(x+r*math.cos(i*math.tau/sides), y,
                   z+r*math.sin(i*math.tau/sides)*stretch)
                  for i in range(sides)] for y, r in profile]
        def uvs(ring, indices):
            if uv_radii is None: return None
            cx, cy, rx, ry = uv_center
            r = uv_radii[ring]
            return [(cx+rx*r*math.cos(i*math.tau/sides),
                     cy-ry*r*math.sin(i*math.tau/sides)) for i in indices]
        for k, (lower, upper) in enumerate(zip(rings, rings[1:])):
            for i in range(sides):
                j = (i+1) % sides
                uv = None if uv_radii is None else uvs(k, [i])+uvs(k+1, [i,j])+uvs(k, [j])
                self.face([lower[i],upper[i],upper[j],lower[j]], material, uv)
        if cap_bottom: self.face(rings[0], material, uvs(0, list(range(sides))))
        if cap_top:
            ids = list(reversed(range(sides)))
            self.face([rings[-1][i] for i in ids], material, uvs(len(rings)-1, ids))

    def tube(self, a, b, radius, material='metal', sides=8, hollow=False):
        axis = unit(sub(b, a))
        ref = (0,1,0) if abs(axis[1]) < .9 else (1,0,0)
        u, v = unit(cross(axis, ref)), None
        v = cross(axis, u)
        def ring(p, r):
            return [add(p, add(mul(u,r*math.cos(i*math.tau/sides)),
                               mul(v,r*math.sin(i*math.tau/sides)))) for i in range(sides)]
        ra, rb = ring(a,radius), ring(b,radius)
        for i in range(sides):
            j=(i+1)%sides
            self.face([ra[i],ra[j],rb[j],rb[i]], material)
        if not hollow:
            self.face(list(reversed(ra)),material); self.face(rb,material)
        else:
            # Genuine open ends, wall thickness and dark inner bore.
            ia, ib = ring(a,radius*.73), ring(b,radius*.73)
            for i in range(sides):
                j=(i+1)%sides
                self.face([ra[j],ra[i],ia[i],ia[j]],material)
                self.face([rb[i],rb[j],ib[j],ib[i]],material)
                self.face([ia[j],ia[i],ib[i],ib[j]],'dark')

    def pipe(self, points, radius=.06, material='copper', sides=6):
        for a,b in zip(points,points[1:]): self.tube(a,b,radius,material,sides)

    def ball(self, x, y, z, r, material='metal', sides=10, bands=6):
        # Keep the tiny polar rings nonzero so every exported face has a normal.
        profile=[(y+r*math.sin(-math.pi/2+i*math.pi/bands),
                  max(.005,r*math.cos(-math.pi/2+i*math.pi/bands))) for i in range(bands+1)]
        self.lathe(x,z,profile,material,sides)

    def dome(self, x, y, z, r, material='metal', sides=12):
        self.lathe(x,z,[(y,r),(y+r*.48,r*.87),(y+r*.82,r*.55),(y+r,.015)],material,sides)

    def plate(self, x, y, z, w, h, d, material='dark', bevel=.15):
        c=min(w,d)*bevel
        poly=[(-w/2+c,-d/2),(w/2-c,-d/2),(w/2,-d/2+c),(w/2,d/2-c),
              (w/2-c,d/2),(-w/2+c,d/2),(-w/2,d/2-c),(-w/2,-d/2+c)]
        self.extrude([(xx+x,zz+z) for xx,zz in poly], y,y+h,material)

    def extrude(self, polygon, low, high, material='metal'):
        # Polygon is counterclockwise in x/z. Triangulate concave piano outlines.
        if sum(polygon[i][0]*polygon[(i+1)%len(polygon)][1]-
               polygon[(i+1)%len(polygon)][0]*polygon[i][1] for i in range(len(polygon))) < 0:
            polygon=list(reversed(polygon))
        for i,(x,z) in enumerate(polygon):
            xx,zz=polygon[(i+1)%len(polygon)]
            self.face([(x,low,z),(x,high,z),(xx,high,zz),(xx,low,zz)],material)
        ids=list(range(len(polygon)))
        def orient(a,b,c):return (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])
        while len(ids)>3:
            for i in range(len(ids)):
                a,b,c=ids[i-1],ids[i],ids[(i+1)%len(ids)]
                if orient(polygon[a],polygon[b],polygon[c])<=1e-8:continue
                if any(all(orient(polygon[u],polygon[v],polygon[p])>=-1e-8
                           for u,v in ((a,b),(b,c),(c,a))) for p in ids if p not in (a,b,c)):continue
                self.face([(polygon[k][0],high,polygon[k][1]) for k in (c,b,a)],material)
                self.face([(polygon[k][0],low,polygon[k][1]) for k in (a,b,c)],material)
                ids.pop(i);break
            else:raise ValueError('Non-simple authored footprint: '+self.name)
        self.face([(polygon[k][0],high,polygon[k][1]) for k in reversed(ids)],material)
        self.face([(polygon[k][0],low,polygon[k][1]) for k in ids],material)

    def vent(self, x, y, z, w, h, count=5):
        self.box(x,y,z,w,h,.055,'dark')
        for i in range(count):
            self.box(x,y+(i+.5)*h/count,z-.034,w*.9,.035,.025,'light')

    def feet(self, w, d, h, material='dark'):
        for x,z in ((-w*.39,-d*.38),(w*.39,-d*.38),(0,d*.36)):
            self.box(x,.04,z,.13,h,.14,material)

    def node(self, def_name=None):
        node=ET.Element('RimWorld3DVR.Render.Rim3DModelDef')
        ET.SubElement(node,'defName').text=def_name or 'RimVision_Recognizable_'+self.name
        ET.SubElement(node,'thingDef').text=self.name
        if self.path_filter:ET.SubElement(node,'graphicPathContains').text=self.path_filter
        node.append(ET.Comment(' Authored component geometry; local texture swatches, no full-sprite decals. '))
        verts=ET.SubElement(node,'vertices')
        for x,y,z in self.vertices:
            li=ET.SubElement(verts,'li')
            for key,value in (('x',x+self.w/2),('height',y),('z',z+self.d/2)):
                ET.SubElement(li,key).text=f'{value:.6f}'
        faces=ET.SubElement(node,'faces')
        for indices,material,uv,links in self.faces:
            li=ET.SubElement(faces,'li')
            ET.SubElement(li,'indices').text=','.join(map(str,indices))
            ET.SubElement(li,'twoSided').text='false'
            if links:
                ET.SubElement(li,'forbiddenLinks' if links.startswith('!') else 'requiredLinks').text=links.lstrip('!')
            if material in ACCENTS:
                ET.SubElement(li,'solidColor').text=','.join(map(str,ACCENTS[material]))
            else:
                ET.SubElement(li,'texture').text=self.style['direction']
                if uv is not None:
                    ET.SubElement(li,'uvPoints').text=';'.join(f'{u:.4f},{v:.4f}' for u,v in uv)
                else:
                    ET.SubElement(li,'uvPixels').text=','.join(map(str,self.style['patches'][material]))
        return node


def machine_base(m):
    m.plate(0,.02,0,m.w*.91,.23,m.d*.91,'dark',.07)
    m.plate(0,.25,0,m.w*.85,.16,m.d*.85,'metal',.06)


def build_neural(m):
    # Reclined treatment chair: cushion, backrest, head hoop, control pedestal.
    m.plate(0,.03,0,.87,.16,2.7,'dark')
    for x in (-.33,.33):m.box(x,.19,-.10,.09,.16,1.95,'metal')
    m.plate(0,.35,-.22,.65,.12,1.7,'dark',.08)
    m.plate(0,.47,-.23,.57,.07,1.58,'light',.11)
    m.box(0,.54,-.88,.56,.07,.28,'metal')
    # Reclined upholstered back, instead of a vertical grey slab.
    m.face([(-.29,.54,.39),(-.29,1.13,.94),(.29,1.13,.94),(.29,.54,.39)],'dark')
    m.face([(-.29,.49,.43),(.29,.49,.43),(.29,1.08,.98),(-.29,1.08,.98)],'metal')
    for x in (-.29,.29):
        m.face([(x,1.08,.98),(x,1.13,.94),(x,.54,.39),(x,.49,.43)] if x<0 else
               [(x,.49,.43),(x,.54,.39),(x,1.13,.94),(x,1.08,.98)],'metal')
    m.face([(-.29,1.13,.94),(-.29,1.08,.98),(.29,1.08,.98),(.29,1.13,.94)],'metal')
    m.plate(0,1.12,.86,.38,.07,.23,'dark')
    for x in (-.32,.32):
        for z in (-.57,.20):m.tube((x,.22,z),(x,.63,z),.028,'metal',6)
        m.box(x,.61,-.18,.085,.075,1.03,'metal')
        m.box(x,.685,-.12,.052,.025,.42,'glow')
    # Head halo is an actual arch with electrodes on its inside.
    halo=[(.40*math.cos(math.pi-i*math.pi/10),1.00+.40*math.sin(math.pi-i*math.pi/10),.94) for i in range(11)]
    m.pipe(halo,.052,'metal',6)
    for x in (-.28,0,.28):m.ball(x,1.27 if x else 1.36,.94,.055,'glow',6,4)
    m.box(0,.19,1.24,.74,.75,.29,'light')
    m.box(0,.86,1.07,.31,.12,.045,'dark')
    m.box(0,.885,1.041,.23,.06,.012,'glow')
    for x in (-.23,.23):m.box(x,.95,1.19,.06,.02,.09,'dark')


def build_toxic(m):
    machine_base(m)
    # Two long chemical reservoirs and paired auxiliary tanks.
    for x in (0,1.65):
        m.tube((x,1.18,-1.65),(x,1.18,1.60),.64,'metal',12)
        for z in (-1.15,1.10):
            m.tube((x,1.18,z-.065),(x,1.18,z+.065),.675,'dark',12)
        m.box(x,.41,-.95,.95,.31,.20,'dark')
        m.box(x,.41,.95,.95,.31,.20,'dark')
    for z in (-.94,.94):
        m.lathe(-1.67,z,[(.41,.48),(.52,.53),(1.30,.53),(1.43,.42)],'metal',10)
    for x in (0,1.65):m.pipe([(x,1.78,1.12),(x,2.02,1.12),(x-.35,2.02,1.12),(x-.35,2.02,-1.2)],.075)
    for x in (-1.6,-.45,.70):m.vent(x,.50,-2.08,.80,.50,5)
    m.box(1.78,.41,-1.98,1.02,.73,.50,'metal')
    m.box(1.78,.80,-2.246,.63,.24,.025,'dark')
    m.box(1.78,.84,-2.265,.30,.035,.015,'glow')
    m.pipe([(-1.67,.75,-1.65),(-1.67,.75,-1.9),(1.7,.75,-1.9),(1.7,.65,-1.45)],.075)


def build_toxifier(m):
    machine_base(m)
    m.lathe(0,0,[(.41,.62),(.58,.72),(.82,.72),(1.30,.42),(1.48,.29)],'metal',12)
    m.lathe(0,0,[(1.48,.29),(1.63,.29),(1.63,.20),(1.28,.20)],'dark',12,cap_top=False)
    for i in range(8):
        t=i*math.tau/8
        m.lathe(.64*math.cos(t),.64*math.sin(t),[(.66,.045),(.69,.045)],'light',6)
    for x in (-.71,.71):
        m.box(x,.41,.44,.32,.65,.58,'metal')
        m.vent(x,.59,.13,.24,.32,4)
    m.pipe([(-.70,.47,-.55),(-.70,.85,-.55),(0,.85,-.55)],.055,'copper')


def build_psychic(m):
    machine_base(m)
    m.lathe(0,0,[(.41,1.9),(.57,1.9),(.62,1.75)],'dark',16)
    m.dome(0,.62,0,1.78,'green',20)
    for x in (-2.0,2.0):
        for z in (-1.27,1.27):m.box(x,.41,z,.60,.50,1.23,'green')
    for x in (-.8,.8):
        for z in (-2.0,2.0):m.box(x,.41,z,1.24,.48,.55,'green')
    for i in range(10):
        t=i*math.tau/10
        m.pipe([(2.12*math.cos(t),.46,2.12*math.sin(t)),
                (1.64*math.cos(t),1.04,1.64*math.sin(t))],.045,'green')
    for y,r in ((1.20,1.68),(1.29,1.63),(1.38,1.57)):
        pts=[(r*math.cos(t),y,r*math.sin(t)) for t in [math.pi*1.18+i*.045 for i in range(13)]]
        m.pipe(pts,.018,'gold',4)


def build_emanator(m):
    m.lathe(0,0,[(.01,.24),(.06,.30),(.14,.22)],'dark',8)
    for x,z in ((-.28,-.25),(.28,-.25),(0,.32)):
        m.tube((x,.04,z),(0,.68,0),.035,'green',6)
    m.tube((0,.13,0),(0,1.18,0),.045,'metal',8)
    # Two opposed curved receiver wings on a thin central mast.
    for sign in (-1,1):
        poly=[(sign*.06,-.10),(sign*.36,-.16),(sign*.40,.15),(sign*.13,.11)]
        m.extrude(poly,.86,1.31,'green')
        for y in (.95,1.08,1.20):m.box(sign*.24,y,-.168,.27,.013,.014,'dark')


def build_fuel(m):
    m.plate(0,.015,0,.68,.10,.70,'metal',.08)
    m.lathe(0,0,[(.115,.24),(.20,.29),(.58,.29),(.68,.24)],'rust',12)
    for y in (.20,.51):m.lathe(0,0,[(y,.302),(y+.055,.302)],'metal',12)
    m.dome(0,.68,0,.24,'light',12)
    m.box(.21,.35,-.21,.09,.14,.065,'dark')
    m.pipe([(.15,.115,.24),(.27,.29,.25),(.27,.44,.21)],.024,'copper')


def build_generator(m):
    m.plate(0,.025,0,1.78,.16,1.8,'dark',.04)
    # Horizontal engine barrel with a round end plate and visible bolt circle.
    m.tube((-.39,.76,-.67),(-.39,.76,.67),.53,'metal',12)
    for z in (-.57,.57):m.tube((-.39,.76,z-.035),(-.39,.76,z+.035),.56,'dark',12)
    for i in range(8):
        t=i*math.tau/8
        m.ball(-.39+.41*math.cos(t),.76+.41*math.sin(t),-.704,.026,'light',6,4)
    m.box(-.39,.19,0,.76,.17,1.4,'metal')
    m.lathe(.57,.14,[(.19,.24),(.27,.30),(1.06,.30),(1.20,.22)],'metal',10)
    for y in (.39,.56,.73,.9):m.lathe(.57,.14,[(y,.325),(y+.035,.325)],'light',10)
    m.box(.57,.22,-.59,.47,.33,.32,'metal')
    m.vent(.57,.28,-.757,.36,.18,3)
    m.pipe([(.16,.59,.36),(.34,.59,.36),(.34,.34,-.43)],.055,'rust')


def build_wheel(m):
    # Same recessed rim treatment as the approved small wheel, scaled to 2 cells.
    cx,cy,rx,ry=m.style['wheel_uv']
    variant=m.style['sprite'][-1]
    broken=variant in 'CEF'
    profile=[(.02,.72),(.10,.85),(.46,.85),(.53,.77),(.53,.59),(.28,.50),(.28,.18),(.38,.15)]
    radii=[.86,.97,.97,.90,.71,.60,.22,.18]
    if variant=='C':
        profile=profile[:5]+[(.34,.52),(.02,.52)]
        radii=radii[:5]+[.64,.64]
    elif broken:
        profile += [(.38,.105),(.02,.105)]
        radii += [.14,.14]
    m.lathe(0,0,profile,'metal',24,cap_bottom=not broken,cap_top=not broken,
            uv_radii=radii,uv_center=(cx,cy,rx,ry))
    # Side walls use local metal swatches; top UVs follow only the wheel's rim/dish.
    for i in range(24):
        idx=i
        indices,mat,uv,links=m.faces[idx]
        m.faces[idx]=(indices,mat,None,links)
        idx=24+i
        indices,mat,uv,links=m.faces[idx]
        m.faces[idx]=(indices,mat,None,links)
    if broken:
        # Bottom annulus and inner bore are opaque metal, with a real opening.
        y0,r0=profile[0];yn,rn=profile[-1]
        for i in range(24):
            a=i*math.tau/24;b=(i+1)*math.tau/24
            m.face([(r0*math.cos(a),y0,r0*math.sin(a)),
                    (r0*math.cos(b),y0,r0*math.sin(b)),
                    (rn*math.cos(b),yn,rn*math.sin(b)),
                    (rn*math.cos(a),yn,rn*math.sin(a))],'dark')
        for idx in range((len(profile)-2)*24,(len(profile)-1)*24):
            indices,mat,uv,links=m.faces[idx]
            m.faces[idx]=(indices,'dark',None,links)


def build_defoliator(m):
    machine_base(m)
    m.plate(0,.41,.35,2.22,.36,1.8,'metal',.12)
    for x in (-.66,.66):
        m.lathe(x,.54,[(.77,.59),(.88,.65),(1.07,.65),(1.15,.56)],'metal',12)
        m.lathe(x,.54,[(1.151,.19),(1.18,.19)],'dark',10)
        for i in range(6):
            t=i*math.tau/6
            m.box(x+.40*math.cos(t),1.157,.54+.40*math.sin(t),.12,.035,.12,'light',yaw=i*60)
    m.box(0,.41,-.82,2.16,.94,.88,'metal')
    m.box(-.30,.73,-1.274,.88,.40,.04,'dark')
    for i in range(3):m.box(-.30,.79+i*.095,-1.302,.56,.027,.015,'yellow')
    m.box(.69,.91,-1.28,.17,.14,.03,'green')
    for x in (-1.20,1.20):m.pipe([(x,.43,-.62),(x,.81,-.62),(x,.81,.20)],.095,'dark')


def build_smoke(m):
    machine_base(m)
    for x,z in ((-1.45,.54),(.20,-.75)):
        m.lathe(x,z,[(.41,.81),(.57,.88),(1.26,.88),(1.48,.68)],'metal',12)
        m.lathe(x,z,[(1.48,.50),(1.53,.50)],'light',12)
    m.lathe(1.45,1.20,[(.41,.37),(1.45,.37),(1.60,.30)],'metal',10)
    m.lathe(1.45,1.20,[(1.60,.22),(2.1,.22),(2.1,.15),(1.6,.15)],'dark',10,cap_top=False)
    m.pipe([(-1.7,1.53,.9),(-1.7,1.93,.9),(1.24,1.93,.9),(1.24,1.8,-.75),(.2,1.8,-.75)],.09)
    m.pipe([(-1.45,1.13,-.2),(-1.45,1.13,-1.60),(1.96,1.13,-1.60),(1.96,1.13,1.3)],.09)
    for z in (-1.2,0,1.2):
        m.box(2.05,.42,z,.72,.72,.78,'metal')
        m.vent(2.05,.66,z-.4,.54,.3,4)
    m.box(.45,.41,-1.92,.9,.43,.5,'dark')
    m.box(.45,.66,-2.181,.55,.1,.018,'glow')


def build_climate(m):
    machine_base(m)
    m.plate(.25,.41,0,3.10,.35,3.48,'metal',.09)
    m.lathe(.45,.15,[(.76,1.50),(.87,1.50)],'dark',16)
    # A domed heat exchanger, with actual alternating metal/copper meridians.
    before=len(m.faces)
    m.dome(.45,.87,.15,1.43,'cyan',16)
    for i in range(before,len(m.faces)-2):
        ids,mat,uv,links=m.faces[i]
        if (i-before)%16 in (1,5,9,13):m.faces[i]=(ids,'copper',uv,links)
    m.lathe(-1.75,1.69,[(.41,.32),(1.70,.32),(1.74,.23)],'metal',10)
    m.lathe(-1.75,1.69,[(1.74,.24),(1.91,.24)],'rust',10)
    m.dome(1.71,.41,1.9,.44,'metal',10)
    for x in (-1.65,.0,1.66):
        m.box(x,.41,-2.18,1.25,.58,.65,'metal')
        m.vent(x,.59,-2.514,.94,.31,5)
    for x in (-.66,.65):m.vent(x,.63,2.33,.85,.49,6)
    m.pipe([(-1.75,.81,1.69),(-1.75,.81,.14),(-.65,.81,.14)],.085,'metal')


def build_lamppost(m):
    m.lathe(0,0,[(.01,.30),(.10,.30),(.18,.24),(.27,.15)],'metal',10)
    # Broken pole and empty light fitting, with a recognisable curved neck.
    m.tube((0,.27,0),(.025,1.82,.025),.065,'metal',8)
    m.pipe([(.025,1.82,.025),(.025,2.05,.025),(.025,2.16,-.20),(.025,2.16,-.45)],.055,'metal',6)
    m.plate(.025,2.08,-.49,.27,.10,.34,'dark',.10)
    m.box(.025,2.057,-.49,.17,.025,.24,'light')
    m.box(.065,.39,-.07,.06,.23,.025,'rust')


def arch_column(m,x,z,w,d,h):
    m.plate(x,.30,z,w,.22,d,'dark',.05)
    m.plate(x,.52,z,w*.90,h*.82,d*.90,'green',.08)
    m.plate(x,.52+h*.82,z,w*.74,h*.18,d*.79,'light',.06)
    # Raised circuit traces wrap from front to side; they have their own volume.
    front=z-d*.45-.014
    m.pipe([(x-w*.27,.83,front),(x+w*.04,.83,front),
            (x+w*.04,.52+h*.48,front),(x+w*.25,.52+h*.48,front),
            (x+w*.25,.52+h*.75,front)],.019,'gold',4)
    m.box(x-w*.25,.95,front,.045,.045,.026,'gold')
    for yy in (.85,1.00,1.15):m.box(x-w*.27,yy,front,w*.18,.015,.023,'gold')
    for xx,yy in ((-.22,.18),(.20,.54),(-.18,.68)):
        m.box(x+w*xx,.52+h*yy,front+.003,w*.16,h*.085,.034,'metal')


def build_arch(m,kind):
    m.plate(0,.02,0,m.w*.93,.28,m.d*.93,'dark',.055)
    if kind=='archtower':
        arch_column(m,0,.2,2.80,2.75,4.3)
        arch_column(m,-.92,-.53,.82,.88,2.6)
        arch_column(m,.95,.68,.60,.79,1.9)
    elif kind=='archmajor':
        arch_column(m,.39,.7,2.66,2.34,4.8)
        arch_column(m,-1.7,-.55,1.34,2.36,2.7)
        arch_column(m,1.8,-.7,1.40,1.70,2.2)
        arch_column(m,0,-1.7,1.76,1.48,1.55)
        m.plate(.39,5.24,.7,1.65,.35,1.43,'light',.10)
    elif kind=='archgrand':
        arch_column(m,0,.25,3.48,3.26,6.50)
        arch_column(m,-1.1,-1.10,1.94,1.58,3.80)
        arch_column(m,1.14,-.40,1.70,1.56,4.80)
        m.lathe(0,.35,[(7.03,.60),(7.24,.70),(7.36,.60)],'light',12)
        for x in (-3.52,3.52):
            for z in (-3.52,3.52):
                m.plate(x,.30,z,.98,.28,.98,'metal',.08)
                m.box(x,.58,z,.55,1.13,.55,'green')
                m.box(x,1.71,z,.61,.12,.61,'light')
    else:
        # The core is a massive, asymmetric assembly, not a scaled tower.
        for x,z,w,d,h in ((.30,.70,4.20,4.08,7.4),(-2.50,-.1,1.73,3.4,4.5),
                           (2.78,-.55,1.8,2.9,5.5),(-.90,-2.71,2.3,1.44,3.2),
                           (.97,-1.4,2.6,1.80,4.2),(-1.05,2.83,2.15,1.53,6.1)):
            arch_column(m,x,z,w,d,h)
        for x in (-4.38,4.38):
            for z in (-4.38,4.38):m.plate(x,.30,z,1.05,.42,1.05,'metal',.10)
        m.lathe(.30,.70,[(7.92,.80),(8.04,.86),(8.24,.76)],'light',16)


def build_vanometric(m):
    m.plate(0,.02,0,.84,.17,1.81,'dark',.09)
    m.box(0,.19,0,.77,.11,1.7,'metal')
    # Glowing horizontal chamber in an open protective cage.
    m.tube((0,.60,-.59),(0,.60,.59),.19,'glow',12)
    for z in (-.69,.69):
        m.tube((0,.60,z-.065),(0,.60,z+.065),.27,'metal',12)
        m.box(0,.30,z,.59,.27,.24,'dark')
    for x in (-.32,.32):
        for z in (-.67,.67):m.box(x,.30,z,.07,.58,.09,'metal')
        m.box(x,.30,0,.07,.07,1.60,'metal')
        m.box(x,.88,0,.08,.055,1.68,'metal')
    for z in (-.46,0,.46):
        m.tube((-.29,.88,z),(.29,.88,z),.022,'metal',6)
    for x in (-.17,0,.17):m.box(x,.305,-.80,.05,.02,.08,'gold')


def build_emi(m):
    machine_base(m)
    m.tube((-1.05,1.07,-1.19),(-1.05,1.07,1.13),.60,'metal',12)
    for z in (-.89,-.60,-.31,-.02,.27,.56,.85):
        m.tube((-1.05,1.07,z-.035),(-1.05,1.07,z+.035),.68,'light',12)
    m.box(-1.05,.41,0,1.06,.30,2.65,'dark')
    m.lathe(1.16,.13,[(.41,.63),(.65,.63),(.75,.45)],'metal',12)
    m.lathe(1.16,.13,[(.75,.28),(2.33,.28)],'cyan',12)
    for y in (.86,1.11,1.36,1.61,1.86,2.11):
        m.lathe(1.16,.13,[(y,.40),(y+.075,.40)],'metal',12)
    m.ball(1.16,2.52,.13,.49,'metal',12,6)
    for x in (.87,1.45):m.tube((x,.68,.13),(x,2.17,.13),.035,'dark',6)
    m.pipe([(-.49,.75,-.88),(.24,.75,-.88),(.24,.58,-.88),(.92,.58,-.88),(1.16,.84,-.32)],.08,'metal')
    for x in (-.68,.10):m.vent(x,.48,-1.57,.58,.30,4)


def build_shield(m):
    m.lathe(0,0,[(.02,.40),(.09,.47),(.18,.40)],'dark',12)
    m.dome(0,.18,0,.38,'green',12)
    # Protective meridians around the emitter, rather than a top-down sphere image.
    for i in range(6):
        t=i*math.tau/6
        pts=[(.385*math.cos(a)*math.cos(t),.18+.385*math.sin(a),.385*math.cos(a)*math.sin(t))
             for a in (0,.3,.6,.9,1.2,1.50)]
        m.pipe(pts,.017,'dark',4)
    m.box(0,.10,-.40,.25,.28,.21,'metal')
    m.box(0,.30,-.512,.13,.035,.017,'glow')


def build_beacon(m):
    m.lathe(0,0,[(.015,.35),(.085,.39),(.22,.31),(.32,.24)],'metal',12)
    m.lathe(0,0,[(.321,.23),(.33,.23)],'dark',12)
    m.lathe(0,0,[(.33,.10),(.52,.085)],'green',8)
    m.tube((-.03,.52,.01),(.03,.77,.07),.043,'rust',6)
    for i in range(3):
        t=i*math.tau/3
        m.box(.27*math.cos(t),.12,.27*math.sin(t),.1,.045,.09,'rust',yaw=i*120)


def build_tripod(m):
    m.lathe(0,0,[(.38,.43),(.51,.56),(.70,.56),(.85,.35)],'metal',8)
    # Four outriggers and circular foot pads from the original wreck silhouette.
    reach=min(m.w,m.d)*.41
    for sx,sz in ((-1,-1),(-1,1),(1,-1),(1,1)):
        end=(sx*reach,.16,sz*reach)
        m.lathe(end[0],end[2],[(.015,.18),(.16,.23)],'metal',8)
        m.tube((sx*.34,.57,sz*.34),end,.10,'green',6)
        m.box(sx*reach*.58,.22,sz*reach*.58,.23,.18,.47,'metal',yaw=45 if sx==sz else -45)
    m.lathe(0,0,[(.851,.16),(.86,.16)],'dark',8)
    m.box(-.28,.64,-.13,.22,.15,.29,'rust',yaw=20)


def build_piano(m,harpsichord=False):
    w,d=m.w*.85,m.d*.86
    # An asymmetric, curved sound box with a narrower tail; the keyboard is geometry.
    poly=[(-w*.5,-d*.35),(w*.5,-d*.35),(w*.5,d*.06),(w*.40,d*.29),
          (w*.19,d*.45),(-w*.03,d*.47),(-w*.13,d*.34),(-w*.22,d*.20),
          (-w*.39,d*.16),(-w*.5,d*.09)]
    body='dark' if harpsichord else 'light'
    m.feet(w,d,.73,body)
    m.extrude(poly,.75,1.03,body)
    inner=[(x*.92,z*.90) for x,z in poly]
    m.extrude(inner,1.031,1.048,'wood')
    # Strings extend along the soundboard; no original instrument sprite is used.
    for i in range(12):
        x=-w*.34+i*w*.058
        end=d*(.32 if x>0 else .08)
        m.tube((x,1.06,-d*.25),(x,1.06,end),.009,'light',4)
    for x in (-w*.32,w*.23):m.box(x,1.07,0,.07,.045,d*.55,'wood',yaw=8)
    # Raised lid covers the left half and exposes the strings on the right.
    top=[(x,1.16+(.50 if x<0 else .06),z) for x,z in poly]
    for i in range(1,len(top)-1):m.face([top[0],top[i+1],top[i]],body)
    m.tube((-w*.34,1.05,-d*.10),(-w*.34,1.56,-d*.10),.016,'metal',6)
    z=-d*.40
    m.box(0,.85,z,w*.88,.12,.32,body)
    count=21
    kw=w*.84/count
    for i in range(count):
        x=-w*.42+(i+.5)*kw
        m.box(x,.97,z,kw*.93,.043,.28,'keywhite')
        if i%7 in (0,1,3,4,5) and i<count-1:
            m.box(x+kw*.50,1.014,z+.075,kw*.53,.045,.145,'keyblack')
    m.box(0,.45,-d*.25,w*.21,.055,.16,'metal')
    for x in (-.12,0,.12):m.box(x,.12,-d*.24,.035,.27,.035,'metal')


def build_drone(m):
    # The two opposed swept shells in the spacedrone sprite become separate hulls.
    for sign in (-1,1):
        poly=[(sign*.20,-1.04),(sign*1.65,-.90),(sign*2.37,-.49),
              (sign*2.30,.54),(sign*1.49,.95),(sign*.30,.67)]
        m.extrude(poly,.16,.61,'light')
        m.extrude([(x*.96,z*.90) for x,z in poly],.61,.83,'metal')
    m.box(0,.21,0,.88,.28,1.76,'dark')
    m.tube((-1.20,.37,0),(1.20,.37,0),.21,'metal',10)
    for x in (-1.75,1.75):
        m.tube((x,.36,.35),(x,.36,.85),.20,'dark',10,hollow=True)
    m.box(0,.26,-1.06,.83,.33,.26,'metal')
    m.box(0,.42,-1.199,.48,.075,.018,'glow')
    for i in range(6):m.box(-1.24+i*.16,.165,-.85,.10,.065,.39,'yellow',yaw=22)


def build_chemreactor(m):
    m.plate(0,.015,0,m.w*.89,.13,m.d*.89,'dark',.11)
    for x,z,r,h in ((-.35,.46,.40,1.28),(.30,-.05,.40,1.02),(-.38,-.54,.29,.80)):
        m.lathe(x,z,[(.145,r*.8),(.26,r),(h,r),(h+.12,r*.88)],'metal',12)
        m.lathe(x,z,[(h+.12,r*.91),(h+.16,r*.91)],'light',12)
        for i in range(6):
            t=i*math.tau/6
            m.lathe(x+r*.68*math.cos(t),z+r*.68*math.sin(t),
                    [(h+.16,.027),(h+.184,.027)],'dark',6)
    m.pipe([(-.35,1.20,.46),(.30,1.20,.46),(.30,1.05,-.05),(-.38,.80,-.05),(-.38,.80,-.54)],.048,'metal')
    m.box(-.38,.25,-.817,.21,.36,.025,'yellow')
    m.box(.30,.45,-.46,.17,.17,.025,'dark')
    m.box(.30,.51,-.478,.055,.055,.012,'yellow')


def build_pipes(m):
    # Two loose corroded pipes, with collars and open bores at both ends.
    for i,x in enumerate((-.19,.16)):
        radius=.12 if i==0 else .105
        a=(x,.145,-.39+i*.04);b=(x+.03,.19,.37+i*.015)
        m.tube(a,b,radius,'rust',10,hollow=True)
        axis=unit(sub(b,a))
        for t in (.18,.74):
            mid=add(a,mul(sub(b,a),t))
            m.tube(add(mid,mul(axis,-.035)),add(mid,mul(axis,.035)),radius*1.19,'copper',10)


def build_cell(m):
    # Broken protective shell surrounding the energy chamber.
    m.lathe(0,0,[(.015,.19),(.08,.26)],'metal',12)
    start=len(m.faces)
    profile=[(.08,.26),(.23,.29),(.40,.23),(.49,.12)]
    m.lathe(0,0,profile,'metal',12,cap_bottom=False,cap_top=False)
    # The missing front sector exposes the chamber instead of painting a crack on it.
    shell=m.faces[start:];m.faces=m.faces[:start]
    m.faces.extend(f for i,f in enumerate(shell) if i%12 not in (7,8,9,10))
    for i in (7,11):
        t=i*math.tau/12
        m.pipe([(r*math.cos(t),y,r*math.sin(t)) for y,r in profile],.022,'dark',4)
    m.ball(0,.29,-.035,.18,'cyan',10,6)
    m.box(.21,.12,.035,.07,.27,.18,'rust',yaw=-18)
    m.tube((-.16,.10,-.05),(-.28,.19,-.16),.03,'dark',6)


def build_wire(m):
    # All three strands attach to the post at the same heights. Linked half-runs
    # share exact boundary endpoints with the neighbouring cell's mirrored half.
    m.tube((0,.02,0),(0,.77,0),.034,'rust',6)
    rows=(.18,.43,.66)
    def strand(direction,dx,dz):
        m.links=direction
        for y in rows:
            pts=[(dx*t,y-.07*math.sin(t*math.pi),dz*t) for t in (0,.125,.25,.375,.5)]
            m.pipe(pts,.014,'rust',4)
            for t in (.22,.43):
                x,z=dx*t,dz*t;yy=y-.07*math.sin(t*math.pi)
                m.tube((x-.045,yy-.045,z-.035),(x+.045,yy+.055,z+.035),.009,'metal',4)
        m.links=''
    for direction,dx,dz in (('North',0,1),('East',1,0),('South',0,-1),('West',-1,0)):
        strand(direction,dx,dz)
    # Short loose ends remain only at unconnected ends of a horizontal run.
    # North/south links suppress these arms on perpendicular runs and corners.
    for direction,sign in (('East',1),('West',-1)):
        m.links='!'+direction+',North,South'
        for y in rows:
            m.pipe([(0,y,0),(sign*.16,y-.015,0),(sign*.31,y-.035,0)],.014,'rust',4)
            m.tube((sign*.18-.035,y-.052,-.026),
                   (sign*.18+.035,y+.022,.026),.009,'metal',4)
    m.links=''


def build_watermill(m):
    # Wooden generator house plus a genuine vertical wheel and paddle boards.
    m.box(0,.05,.62,3.76,.20,2.4,'dark')
    m.box(0,.25,.62,3.34,1.20,2.11,'wood')
    # Gabled roof: two sloped boards, not a flat top-down building sprite.
    for sx in (-1,1):
        points=[(0,2.00,-.57),(0,2.00,1.89),(sx*1.95,1.43,1.89),(sx*1.95,1.43,-.57)]
        if sx<0:points.reverse()
        m.face(points,'wood')
    m.face([(-1.67,1.44,-.436),(1.67,1.44,-.436),(0,1.99,-.436)],'wood')
    m.face([(1.67,1.44,1.676),(-1.67,1.44,1.676),(0,1.99,1.676)],'wood')
    for x in (-1.40,-.70,0,.70,1.40):
        m.box(x,.27,-.443,.033,1.15,.025,'dark')
    m.box(0,.26,-.465,.66,.83,.08,'metal')
    # Wheel axis follows z, matching the axle in the south-facing game graphic.
    m.tube((0,1.04,-2.60),(0,1.04,.43),.10,'metal',10)
    for z in (-2.15,-1.25):
        outer=[];inner=[]
        for i in range(16):
            t=i*math.tau/16
            outer.append((1.02*math.cos(t),1.04+1.02*math.sin(t),z))
            inner.append((.83*math.cos(t),1.04+.83*math.sin(t),z))
        # Tangible hoop with thickness, not a flat ring silhouette.
        for i in range(16):
            j=(i+1)%16
            m.face([outer[i],outer[j],inner[j],inner[i]],'wood')
            m.face([add(outer[j],(0,0,.08)),add(outer[i],(0,0,.08)),
                    add(inner[i],(0,0,.08)),add(inner[j],(0,0,.08))],'wood')
            m.face([outer[j],outer[i],add(outer[i],(0,0,.08)),add(outer[j],(0,0,.08))],'dark')
        for i in range(8):
            t=i*math.tau/8
            m.tube((0,1.04,z),(math.cos(t)*.89,1.04+math.sin(t)*.89,z),.045,'wood',6)
    for i in range(12):
        t=i*math.tau/12
        x,y=math.cos(t)*.95,1.04+math.sin(t)*.95
        # Radial paddle cross-section, extruded along the axle.
        u=(math.cos(t)*.16,math.sin(t)*.16)
        v=(-math.sin(t)*.045,math.cos(t)*.045)
        corners=[(x+sx*u[0]+sy*v[0],y+sx*u[1]+sy*v[1]) for sx,sy in ((-1,-1),(1,-1),(1,1),(-1,1))]
        a=[(xx,yy,-2.23) for xx,yy in corners];b=[(xx,yy,-1.17) for xx,yy in corners]
        m.face(a,'wood');m.face(list(reversed(b)),'wood')
        for k in range(4):m.face([a[k],b[k],b[(k+1)%4],a[(k+1)%4]],'wood')


def build_hopper(m):
    # Open bin, with four sloping walls and a recessed outlet at the bottom.
    for x in (-.26,.26):m.box(x,.02,0,.07,.45,.44,'metal')
    m.lathe(0,0,[(.20,.14),(.34,.17),(.73,.38),(.85,.38),
                 (.85,.32),(.73,.32),(.34,.11)],'metal',12,cap_bottom=False,cap_top=False)
    m.lathe(0,0,[(.331,.11),(.335,.11)],'dark',12)
    m.box(0,.15,-.25,.31,.15,.18,'metal')
    m.box(0,.205,-.346,.18,.065,.022,'dark')


def build_tree(m):
    m.lathe(0,0,[(.01,.19),(.07,.23),(.18,.16)],'dark',8)
    m.tube((0,.07,0),(0,1.55,0),.062,'wood',8)
    # Layered boughs have scalloped radial tips, rather than a smooth green cone.
    for base,r,h in ((.20,.43,.73),(.62,.35,.65),(1.00,.26,.57),(1.32,.17,.45)):
        top=(0,base+h,0)
        for i in range(12):
            a=i*math.tau/12;b=(i+1)*math.tau/12
            ri=r*(1 if i%2==0 else .73);rj=r*(1 if (i+1)%2==0 else .73)
            p=(ri*math.cos(a),base+.05*(i%2),ri*math.sin(a))
            q=(rj*math.cos(b),base+.05*((i+1)%2),rj*math.sin(b))
            m.face([p,top,q],'green')
            m.face([p,q,(0,base+.09,0)],'dark')
    for i in range(15):
        t=i*2.4;y=.39+i*.075;r=.40*(1-(y-.2)/1.62)
        m.ball(r*math.cos(t),y,r*math.sin(t),.039,('yellow','red','blue')[i%3],6,4)
    # Five-point star has real thickness and faces on both sides.
    pts=[]
    for i in range(10):
        a=math.pi/2+i*math.tau/10;r=.15 if i%2==0 else .066
        pts.append((r*math.cos(a),1.85+r*math.sin(a)))
    for i in range(10):
        j=(i+1)%10
        m.face([(0,1.85,-.035),(pts[i][0],pts[i][1],0),(pts[j][0],pts[j][1],0)],'yellow')
        m.face([(0,1.85,.035),(pts[j][0],pts[j][1],0),(pts[i][0],pts[i][1],0)],'yellow')


def make(name,style,path_filter=''):
    m=Mesh(name,style,path_filter)
    kind=BUILDERS[name]
    if kind.startswith('arch'):build_arch(m,kind)
    elif kind=='piano':build_piano(m)
    elif kind=='harpsichord':build_piano(m,True)
    else:globals()['build_'+kind](m)
    # The random wreck variants keep subtle damage differences in their geometry.
    if path_filter and kind in ('fuel','generator','wheel','lamppost','beacon','tripod','pipes'):
        letter=path_filter[-1]
        angle=math.radians({'B':5,'C':-7,'D':3,'E':8,'F':-5}.get(letter,0))
        if angle:
            m.vertices=[(x,y*math.cos(angle)+z*math.sin(angle),
                         z*math.cos(angle)-y*math.sin(angle)) for x,y,z in m.vertices]
            low=min(y for x,y,z in m.vertices)
            m.vertices=[(x,y-low+.012,z) for x,y,z in m.vertices]
    return m


def write(path,root):
    ET.indent(root,space='  ')
    ET.ElementTree(root).write(path,encoding='utf-8',xml_declaration=True)
    meta=Path(str(path)+'.meta')
    if not meta.exists():
        guid=uuid.uuid5(uuid.NAMESPACE_URL,str(path.relative_to(ROOT))).hex
        meta.write_text('fileFormatVersion: 2\nguid: '+guid+'\nTextScriptImporter:\n'
                        '  externalObjects: {}\n  userData: \n  assetBundleName: \n'
                        '  assetBundleVariant: \n',encoding='utf-8')


def main():
    styles=json.loads(MATERIALS.read_text(encoding='utf-8'))
    handled=set()
    for path in sorted(MODELS.rglob('*.xml')):
        root=ET.parse(path).getroot();changed=False
        for i,node in enumerate(list(root)):
            name=node.findtext('thingDef')
            if name not in BUILDERS:continue
            path_filter=node.findtext('graphicPathContains','')
            style=styles.get(name+'|'+path_filter,styles[name+'|'])
            mesh=make(name,style,path_filter)
            root.remove(node);root.insert(i,mesh.node(node.findtext('defName')))
            handled.add((name,path_filter));changed=True
        if changed:write(path,root)
    # Unfiltered models also handle Graphic_Random's folder path and placement ghosts.
    for name in BUILDERS:
        if (name,'') not in handled:
            style=styles[name+'|'];mesh=make(name,style)
            root=ET.Element('Defs');root.append(mesh.node())
            path=MODELS/style['dlc']/('RimVision_Recognizable_'+name+'.xml')
            write(path,root)
    print('Authored',len(BUILDERS),'ThingDefs; updated existing variants and supplied folder-path fallbacks.')
    for name in BUILDERS:
        mesh=make(name,styles[name+'|'])
        print(name, sum(len(f[0])-2 for f in mesh.faces),'triangles')


if __name__=='__main__':main()
