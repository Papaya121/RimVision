"""Second prop pass: turrets, furniture, ritual objects and APC. No full-sprite decals."""
from pathlib import Path
import json
import math
import xml.etree.ElementTree as ET
from GenerateRecognizableProps import Mesh, MODELS, write

MATERIALS=Path(__file__).with_name('ModelRepairsMaterials.json')
REPAIRS={'Grave','TransportPod','AnimalBed','Burnbong','DarktorchFungus',
         'PowerSwitch','Pyre','SacrificialFlag','AncientAPC'}


class RepairMesh(Mesh):
    def __init__(self,*args):
        super().__init__(*args)
        self.moving=False
        self.top_faces=set()
    def face(self,*args,**kwargs):
        super().face(*args,**kwargs)
        if self.moving:self.top_faces.add(len(self.faces)-1)
    def node(self,*args):
        node=super().node(*args)
        for i,face in enumerate(node.find('faces')):
            if i in self.top_faces:ET.SubElement(face,'turretTop').text='true'
        return node


def turret(m):
    big=m.w>1
    r=.66 if big else .35
    m.plate(0,.015,0,m.w*.78,.15,m.d*.78,'dark',.10)
    m.lathe(0,0,[(.165,r),(.29,r),(.51,r*.72),(.65,r*.63)],'metal',12)
    for x in (-r*.72,r*.72):m.box(x,.17,0,.12,.27,m.d*.65,'metal')
    m.moving=True
    h=.70 if big else .52
    name=m.name.lower()
    if 'mortar' in name:
        m.tube((-.42,h,0),(.42,h,0),.13,'metal',8)
        for x in (-.42,.42):m.box(x,.46,0,.12,.48,.19,'metal')
        m.tube((0,h,-.12),(0,h+1.02,.62),.16,'metal',12,hollow=True)
        m.tube((0,h+.71,.40),(0,h+.82,.48),.20,'dark',12)
        m.box(0,.30,-.60,.56,.15,.53,'metal')
        m.pipe([(-.35,.24,-.47),(-.35,h,-.05),(-.17,h+.32,.20)],.04,'metal')
    elif 'rocket' in name:
        m.box(0,h,0,.87,.48,.74,'metal')
        for x in (-.25,0,.25):
            for y in (h+.13,h+.36):m.tube((x,y,-.31),(x,y,.80),.10,'metal',8,hollow=True)
    else:
        sniper='sniper' in name
        length=1.90 if sniper else 1.18 if big else .76
        m.box(0,h,-.12,.60 if big else .37,.32 if big else .23,.70 if big else .42,'metal')
        m.tube((0,h+.14,.11),(0,h+.14,length),.075 if sniper else .12 if big else .06,'metal',10,hollow=True)
        m.tube((0,h+.14,length-.16),(0,h+.14,length+.045),.11 if sniper else .15 if big else .085,'dark',10,hollow=True)
        if sniper:
            m.tube((-.13,h+.40,-.13),(-.13,h+.40,.40),.055,'dark',8)
            m.box(-.13,h+.30,.03,.065,.10,.24,'metal')
        elif 'autocannon' in name:
            m.box(.38,h,-.27,.22,.30,.39,'dark')
            m.tube((-.18,h+.14,.10),(-.18,h+.14,length-.2),.053,'metal',8,hollow=True)
        elif 'charge' in name or 'inferno' in name:
            for z in (.29,.50,.71):m.tube((0,h+.14,z),(0,h+.14,z+.065),.16,'metal',10)
        m.box(0,h+.32,-.26,.16,.04,.08,'dark')
    m.moving=False


def grave(m):
    full='GraveFull' in m.path_filter
    # 1 x 2 cells, independently of the 3 x 4 padded vanilla graphic canvas.
    if full:
        m.lathe(0,0,[(.015,.31),(.08,.39),(.19,.27),(.24,.12)],'wood',12,stretch=2.13)
    else:
        m.plate(0,.014,0,.66,.017,1.66,'dark',.08)
        for x in (-.39,.39):m.box(x,.015,0,.095,.045,1.82,'wood')
        for z in (-.88,.88):m.box(0,.015,z,.73,.045,.095,'wood')
    m.box(0,.016,.77,.38,.40,.075,'metal')
    m.box(0,.29,.724,.18,.06,.013,'dark')


def pod(m):
    m.lathe(0,0,[(.01,.29),(.12,.43),(.23,.43),(.31,.36),
                  (1.22,.36),(1.48,.25),(1.63,.09)],'metal',12)
    # Long front hatch and raised frame, rather than a sprite pasted onto a cylinder.
    m.box(0,.32,-.36,.41,.86,.065,'dark')
    m.box(0,.38,-.403,.32,.69,.025,'light')
    m.box(0,.90,-.424,.23,.14,.026,'cyan')
    m.box(.15,.63,-.428,.035,.10,.028,'dark')
    for x in (-.24,.24):m.box(x,.29,-.27,.044,.96,.09,'metal')
    for i in range(3):
        t=i*math.tau/3
        m.pipe([(.31*math.cos(t),.31,.31*math.sin(t)),
                (.49*math.cos(t),.08,.49*math.sin(t))],.045,'dark')
    m.lathe(0,0,[(.002,.13),(.035,.19)],'dark',10)


def animalbed(m):
    m.lathe(0,0,[(.015,.34),(.07,.43),(.16,.43),(.24,.36),
                  (.24,.28),(.12,.23)],'light',16,stretch=.85)
    # Thick sewn bolster around a recessed fabric cushion.
    for i in range(16):
        t=i*math.tau/16
        m.tube((.397*math.cos(t),.175,.337*math.sin(t)),
               (.397*math.cos(t+.025),.175,.337*math.sin(t+.025)),.009,'dark',4)


def burnbong(m):
    m.lathe(0,0,[(.02,.29),(.09,.36),(.36,.32),(.68,.22),
                  (.76,.23),(.76,.16),(.57,.14)],'wood',10)
    for y,r in ((.15,.35),(.41,.305),(.66,.23)):
        m.lathe(0,0,[(y,r),(y+.035,r)],'dark',10)
    for i in range(5):
        t=i*math.tau/5
        m.pipe([(.29*math.cos(t),.1,.29*math.sin(t)),(.19*math.cos(t),.67,.19*math.sin(t))],.016,'copper')


def darktorch(m):
    m.lathe(0,0,[(.015,.15),(.075,.18),(.13,.10)],'dark',8)
    m.tube((0,.075,0),(0,.88,0),.055,'wood',8)
    m.lathe(0,0,[(.75,.07),(.81,.12),(1.0,.12),(1.08,.10)],'light',10)
    for y in (.83,.96):m.lathe(0,0,[(y,.128),(y+.035,.128)],'dark',10)


def switch(m):
    m.plate(0,.015,0,.62,.09,.60,'dark',.08)
    m.box(0,.105,0,.52,.10,.49,'metal')
    for x in (-.20,.20):m.box(x,.20,0,.07,.16,.15,'metal')
    m.tube((-.23,.31,0),(.23,.31,0),.03,'metal',8)
    m.tube((0,.31,0),(0,.48,.17),.035,'metal',8)
    m.box(0,.45,.17,.24,.065,.09,'dark')
    m.box(.15,.21,-.14,.055,.03,.055,'glow')


def pyre(m):
    for y,axis in ((.10,0),(.25,1),(.40,0)):
        for t in (-.55,-.18,.18,.55):
            a=(-.78,y,t) if not axis else (t,y,-.78)
            b=(.78,y,t) if not axis else (t,y,.78)
            m.tube(a,b,.075,'wood',8)
    m.box(0,.46,0,1.05,.10,1.55,'dark')


def flag(m):
    m.lathe(0,0,[(.015,.20),(.09,.20)],'dark',8)
    m.tube((0,.09,0),(0,1.85,0),.028,'wood',6)
    m.tube((-.37,1.73,0),(.37,1.73,0),.025,'wood',6)
    # Hanging pennant with thickness and a pointed, split lower edge.
    for sign in (-1,1):
        x0=-.31 if sign<0 else 0;x1=0 if sign<0 else .31
        for z in (-.014,.014):
            p=[(x0,1.70,z),(x1,1.70,z),(x1,.88,z),((x0+x1)/2,.71,z),(x0,.88,z)]
            if z>0:p.reverse()
            for i in range(1,4):m.face([p[0],p[i],p[i+1]],'rust')
    m.box(0,1.12,-.025,.04,.36,.012,'light')
    m.box(0,1.30,-.025,.24,.04,.012,'light')


def apc(m):
    # Six wheels, armoured hull, both side doors and a destroyed roof hatch.
    for z in (-1.03,1.03):
        for x in (-1.48,0,1.48):
            m.tube((x,.40,z-.10),(x,.40,z+.10),.35,'dark',12)
            m.tube((x,.40,z-.115),(x,.40,z+.115),.15,'metal',10)
    m.plate(0,.36,0,4.3,.49,2.0,'metal',.065)
    m.plate(-.14,.85,0,3.86,.64,1.72,'green',.09)
    m.box(-1.56,1.35,0,.54,.29,1.43,'metal')
    for z in (-.90,.90):
        for x in (-.22,.65):
            m.box(x,.60,z,.65,.78,.06,'dark')
            m.box(x,.65,z+(.035 if z>0 else -.035),.56,.66,.035,'metal')
            m.box(x+.17,.95,z+(.06 if z>0 else -.06),.11,.045,.025,'dark')
        for x in (-1.41,-.85):m.box(x,1.20,z,.37,.17,.027,'dark')
        m.box(-.38,.47,z,2.73,.06,.12,'metal')
    for z in (-.48,.48):m.box(-2.05,1.06,z,.065,.19,.46,'dark')
    m.lathe(.47,0,[(1.49,.30),(1.58,.35),(1.67,.35),(1.67,.22),(1.40,.22)],'dark',12,cap_top=False)
    m.box(.87,1.49,.18,.40,.07,.48,'rust',yaw=-18)
    m.box(1.35,1.49,-.30,.63,.045,.42,'dark')
    for z in (-.66,.66):m.box(2.14,.64,z,.04,.26,.23,'rust')


def make_repair(name,style,path_filter=''):
    m=RepairMesh(name,style,path_filter)
    if name.startswith('Turret_'):turret(m)
    else:{'Grave':grave,'TransportPod':pod,'AnimalBed':animalbed,'Burnbong':burnbong,
          'DarktorchFungus':darktorch,'PowerSwitch':switch,'Pyre':pyre,
          'SacrificialFlag':flag,'AncientAPC':apc}[name](m)
    return m


def main():
    styles=json.loads(MATERIALS.read_text())
    names={key.split('|')[0] for key in styles};done=set()
    for path in sorted(MODELS.rglob('*.xml')):
        root=ET.parse(path).getroot();changed=False
        for i,node in enumerate(list(root)):
            name=node.findtext('thingDef')
            if name not in names:continue
            filt=node.findtext('graphicPathContains','')
            style=styles.get(name+'|'+filt,styles[name+'|'])
            mesh=make_repair(name,style,filt)
            root.remove(node);root.insert(i,mesh.node(node.findtext('defName')))
            changed=True;done.add((name,filt))
        if changed:write(path,root)
    for name in sorted(names):
        if (name,'') not in done:
            style=styles[name+'|'];root=ET.Element('Defs');root.append(make_repair(name,style).node())
            write(MODELS/style['dlc']/('RimVision_Repaired_'+name+'.xml'),root)
    print('Updated',len(names),'definitions and their existing variants.')


if __name__=='__main__':main()
