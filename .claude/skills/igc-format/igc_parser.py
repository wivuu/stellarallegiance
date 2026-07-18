#!/usr/bin/env python3
"""
Self-contained parser for Allegiance binary static-core files (.igc).

An .igc static core packs every ship/part/station/faction/tech as a raw memcpy'd
MSVC C-struct (see src/Igc/igc.h). This reads them back out.

Usage:
    python3 igc_parser.py <core.igc>                # summary: object counts + civ list
    python3 igc_parser.py <core.igc> --dump hulls   # dump a category
        categories: hulls parts stations devs drones civs missiles mines chaff probes
    python3 igc_parser.py <core.igc> --faction "Iron Coalition"   # resolve a faction's buildable set
    python3 igc_parser.py <core.igc> --iron-slice   # Phase-2 report: raw + anchor-translated combat stats

Validated against artwork-full/static_core.igc (5 factions, 683 objects).
"""
import struct, sys
from collections import Counter, defaultdict

# ---- ObjectType enum (igc.h:120-187); static-core subset ----
OT = {22:"projectileType",23:"missileType",24:"mineType",25:"probeType",26:"chaffType",
      27:"civilization",28:"treasureSet",29:"hullType",30:"partType",31:"stationType",
      32:"development",33:"droneType",34:"constants"}

# ---- string-field widths (Utility.h) ----
CB_FILE=13; CB_NAME=25; CB_DESC=201
# ---- DataBuyableIGC base layout: FIXED offsets, shared by hull/part/station/dev/drone ----
# Money price(0,4) DWORD timeToBuild(4,4) modelName[14](8) iconName[13](22) name[25](35)
# description[201](60) groupID(261,1) ttbmRequired[50](262) ttbmEffects[50](312) -> sizeof 364
BUY = 364
# TechTreeBitMask = TLargeBitMask<400> = 50 bytes; bit i -> byte i>>3, mask 0x80>>(i&7)  (MSB-first)
TTBM = 50

def cstr(b):
    i = b.find(b'\x00')
    return (b[:i] if i>=0 else b).decode('latin-1','replace')

def u(fmt,b,o): return struct.unpack_from(fmt,b,o)[0]

def mask_bits(b, n=400):
    return [i for i in range(n) if (i>>3)<len(b) and (b[i>>3] & (0x80>>(i&7)))]

def read_records(path):
    """File = [int32 version][int32 totalSize][records]; record = [int16 type][int32 size][size bytes]."""
    with open(path,'rb') as f: data=f.read()
    version = u('<i',data,0); datasize = u('<i',data,4)
    recs=[]; p=8; end=8+datasize
    while p < end:
        typ = u('<h',data,p); size = u('<i',data,p+2)
        recs.append((typ, size, data[p+6:p+6+size]))
        p += 6+size
    return version, datasize, recs

# ---- DataBuyableIGC base (price/name/tech masks) ----
def parse_buyable(b):
    return dict(price=u('<i',b,0), build=u('<I',b,4),
        model=cstr(b[8:22]), icon=cstr(b[22:35]), name=cstr(b[35:60]),
        desc=cstr(b[60:261]), group=u('<b',b,261),
        req=mask_bits(b[262:312]), eff=mask_bits(b[312:362]))

# ---- derived structs (all start at offset 364; MSVC does NOT reuse base tail padding) ----
def parse_hull(b, size):
    d=parse_buyable(b); o=BUY
    # DataHullTypeIGC derived layout (offsets relative to BUY=364; validated by hullID@+82 etc.):
    #  mass@0 signature@4 speed@8 maxTurnRates[3]@12 turnTorques[3]@24 thrust@36
    #  sideMultiplier@40 backMultiplier@44 scannerRange@48 maxFuel@52 ecm@56 length@60
    #  maxEnergy@64 rechargeRate@68 ripcordSpeed@72 ripcordCost@76 maxAmmo(short)@80
    #  hullID@82 succ@84 maxWeapons@86 maxFixed@87 hitPoints(float)@88 hardpointOffset@92
    #  defenseType(byte)@94 capacityMagazine@96 capacityDispenser@98 capacityChaffLauncher@100
    #  habm@130 pmEquipment[8]@146  -> struct sizeof 540 (incl. 364 base)
    d.update(hullID=u('<h',b,o+82), succ=u('<h',b,o+84), maxWeapons=u('<b',b,o+86),
             maxFixed=u('<b',b,o+87), habm=u('<H',b,o+130),
             pmEquip=list(struct.unpack_from('<8H',b,o+146)),
             mass=u('<f',b,o+0), signature=u('<f',b,o+4), speed=u('<f',b,o+8),
             maxTurnRates=list(struct.unpack_from('<3f',b,o+12)),
             turnTorques=list(struct.unpack_from('<3f',b,o+24)),
             thrust=u('<f',b,o+36), sideMult=u('<f',b,o+40), backMult=u('<f',b,o+44),
             scannerRange=u('<f',b,o+48), maxFuel=u('<f',b,o+52), ecm=u('<f',b,o+56),
             length=u('<f',b,o+60), maxEnergy=u('<f',b,o+64), rechargeRate=u('<f',b,o+68),
             ripcordSpeed=u('<f',b,o+72), ripcordCost=u('<f',b,o+76), maxAmmo=u('<h',b,o+80),
             hitPoints=u('<f',b,o+88), defenseType=u('<B',b,o+94),
             capacityMagazine=u('<h',b,o+96), capacityDispenser=u('<h',b,o+98),
             capacityChaffLauncher=u('<h',b,o+100))
    # variable HardpointData[] tail: 30 bytes each, PartMask at +26, bFixed at +28
    HULL_STRUCT=540; HP=30
    n=max(0,(size-HULL_STRUCT)//HP)
    d['hardpoints']=[u('<H',b,HULL_STRUCT+i*HP+26) for i in range(n)]
    return d

def parse_part(b, size):
    if size < 100:  # magazine/dispenser = DataLauncherTypeIGC (igc.h:1881, 23 B unpadded), no DataBuyableIGC base
        # amount@0 partID@2 successorPartID@4 launchCount@6 expendableTypeID@8 (all i16)
        return dict(name='<launcher>', launcher=True,
                    amount=u('<h',b,0), partID=u('<h',b,2), succ=u('<h',b,4),
                    launchCount=u('<h',b,6), expendableTypeID=u('<h',b,8))
    d=parse_buyable(b); o=BUY
    d.update(mass=u('<f',b,o), partID=u('<h',b,o+8), succ=u('<h',b,o+10),
             equipmentType=u('<h',b,o+12), partMask=u('<H',b,o+14))
    # DataWeaponTypeIGC tail follows DataPartTypeIGC (derived sizeof 32 -> weapon fields at +32):
    #  dtimeReady@32 dtimeBurst@36 energyPerShot@40 dispersion@44 cAmmoPerShot(short)@48
    #  projectileTypeID@50 activateSound@52 singleShotSound@54 burstSound@56
    if d.get('equipmentType')==1 and size >= BUY+58:
        d.update(dtimeReady=u('<f',b,o+32), dtimeBurst=u('<f',b,o+36),
                 energyPerShot=u('<f',b,o+40), dispersion=u('<f',b,o+44),
                 cAmmoPerShot=u('<h',b,o+48), projectileTypeID=u('<h',b,o+50),
                 activateSound=u('<h',b,o+52), singleShotSound=u('<h',b,o+54),
                 burstSound=u('<h',b,o+56))
    return d

def parse_projectile(b):
    # DataProjectileTypeIGC : DataObjectIGC (NOT a buyable, no name field).
    # DataObjectIGC(52): COLORVALUE color@0(16) radius@16 rotation@20 modelName[13]@24 textureName[13]@37
    # then derived @52: power@52 blastPower@56 blastRadius@60 speed@64 lifespan@68
    #   projectileTypeID(short)@72 damageType(byte)@74 absoluteF@75 bDirectional@76 width(float)@80 ambientSound@84
    # (width lands @80 whether damageType is 1 or 2 bytes — float 4-alignment absorbs the difference)
    o=0
    return dict(model=cstr(b[24:37]), radius=u('<f',b,o+16),
        power=u('<f',b,o+52), blastPower=u('<f',b,o+56), blastRadius=u('<f',b,o+60),
        speed=u('<f',b,o+64), lifespan=u('<f',b,o+68),
        projID=u('<h',b,o+72), damageType=u('<B',b,o+74), width=u('<f',b,o+80))

def parse_station(b):
    d=parse_buyable(b); o=BUY
    # DataStationTypeIGC derived (offsets rel BUY; validated by income@+24, ttbmLocal@+32, stationID@+82):
    #  signature@0 maxArmorHitPoints@4 maxShieldHitPoints@8 armorRegen@12 shieldRegen@16
    #  scannerRange@20 income@24 radius@28 ttbmLocal[50]@32 stationID@82 succ@84
    #  defenseTypeArmor(byte)@86 defenseTypeShield(byte)@87 sabm@88 aabm@90 classID@92 constructionDrone@94
    d.update(income=u('<i',b,o+24), ttbmLocal=mask_bits(b[o+32:o+82]),
             stationID=u('<h',b,o+82), succ=u('<h',b,o+84),
             sabm=u('<H',b,o+88), aabm=u('<H',b,o+90), classID=u('<B',b,o+92),
             constructionDrone=u('<h',b,o+94),
             signature=u('<f',b,o+0), maxArmor=u('<f',b,o+4), maxShield=u('<f',b,o+8),
             armorRegen=u('<f',b,o+12), shieldRegen=u('<f',b,o+16),
             scannerRange=u('<f',b,o+20), radius=u('<f',b,o+28))
    return d

def parse_dev(b):
    d=parse_buyable(b); o=BUY
    gas=list(struct.unpack_from('<25f',b,o))
    d.update(gas=gas, devID=u('<h',b,o+100),
             techOnly=all(abs(x-1.0)<1e-6 for x in gas))  # techOnly = no stat effects (developmentigc.cpp:38)
    return d

def parse_drone(b):
    d=parse_buyable(b); o=BUY
    d.update(pilot=u('<B',b,o+12), hullTypeID=u('<h',b,o+14), droneID=u('<h',b,o+16))
    return d

def parse_civ(b):
    return dict(income=u('<f',b,0), bonus=u('<f',b,4), name=cstr(b[8:33]),
        icon=cstr(b[33:46]), hud=cstr(b[46:59]),
        base=mask_bits(b[59:109]), nodev=mask_bits(b[109:159]),
        gas=list(struct.unpack_from('<25f',b,160)),
        lifepod=u('<h',b,260), civID=u('<h',b,262), initStation=u('<h',b,264))

def parse_expendable_base(b):
    """DataExpendableTypeIGC common layout, shared by missile/mine/chaff/probe records
    (igc.h:1946 DataExpendableTypeIGC, ~1881 DataLauncherTypeIGC's sibling LauncherDef):
      DataObjectIGC(52) + loadTime@52/lifespan@56/signature@60(f32) +
      embedded LauncherDef @64 = DataBuyableIGC(364, name@99 as already validated) +
      signature@428/mass@432(f32) + partMask@436/expendableSize@438(u16) +
      hitPoints@440(f32) + defenseType@444(u8) + expendableTypeID@446(i16)
      -> type-specific tail starts @464 (all validated against igc.h struct math)."""
    d = parse_buyable(b[64:64+BUY])  # embedded LauncherDef's DataBuyableIGC base
    d.update(loadTime=u('<f',b,52), lifespan=u('<f',b,56), signature=u('<f',b,60),
              launchSig=u('<f',b,428), mass=u('<f',b,432),
              partMask=u('<H',b,436), expendableSize=u('<H',b,438),
              hitPoints=u('<f',b,440), defenseType=u('<B',b,444),
              expendableTypeID=u('<h',b,446))
    return d

def parse_missile(b):
    # DataMissileTypeIGC tail @464 (igc.h:1960), record sizeof 524 (validated).
    d = parse_expendable_base(b)
    d.update(type='missile',
              acceleration=u('<f',b,464), turnRate=u('<f',b,468), initialSpeed=u('<f',b,472),
              lockTime=u('<f',b,476), readyTime=u('<f',b,480), maxLock=u('<f',b,484),
              chaffResistance=u('<f',b,488), dispersion=u('<f',b,492), lockAngle=u('<f',b,496),
              power=u('<f',b,500), blastPower=u('<f',b,504), blastRadius=u('<f',b,508),
              width=u('<f',b,512), damageType=u('<B',b,516))
    return d

def parse_mine(b):
    # DataMineTypeIGC tail @464 (igc.h:1985).
    d = parse_expendable_base(b)
    d.update(type='mine', radius=u('<f',b,464), power=u('<f',b,468), endurance=u('<f',b,472),
              damageType=u('<B',b,476))
    return d

def parse_chaff(b):
    # DataChaffTypeIGC tail @464 (igc.h:1992).
    d = parse_expendable_base(b)
    d.update(type='chaff', chaffStrength=u('<f',b,464))
    return d

def parse_probe(b):
    # DataProbeTypeIGC tail @464 (igc.h:1996).
    d = parse_expendable_base(b)
    d.update(type='probe', scannerRange=u('<f',b,464), dtimeBurst=u('<f',b,468),
              dispersion=u('<f',b,472), accuracy=u('<f',b,476), ammo=u('<h',b,480),
              projectileTypeID=u('<h',b,482), dtRipcord=u('<f',b,488))
    return d

# ---- enum label helpers ----
EQUIP={0:"ChaffLauncher",1:"Weapon",2:"Magazine",3:"Dispenser",4:"Shield",5:"Cloak",6:"Pack",7:"Afterburner"}
SCLASS={0:"Starbase",1:"Garrison",2:"Shipyard",3:"Ripcord",4:"Mining",5:"Research(Exp)",
        6:"Ordinance(Sup)",7:"Electronics(Tac)"}
PILOT={0:"Miner",2:"Wingman",5:"Layer",6:"Builder",9:"Carrier",10:"Player"}

def parse_all(path):
    _,_,recs = read_records(path)
    out=defaultdict(list)
    for t,s,b in recs:
        if   t==29: out['hulls'].append(parse_hull(b,s))
        elif t==22: out['projectiles'].append(parse_projectile(b))
        elif t==30: out['parts'].append(parse_part(b,s))
        elif t==31: out['stations'].append(parse_station(b))
        elif t==32: out['devs'].append(parse_dev(b))
        elif t==33: out['drones'].append(parse_drone(b))
        elif t==27: out['civs'].append(parse_civ(b))
        elif t==23: out['missiles'].append(parse_missile(b))
        elif t==24: out['mines'].append(parse_mine(b))
        elif t==26: out['chaff'].append(parse_chaff(b))
        elif t==25: out['probes'].append(parse_probe(b))
    return out, recs

def resolve_faction(data, civ):
    """Replicate CsideIGC::CreateBuckets (sideigc.cpp) for the given civ.
    seed = base techs + the four AllowPath bits {1,2,3,4} (missionigc.cpp:3874, all paths on).
    ultimate = seed | all part/station effects, grown to a fixed point over developments.
    An item is available when its required tech-bits are a subset of the reachable set."""
    PATH_BITS={1,2,3,4}
    parts,stations,devs=data['parts'],data['stations'],data['devs']
    ult=set(civ['base'])|PATH_BITS; localu=set()
    for p in parts:
        if not p.get('launcher'): ult|=set(p['eff'])
    for s in stations: ult|=set(s['eff']); localu|=set(s['ttbmLocal'])
    changed=True
    while changed:
        changed=False
        for d in devs:
            if set(d['req'])<=ult and not set(d['eff'])<=ult:
                ult|=set(d['eff']); changed=True
    localu|=ult
    init=set(civ['base'])|PATH_BITS
    sub=lambda o: set(o['req'])<=localu
    return dict(ultimate=ult, localUltimate=localu,
        hulls=[h for h in data['hulls'] if sub(h)],
        parts=[p for p in parts if not p.get('launcher') and sub(p)],
        stations=[s for s in stations if sub(s)],
        drones=[d for d in data['drones'] if sub(d)],
        # expendables carry req/eff via their embedded LauncherDef buyable; same subset rule as parts
        missiles=[m for m in data['missiles'] if sub(m)],
        mines=[m for m in data['mines'] if sub(m)],
        chaff=[m for m in data['chaff'] if sub(m)],
        probes=[m for m in data['probes'] if sub(m)],
        devs=[d for d in devs if d['devID']!=1 and set(d['req'])<=ult
              and not (d['techOnly'] and set(d['eff'])<=init)])

def _dedup(lst):
    seen=set(); out=[]
    for o in lst:
        if o['name'] in seen: continue
        seen.add(o['name']); out.append(o)
    return out

# ---- --iron-slice: focused raw+translated report for the Phase-2 content transcription ----
# Translation convention = "keep our magnitudes, import Allegiance ratios" (plan Adaptation §3).
# Anchors (read from server/Content/core/*.yaml @ 2026-07-16):
#   HULL anchor: our `fighter` <-> IGC "Enh Fighter". our: speed 100, armor-hit-points 120, mass 36,
#                thrust 25, max-turn-rates 60 (yaw/pitch/roll).  IGC EnhF: speed 120, hp 350, mass 30,
#                thrust 750, turn 1.047 (rad, all three axes equal).
#   GUN anchor:  our `fighter-cannon`/`fighter-bolt` <-> IGC "PW Gat Gun 1". our: power 10, proj speed 200,
#                fire-interval-ticks 4, projectile-life-ticks 16.  IGC Gat1: proj power 3.2, proj speed 600,
#                dtimeReady 0.25, dtimeBurst 0.10.
#   STATION/DEV price anchor: our outpost 300 <-> IGC 5000  =>  x0.06.  STATION armor: our garrison
#                max-armor 2000 <-> IGC Garrison maxArmor 20000  => x0.10.  radius: our garrison 90 <-> 423.5.
_A = dict(hull_speed=100.0, hull_armor=120.0, hull_mass=36.0, hull_thrust=25.0, hull_turn=60.0,
          igc_speed=120.0, igc_hp=350.0, igc_mass=30.0, igc_thrust=750.0, igc_turn=1.047197,
          gun_power=10.0, gun_pspeed=200.0, igc_gpower=3.2, igc_gspeed=600.0,
          price_mult=0.06, sta_armor=2000.0, igc_sta_armor=20000.0, sta_radius=90.0, igc_sta_radius=423.5)

#   ORDNANCE anchors: unlike HULL/GUN/STATION above, these are NOT measured against an existing
#   our-side ordnance item (none exists yet pre-import) — they are judgment-call multipliers picked
#   to keep Allegiance's ordnance ratios recognizable at our tick/scale, meant to be eyeballed and
#   adjusted per line during transcription, not applied mechanically:
#     missile : power x0.75 | lock-time x0.75 | acceleration x2/3 | turn-rate rad/s x83.3 -> deg/s
#               (cap fast turners to ~120-200 deg/s by judgment — raw conversion overshoots at our
#               scale) | fire-interval-ticks = round(loadTime*15) (15 ticks/s is a starting anchor,
#               judgment-adjusted per line, not a measured tick rate)
#     mine    : power x0.15 | endurance x0.03 -> lifespan (seconds) | radius x0.8
#     chaff   : chaffStrength x2/3
#     probe   : scannerRange x9.6 -> sight-radius
#     magazine: amount x0.6
_O = dict(missile_power=0.75, missile_lock=0.75, missile_accel=2/3, missile_turn_rad2deg=83.3,
          missile_turn_cap_lo=120.0, missile_turn_cap_hi=200.0, missile_fi_ticks_per_sec=15.0,
          mine_power=0.15, mine_endurance=0.03, mine_radius=0.8,
          chaff_strength=2/3, probe_scanner=9.6, magazine_amount=0.6)

def _pick(lst, name):
    """Exact (case-insensitive) name match within a resolved roster list; warn on 0/>1."""
    hits=[o for o in lst if o.get('name','').lower()==name.lower()]
    if not hits: return None
    if len(hits)>1:
        # collapse identical duplicates; prefer distinct — keep first, note if they truly differ
        pass
    return hits[0]

def iron_slice_report(data):
    civ=next((c for c in data['civs'] if 'iron' in c['name'].lower()), None)
    if not civ:
        print("no Iron Coalition faction in this core"); return
    r=resolve_faction(data,civ)
    projById={}
    for p in data['projectiles']:
        projById.setdefault(p['projID'], p)
    A=_A
    def rnd(x,n=2):
        v=round(x,n); return int(v) if n==0 else v

    print(f"IRON COALITION SLICE  (civ {civ['civID']}, core faction {civ['name']!r})")
    print("Translation = keep-our-magnitudes / import-IGC-ratios (plan Adaptation §3). "
          "'raw' = IGC value, 'yaml' = anchor-translated with the core-bundle field name.\n")

    # ---------------- HULLS ----------------
    print("================= HULLS (anchor: Enh Fighter <-> our `fighter`) =================")
    for name in ['Scout','Lt Interceptor','Enh Fighter','Adv Fighter','Bomber','Devastator','Miner','Lifepod']:
        h=_pick(r['hulls'], name)
        if not h: print(f"  {name!r}: NOT FOUND in Iron roster"); continue
        tr_speed = rnd(A['hull_speed']*h['speed']/A['igc_speed'],1)
        tr_armor = rnd(A['hull_armor']*h['hitPoints']/A['igc_hp'],0)
        tr_mass  = rnd(A['hull_mass']*h['mass']/A['igc_mass'],1)
        tr_thr   = rnd(A['hull_thrust']*h['thrust']/A['igc_thrust'],2)
        tr_turn  = rnd(A['hull_turn']*h['maxTurnRates'][0]/A['igc_turn'],1)
        print(f"  {h['name']} [id {h['hullID']} succ {h['succ']} model {h['model']!r} price {h['price']} "
              f"mounts {h['maxWeapons']} hardpoints {[hex(x) for x in h['hardpoints']]}]")
        print(f"    raw : mass {h['mass']:.0f}  speed {h['speed']:.0f}  thrust {h['thrust']:.0f}  "
              f"hitPoints {h['hitPoints']:.0f}  turnRate {h['maxTurnRates'][0]:.3f} rad  sig {h['signature']:.2f}  "
              f"scanner {h['scannerRange']:.0f}  length {h['length']:.1f}  maxEnergy {h['maxEnergy']:.0f}  "
              f"maxAmmo {h['maxAmmo']}  maxFuel {h['maxFuel']:.1f}  ecm {h['ecm']:.2f}  defenseType {h['defenseType']}  "
              f"sideMult {h['sideMult']:.2f}  backMult {h['backMult']:.2f}  ripcord {h['ripcordSpeed']:.0f}/{h['ripcordCost']:.0f}")
        print(f"    yaml: mass {tr_mass}  speed {tr_speed}  thrust {tr_thr}  armor-hit-points {tr_armor}  "
              f"max-turn-rates {{ yaw: {tr_turn}, pitch: {tr_turn}, roll: {tr_turn} }}")

    # ---------------- GUNS ----------------
    print("\n================= GUNS (anchor: PW Gat Gun 1 <-> our `fighter-cannon`) =================")
    print("  NOTE: sustained fire cadence in Allegiance = dtimeBurst (weaponIGC.cpp:310 `m_nextFire += dtimeBurst`).")
    print("        dtimeReady is uniform 0.25 across ALL guns -> the plan's round(dtimeReady*20)=5 flattens the")
    print("        gun line. Both interval derivations are printed; prefer the dtimeBurst one for feel.\n")
    guns=[p for p in r['parts'] if p.get('equipmentType')==1]
    for name in ['PW Gat Gun 1','PW Gat Gun 2','PW Gat Gun 3','PW Mini-Gun 1','PW Mini-Gun 2','PW Mini-Gun 3',
                 'PW AutoCan 1','PW AutoCan 2','PW AutoCan 3','ER Nanite 1','ER Nanite 2','ER Nanite 3']:
        w=_pick(guns, name)
        if not w: print(f"  {name!r}: NOT FOUND in Iron roster"); continue
        pj=projById.get(w.get('projectileTypeID'))
        healing = pj is not None and pj['power']<0
        tr_power = rnd(A['gun_power']*abs(pj['power'])/A['igc_gpower'],2) if pj else None
        tr_pspeed= rnd(A['gun_pspeed']*pj['speed']/A['igc_gspeed'],0) if pj else None
        tr_life  = min(20, rnd(pj['lifespan']*20,0)) if pj else None
        fi_ready = rnd(w['dtimeReady']*20,0)
        fi_burst = rnd(w['dtimeBurst']*20,0)
        print(f"  {w['name']} [partID {w['partID']} succ {w['succ']} partMask 0x{w['partMask']:x} price {w['price']}]")
        print(f"    raw gun : dtimeReady {w['dtimeReady']:.3f}  dtimeBurst {w['dtimeBurst']:.3f}  "
              f"energyPerShot {w['energyPerShot']:.2f}  dispersion {w['dispersion']:.3f}  "
              f"cAmmoPerShot {w['cAmmoPerShot']}  projectileTypeID {w['projectileTypeID']}")
        if pj:
            print(f"    raw proj: id {pj['projID']} model {pj['model']!r}  power {pj['power']:.2f}  "
                  f"blastPower {pj['blastPower']:.2f}  blastRadius {pj['blastRadius']:.1f}  speed {pj['speed']:.0f}  "
                  f"lifespan {pj['lifespan']:.3f}  width {pj['width']:.3f}  radius {pj['radius']:.3f}  "
                  f"damageType {pj['damageType']}")
            print(f"    yaml    : power {tr_power}{'  is-healing: true (IGC power<0)' if healing else ''}  "
                  f"projectile speed {tr_pspeed}  projectile-life-ticks {tr_life}  "
                  f"fire-interval-ticks {fi_burst} (dtimeBurst)  |  {fi_ready} (plan dtimeReady formula)")
        else:
            print(f"    raw proj: MISSING (projectileTypeID {w['projectileTypeID']} unresolved)")

    # ---------------- STATIONS ----------------
    print("\n================= STATIONS (anchor: Garrison armor 20000 <-> our 2000; price x0.06) =================")
    for name in ['Garrison','Garrison (Str)','Outpost (Hvy)','Supremacy','Supremacy (Adv)','Shipyard','Shipyard (Dry)']:
        s=_pick(r['stations'], name)
        if not s: print(f"  {name!r}: NOT FOUND in Iron roster"); continue
        tr_armor = rnd(A['sta_armor']*s['maxArmor']/A['igc_sta_armor'],0)
        tr_shield= rnd(A['sta_armor']*s['maxShield']/A['igc_sta_armor'],0)
        tr_radius= rnd(A['sta_radius']*s['radius']/A['igc_sta_radius'],1)
        tr_price = rnd(s['price']*A['price_mult'],0)
        print(f"  {s['name']} [id {s['stationID']} succ {s['succ']} model {s['model']!r} class {SCLASS.get(s['classID'],s['classID'])}]")
        print(f"    raw : price {s['price']}  maxArmor {s['maxArmor']:.0f}  maxShield {s['maxShield']:.0f}  "
              f"signature {s['signature']:.2f}  scannerRange {s['scannerRange']:.0f}  radius {s['radius']:.1f}")
        print(f"    yaml: price {tr_price}  max-armor {tr_armor}  max-shield-station {tr_shield} (inert - bases have no shields)  "
              f"radius {tr_radius}")

    # ---------------- DEVELOPMENTS ----------------
    print("\n================= DEVELOPMENTS (raw price/techOnly/req/eff; translated price x0.06) =================")
    for name in ['Bomber','Upgrade Garrison','Upgrade Supremacy','Upgrade Heavy Class',
                 'PW Gattling Gun 2','PW Gattling Gun 3','PW Mini-Gun 2','PW Mini-Gun 3',
                 'PW Auto-Cannon 2','PW Auto-Cannon 3','ER Nanite 2','ER Nanite 3']:
        d=_pick(r['devs'], name)
        if not d: print(f"  {name!r}: NOT FOUND in Iron roster"); continue
        print(f"  {d['name']:22} price {d['price']:6d} -> yaml {rnd(d['price']*A['price_mult'],0):5}  "
              f"techOnly {d['techOnly']}  req {d['req']}  eff {d['eff']}")

    # ---------------- ORDNANCE (missiles/mines/chaff/probes + launcher magazines) ----------------
    O=_O
    print("\n================= ORDNANCE (expendables: missiles/mines/chaff/probes + magazines) =================")
    print("  Translation = judgment-call multipliers (NOT measured against an existing our-side ordnance")
    print("  anchor item — none exists pre-import); eyeball/adjust per line during transcription:")
    print("    missile : power x0.75 | lock-time x0.75 | acceleration x2/3 | turn-rate rad/s x83.3 -> deg/s")
    print("              (cap fast turners ~120-200 deg/s by judgment) | fire-interval-ticks =")
    print("              round(loadTime*15)  (judgment-adjusted per line)")
    print("    mine    : power x0.15 | endurance x0.03 -> lifespan(s) | radius x0.8")
    print("    chaff   : chaffStrength x2/3")
    print("    probe   : scannerRange x9.6 -> sight-radius")
    print("    magazine: amount x0.6\n")

    all_mags=[p for p in data['parts'] if p.get('launcher')]
    def mags_for(etid): return [m for m in all_mags if m['expendableTypeID']==etid]
    def print_magazines(etid):
        ms=mags_for(etid)
        if not ms: print("    magazine: NONE FOUND"); return
        for m in ms:
            tr_amt=rnd(m['amount']*O['magazine_amount'],0)
            print(f"    magazine: partID {m['partID']} succ {m['succ']} amount {m['amount']} (yaml {tr_amt})  "
                  f"launchCount {m['launchCount']}")

    def print_missile(o):
        tr_power=rnd(o['power']*O['missile_power'],1)
        tr_lock =rnd(o['lockTime']*O['missile_lock'],2)
        tr_accel=rnd(o['acceleration']*O['missile_accel'],1)
        tr_turn_raw=o['turnRate']*O['missile_turn_rad2deg']
        cap_note = (f"  (raw {rnd(tr_turn_raw,1)} deg/s exceeds judgment cap -- consider clamping to "
                    f"~{O['missile_turn_cap_lo']:.0f}-{O['missile_turn_cap_hi']:.0f})") \
                   if tr_turn_raw>O['missile_turn_cap_hi'] else ""
        tr_turn=rnd(min(tr_turn_raw,O['missile_turn_cap_hi']),1)
        tr_fi  =rnd(o['loadTime']*O['missile_fi_ticks_per_sec'],0)
        print(f"  {o['name']} [expendableTypeID {o['expendableTypeID']} model {o['model']!r} price {o['price']} "
              f"req {o['req']}]")
        print(f"    raw : power {o['power']:.1f}  lockTime {o['lockTime']:.3f}  turnRate {o['turnRate']:.3f} rad  "
              f"acceleration {o['acceleration']:.1f}  initialSpeed {o['initialSpeed']:.1f}  readyTime {o['readyTime']:.2f}  "
              f"maxLock {o['maxLock']:.2f}  chaffResistance {o['chaffResistance']:.2f}  dispersion {o['dispersion']:.3f}  "
              f"lockAngle {o['lockAngle']:.3f}  blastPower {o['blastPower']:.1f}  blastRadius {o['blastRadius']:.1f}  "
              f"width {o['width']:.2f}  damageType {o['damageType']}  loadTime {o['loadTime']:.2f}  "
              f"lifespan {o['lifespan']:.1f}  hitPoints {o['hitPoints']:.0f}  mass {o['mass']:.1f}")
        print(f"    yaml: power {tr_power}  lock-time {tr_lock}  turn-rate-deg-s {tr_turn}{cap_note}  "
              f"acceleration {tr_accel}  fire-interval-ticks {tr_fi} (judgment-adjusted per line)")
        print_magazines(o['expendableTypeID'])

    def print_mine(o):
        tr_power=rnd(o['power']*O['mine_power'],1)
        tr_life =rnd(o['endurance']*O['mine_endurance'],1)
        tr_radius=rnd(o['radius']*O['mine_radius'],1)
        print(f"  {o['name']} [expendableTypeID {o['expendableTypeID']} model {o['model']!r} price {o['price']} "
              f"req {o['req']}]")
        print(f"    raw : power {o['power']:.1f}  endurance {o['endurance']:.1f}  radius {o['radius']:.1f}  "
              f"damageType {o['damageType']}  loadTime {o['loadTime']:.2f}  lifespan {o['lifespan']:.1f}  "
              f"hitPoints {o['hitPoints']:.0f}  mass {o['mass']:.1f}")
        print(f"    yaml: power {tr_power}  lifespan-s {tr_life}  radius {tr_radius}")
        print_magazines(o['expendableTypeID'])

    def print_chaff(o):
        tr_str=rnd(o['chaffStrength']*O['chaff_strength'],2)
        print(f"  {o['name']} [expendableTypeID {o['expendableTypeID']} model {o['model']!r} price {o['price']} "
              f"req {o['req']}]")
        print(f"    raw : chaffStrength {o['chaffStrength']:.3f}  loadTime {o['loadTime']:.2f}  "
              f"lifespan {o['lifespan']:.1f}  hitPoints {o['hitPoints']:.0f}  mass {o['mass']:.2f}")
        print(f"    yaml: strength {tr_str}")
        print_magazines(o['expendableTypeID'])

    def print_probe(o):
        tr_scan=rnd(o['scannerRange']*O['probe_scanner'],0)
        print(f"  {o['name']} [expendableTypeID {o['expendableTypeID']} model {o['model']!r} price {o['price']} "
              f"req {o['req']}]")
        print(f"    raw : scannerRange {o['scannerRange']:.0f}  dtimeBurst {o['dtimeBurst']:.2f}  "
              f"dispersion {o['dispersion']:.3f}  accuracy {o['accuracy']:.2f}  ammo {o['ammo']}  "
              f"projectileTypeID {o['projectileTypeID']}  dtRipcord {o['dtRipcord']:.2f}  loadTime {o['loadTime']:.2f}  "
              f"lifespan {o['lifespan']:.0f}  hitPoints {o['hitPoints']:.0f}  mass {o['mass']:.1f}")
        print(f"    yaml: sight-radius {tr_scan}")
        print_magazines(o['expendableTypeID'])

    print("--- MISSILES ---")
    for name in ['SRM Dumbfire 1','SRM Dumbfire 2','SRM Dumbfire 3',
                 'MRM Seeker 1','MRM Seeker 2','MRM Seeker 3',
                 'MRM Quickfire 1','MRM Quickfire 2','MRM Quickfire 3',
                 'SRM Anti-Base 1','SRM Anti-Base 2','SRM Anti-Base 3']:
        o=_pick(r['missiles'], name)
        if not o: print(f"  {name!r}: NOT FOUND in Iron roster"); continue
        print_missile(o)

    print("\n--- MINES ---")
    for name in ['Prox Mine 1','Prox Mine 2','Prox Mine 3']:
        o=_pick(r['mines'], name)
        if not o: print(f"  {name!r}: NOT FOUND in Iron roster"); continue
        print_mine(o)

    print("\n--- CHAFF ---")
    for name in ['Counter 1','Counter 2','Counter 3']:
        o=_pick(r['chaff'], name)
        if not o: print(f"  {name!r}: NOT FOUND in Iron roster"); continue
        print_chaff(o)

    print("\n--- PROBES ---")
    for name in ['EWS Probe 1','EWS Probe 2','EWS Probe 3']:
        o=_pick(r['probes'], name)
        if not o: print(f"  {name!r}: NOT FOUND in Iron roster"); continue
        print_probe(o)

def main():
    if len(sys.argv)<2:
        print(__doc__); return
    path=sys.argv[1]
    data,recs=parse_all(path)
    ver,ds,_=read_records(path)
    if '--iron-slice' in sys.argv:
        iron_slice_report(data); return
    if '--faction' in sys.argv:
        name=sys.argv[sys.argv.index('--faction')+1]
        civ=next((c for c in data['civs'] if name.lower() in c['name'].lower()), None)
        if not civ: print("no such faction; have:", [c['name'] for c in data['civs']]); return
        r=resolve_faction(data,civ)
        print(f"{civ['name']} (civ {civ['civID']}): income{civ['income']:+.3f} bonus{civ['bonus']:+.3f} "
              f"lifepod={civ['lifepod']} initStation={civ['initStation']} baseTechBits={len(civ['base'])}")
        for k in ['hulls','parts','stations','drones','devs']:
            names=sorted(set(o['name'] for o in r[k]))
            print(f"  {k} ({len(names)}): {names}")
        return
    if '--dump' in sys.argv:
        cat=sys.argv[sys.argv.index('--dump')+1]
        for o in _dedup(data[cat]) if data[cat] and isinstance(data[cat][0],dict) else data[cat]:
            if isinstance(o,str): print(" ",o); continue
            extra=""
            if cat=='parts': extra=f" et={EQUIP.get(o.get('equipmentType'),'?')} pmask=0x{o.get('partMask',0):x}"
            if cat=='stations': extra=f" class={SCLASS.get(o.get('classID'),o.get('classID'))} aabm=0x{o.get('aabm',0):x}"
            if cat=='hulls': extra=f" mounts={o.get('maxWeapons')} hardpoints={[hex(x) for x in o.get('hardpoints',[])]}"
            print(f"  price={o.get('price',0):7d} req={o.get('req')} eff={o.get('eff')}{extra}  {o['name']!r}")
        return
    print(f"version={ver} datasize={ds} records={len(recs)}")
    print("counts:", dict(Counter(OT.get(t,f'?{t}') for t,_,_ in recs)))
    print("civilizations:", [f"{c['name']}(id {c['civID']})" for c in data['civs']])

if __name__=='__main__':
    main()
