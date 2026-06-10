# Constants, Enums & Struct Layouts

Everything the flight model references, pulled verbatim from the engine headers so this folder is
self-contained. Axis indices, control input format, button bitmask, hull stat struct, afterburner
stat struct, and the global mission float-constants table.

---

## Axis indices

Used to index `maxTurnRates[]`, `turnTorques[]`, `m_turnRates[]`, and `ControlData.jsValues[]`.

```cpp
typedef unsigned char Axis;
const Axis c_axisYaw      = 0;
const Axis c_axisPitch    = 1;
const Axis c_axisRoll     = 2;
const Axis c_axisThrottle = 3;   // jsValues only; not a rotation axis
const Axis c_axisMax      = 4;
```

## Control input — `ControlData`

The analog stick state. Each value is clamped to **[-1, 1]**. Throttle **rests at -1.0** (full forward
in the engine's sign convention; see throttle math in `01_flight_model.md`).

```cpp
struct ControlData {
    float jsValues[4];   // [yaw, pitch, roll, throttle]
    void Reset() {
        jsValues[c_axisYaw] = jsValues[c_axisPitch] = jsValues[c_axisRoll] = 0.0f;
        jsValues[c_axisThrottle] = -1.0f;
    }
};
```

## Button bitmask — `ShipControlStateIGC` (`m_stateM`)

Digital button state. Each constant is defined relative to the previous (`coastButtonIGC` is the base
unit for the movement buttons). Only the movement-relevant bits are reproduced; the trailing weapon/
cloak/mining bits are listed for completeness of the bitmask.

```cpp
enum ShipControlStateIGC {
    selectedWeaponOneIGC  = 1,
    selectedWeaponTwoIGC  = 2 * selectedWeaponOneIGC,    // = 2
    selectedWeaponMaskIGC = (2 * selectedWeaponTwoIGC)-1,// = 3
    selectedWeaponShiftIGC= 0,

    coastButtonIGC        =   2 * selectedWeaponTwoIGC,  // = 4    (vector lock / coast)
    backwardButtonIGC     =   2 * coastButtonIGC,        // = 8    thrust back
    forwardButtonIGC      =   4 * coastButtonIGC,        // = 16   thrust forward
    leftButtonIGC         =   8 * coastButtonIGC,        // = 32   strafe left
    rightButtonIGC        =  16 * coastButtonIGC,        // = 64   strafe right
    upButtonIGC           =  32 * coastButtonIGC,        // = 128  strafe up
    downButtonIGC         =  64 * coastButtonIGC,        // = 256  strafe down
    afterburnerButtonIGC  = 128 * coastButtonIGC,        // = 512  afterburner
    // ---- non-movement bits (for completeness) ----
    keyMaskIGC            = (256 * coastButtonIGC - 4),
    drillingMaskIGC       = 256 * coastButtonIGC,        // on rails (no collisions)
    cloakActiveIGC        = 512 * coastButtonIGC,
    droneRipMaskIGC       = 1024 * coastButtonIGC,
    miningMaskIGC         = 2048 * coastButtonIGC,
    buttonsMaskIGC        = 4095 * coastButtonIGC,
};
```

Resolved numeric values for the movement bits:

| Button | Value |
|---|---|
| `coastButtonIGC` | 4 |
| `backwardButtonIGC` | 8 |
| `forwardButtonIGC` | 16 |
| `leftButtonIGC` | 32 |
| `rightButtonIGC` | 64 |
| `upButtonIGC` | 128 |
| `downButtonIGC` | 256 |
| `afterburnerButtonIGC` | 512 |

---

## Hull stat struct — `DataHullTypeIGC`

The fully-resolved per-ship stats consumed by the physics. Motion-relevant fields are flagged ★.
(Stored values are pre-converted: turn rates in **radians/s**, thrust in **Newtons** — see
`04_data_schema.md` for how they're derived from the raw design CSV.)

```cpp
struct DataHullTypeIGC : public DataBuyableIGC {
    float  mass;                 // ★ kg-ish; collisions only (cancels from accel/turn — see 04)
    float  signature;            //   detectability
    float  speed;                // ★ maxSpeed (terminal velocity)
    float  maxTurnRates[3];      // ★ yaw, pitch, roll — max angular velocity (rad/s)
    float  turnTorques[3];       // ★ yaw, pitch, roll — angular-accel term (see 04)
    float  thrust;               // ★ engine force (Newtons) = mass * acceleration
    float  sideMultiplier;       // ★ strafe thrust fraction (<1)
    float  backMultiplier;       // ★ reverse thrust fraction (<1)
    float  scannerRange;         //   sensors
    float  maxFuel;              //   afterburner fuel
    float  ecm;                  //   countermeasures
    float  length;               //   collision/visual size
    float  maxEnergy;            //   weapon/AB energy pool
    float  rechargeRate;         //   energy regen
    float  ripcordSpeed;         //   warp-to-base time factor
    float  ripcordCost;
    short  maxAmmo;
    HullID hullID, successorHullID;
    Mount  maxWeapons, maxFixedWeapons;
    HitPoints hitPoints;
    short  hardpointOffset;
    DefenseTypeID defenseType;
    short  capacityMagazine, capacityDispenser, capacityChaffLauncher;
    PartID preferredPartsTypes[c_cMaxPreferredPartTypes];
    HullAbilityBitMask habmCapabilities;
    char   textureName[c_cbFileName];
    PartMask pmEquipment[ET_MAX];
    SoundID interiorSound, exteriorSound,
            mainThrusterInteriorSound, mainThrusterExteriorSound,
            manuveringThrusterInteriorSound, manuveringThrusterExteriorSound;
    // HardpointData[] follows in memory
};
```

## Afterburner stat struct — `DataAfterburnerTypeIGC`

```cpp
struct DataAfterburnerTypeIGC : public DataPartTypeIGC {
    float   fuelConsumption;   // fuel/sec = power * fuelConsumption * maxThrust * dt
    float   maxThrust;         // extra forward thrust (Newtons) at full power
    float   onRate;            // power ramp-up  per second (power += dt*onRate, clamp 1)
    float   offRate;           // power ramp-down per second (power -= dt*offRate, clamp 0)
    SoundID interiorSound, exteriorSound;
};
```

Afterburner effect on flight: while held, target speed becomes `maxSpeed * (1 + maxThrust/thrust)`,
and `power*maxThrust` is added along the ship's backward axis into the drag/engine calc.

---

## Per-team Global Attributes (tech upgrades)

Every motion getter multiplies the raw stat by a Global Attribute, **default 1.0**. Research raises
them mid-game. For a baseline homage set all to 1.0.

| Getter | Multiplier ID |
|---|---|
| `GetMaxSpeed()` | `c_gaMaxSpeed` |
| `GetMaxTurnRate()` | `c_gaTurnRate` |
| `GetTurnTorque()` | `c_gaTurnTorque` |
| `GetThrust()` | `c_gaThrust` |
| afterburner `GetMaxThrustWithGA()` | `c_gaThrust` |

---

## Mission float-constants (`FloatConstantID`)

Global tuning values loaded from `Constants.csv` (per core). The movement-relevant ones:

| ID | Value | Used for |
|---|---|---|
| `c_fcidExitWarpSpeed` | 3 | speed when exiting warp |
| `c_fcidExitStationSpeed` | 4 | speed when launching from a station |
| `c_fcidRipcordTime` | 13 | multiplies `ripcordSpeed` → warp charge time |
| `c_fcidMountRate` | 10 | also drives afterburner "mount"/ready ramp |
| `c_fcidLifepodEndurance` | 31 | ejected-pod lifetime |
| `c_fcidRadiusUniverse` | 1 | sector size |
| `c_fcidOutOfBounds` | 2 | boundary distance |
| `c_fcidMax` | 40 | size of the table |

(Full ID list 0–39 exists; only the flight-relevant subset is shown. These IDs are indices into the
`Constants.csv` table — the actual float values come with the core.)

---

## Closed-form drag (useful for prediction / netcode)

The discrete exponential drag has an exact continuous solution (from the ripcord-drift calc):

```
k     = thrust / (maxSpeed * mass)      // note: with thrust = mass*accel, k = accel/maxSpeed
V(t)  = V0 * exp(-k * t)                // velocity decay when coasting (no engine)
S(t)  = V0 * (1 - exp(-k * t)) / k      // distance traveled while decaying
```

This is the same drag the per-tick `f = exp(-thrust*dT/(mass*maxSpeed))` approximates each frame.
