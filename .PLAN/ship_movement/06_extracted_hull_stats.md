# Extracted Hull Stats (from artwork/tester.igc)

Pulled directly from the static core with extract_hulls.py. 74 ship types, 379 hull
records total (most ships appear once per civilization, ~9 civs). Turn rates shown in
deg/s; **driftYaw** = derived overshoot angle (smaller = snappier). Where a name has
multiple distinct stat-lines they are civilization/variant differences.

Full per-record data incl. all civilizations is in `hull_stats.csv`.

## Scout

| Ship | maxSpeed | accel | yaw°/s | pitch°/s | roll°/s | driftYaw° | side | back | mass | variants |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Adv Scout | 180 | 30 | 50 | 50 | 50 | 5 | 0.5 | 0.25 | 40 | 4 |
| Adv Scout [SR] | 180 | 30 | 75 | 25 | 75 | 10 | 0.5 | 0.25 | 40 | 1 |
| Hvy Scout | 180 | 36 | 50 | 50 | 50 | 5 | 0.5 | 0.25 | 40 | 4 |
| Hvy Scout [SR] | 180 | 36 | 75 | 25 | 75 | 10 | 0.5 | 0.25 | 40 | 1 |
| Lxy Patroller | 125 | 32.5 | 60 | 60 | 60 | 5 | 0.5 | 0.5 | 36 | 1 |
| Lxy Scout | 180 | 36 | 50 | 50 | 50 | 5 | 0.5 | 0.25 | 40 | 1 |
| Patroller | 110 | 30 | 60 | 60 | 60 | 5 | 0.5 | 0.5 | 36 | 1 |
| Scout | 160 | 30 | 50 | 50 | 50 | 5 | 0.5 | 0.25 | 40 | 4 |
| Scout [SR] | 160 | 30 | 75 | 25 | 75 | 10 | 0.5 | 0.25 | 40 | 1 |

## Interceptor

| Ship | maxSpeed | accel | yaw°/s | pitch°/s | roll°/s | driftYaw° | side | back | mass | variants |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Hvy Interceptor | 90 | 15 | 60 | 60 | 60 | 5 | 1 | 1 | 20 | 5 |
| Interceptor | 80 | 15 | 60 | 60 | 60 | 5 | 1 | 1 | 20 | 4 |
| Lt Interceptor | 75 | 15 | 60 | 60 | 60 | 5 | 1 | 1 | 20 | 3 |
| Lxy Interceptor | 90 | 15 | 60 | 60 | 60 | 5 | 1 | 1 | 20 | 1 |

## Fighter

| Ship | maxSpeed | accel | yaw°/s | pitch°/s | roll°/s | driftYaw° | side | back | mass | variants |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Adv Fighter | 120 | 25 | 60 | 60 | 60 | 5 | 0.5 | 0.5 | 36 | 5 |
| Enh Fighter | 100 | 25 | 60 | 60 | 60 | 5 | 0.5 | 0.5 | 36 | 5 |
| Fighter | 100 | 25 | 60 | 60 | 60 | 5 | 0.5 | 0.5 | 36 | 5 |
| Lxy Fighter | 120 | 25 | 60 | 60 | 60 | 5 | 0.5 | 0.5 | 36 | 1 |
| Omni Fighter | 120 | 16 | 60 | 60 | 60 | 5 | 1 | 1 | 64 | 1 |

## Stealth Fighter

| Ship | maxSpeed | accel | yaw°/s | pitch°/s | roll°/s | driftYaw° | side | back | mass | variants |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Adv Stl Fighter | 160 | 36 | 30 | 30 | 30 | 6 | 0.5 | 0.25 | 60 | 4 |
| Lt Stl Fighter | 120 | 38.6 | 30 | 30 | 30 | 5.6 | 0.5 | 0.25 | 56 | 2 |
| Lxy Stl Fighter | 160 | 36 | 30 | 30 | 30 | 6 | 0.5 | 0.25 | 60 | 1 |
| Stealth Bomber | 80 | 18 | 30 | 30 | 30 | 7 | 0.75 | 0.5 | 120 | 4 |
| Stealth Bomber [NG] | 80 | 18 | 30 | 30 | 30 | 7 | 0.75 | 0.5 | 120 | 4 |
| Stealth Fighter | 140 | 36 | 30 | 30 | 30 | 6 | 0.5 | 0.25 | 60 | 5 |

## Bomber

| Ship | maxSpeed | accel | yaw°/s | pitch°/s | roll°/s | driftYaw° | side | back | mass | variants |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Bomber | 60 | 15 | 20 | 20 | 20 | 8 | 0.5 | 0.5 | 50 | 3 |
| Fighter/Bomber | 70 | 25 | 60 | 60 | 60 | 5.6 | 0.5 | 0.5 | 40 | 5 |
| Hvy Bomber | 75 | 15 | 25 | 25 | 25 | 8 | 0.5 | 0.5 | 50 | 3 |
| PT Bomber | 120 | 25 | 43 | 43 | 43 | 3.3 | 0.5 | 0.5 | 50 | 1 |

## Gunship

| Ship | maxSpeed | accel | yaw°/s | pitch°/s | roll°/s | driftYaw° | side | back | mass | variants |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Gunship | 80 | 20 | 37 | 37 | 37 | 4.5 | 0.5 | 0.5 | 50 | 3 |

## Miner

| Ship | maxSpeed | accel | yaw°/s | pitch°/s | roll°/s | driftYaw° | side | back | mass | variants |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Adv Miner | 120 | 10 | 24 | 24 | 24 | 12 | 1 | 0.5 | 100 | 1 |
| Adv Miner [SR] | 120 | 12.5 | 24 | 24 | 24 | 9.6 | 1 | 0.5 | 80 | 1 |
| Enh Miner | 100 | 10 | 24 | 24 | 24 | 12 | 1 | 0.5 | 100 | 1 |
| Enh Miner [SR] | 100 | 12.5 | 24 | 24 | 24 | 9.6 | 1 | 0.5 | 80 | 1 |
| Miner | 100 | 10 | 24 | 24 | 24 | 12 | 1 | 0.5 | 100 | 1 |
| Miner [SR] | 100 | 12.5 | 24 | 24 | 24 | 9.6 | 1 | 0.5 | 80 | 1 |

## Constructor

| Ship | maxSpeed | accel | yaw°/s | pitch°/s | roll°/s | driftYaw° | side | back | mass | variants |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Lg Adv Constructor | 100 | 10 | 18 | 18 | 18 | 12 | 1 | 0.5 | 50000 | 2 |
| Lg Enh Constructor | 80 | 10 | 18 | 18 | 18 | 12 | 1 | 0.5 | 50000 | 2 |
| Lg Std Constructor | 60 | 10 | 18 | 18 | 18 | 12 | 1 | 0.5 | 50000 | 2 |
| Sm Adv Constructor | 100 | 10 | 18 | 18 | 18 | 12 | 1 | 0.5 | 200 | 2 |
| Sm Enh Constructor | 80 | 10 | 18 | 18 | 18 | 12 | 1 | 0.5 | 200 | 2 |
| Sm Std Constructor | 60 | 10 | 18 | 18 | 18 | 12 | 1 | 0.5 | 200 | 2 |

## Utility / Transport

| Ship | maxSpeed | accel | yaw°/s | pitch°/s | roll°/s | driftYaw° | side | back | mass | variants |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Cargo Container | 160 | 30 | 6 | 6 | 6 | 10 | 0.5 | 0.25 | 250 | 1 |
| Freighter | 120 | 10 | 37 | 37 | 37 | 63.3 | 0.5 | 0.5 | 1250 | 2 |
| Hvy Troop Tran [AP] | 75 | 15 | 35 | 35 | 35 | 20 | 0.75 | 0.75 | 180 | 1 |
| Hvy Troop Transport | 75 | 15 | 35 | 35 | 35 | 20 | 0.5 | 0.5 | 90 | 3 |
| Troop Transport | 75 | 15 | 35 | 35 | 35 | 10 | 0.5 | 0.5 | 45 | 3 |

## Capital / Heavy

| Ship | maxSpeed | accel | yaw°/s | pitch°/s | roll°/s | driftYaw° | side | back | mass | variants |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Adv Devastator | 75 | 10 | 8 | 8 | 8 | 10 | 0.5 | 0.5 | 5000 | 1 |
| Assault Carrier | 100 | 10 | 12 | 12 | 12 | 25.9 | 0.5 | 0.5 | 5000 | 1 |
| Assault Carrier Drone | 70 | 10 | 18 | 18 | 18 | 12 | 0.5 | 0.5 | 50000 | 1 |
| Assault Ship | 100 | 15 | 12 | 12 | 12 | 9 | 0.5 | 0.5 | 2500 | 2 |
| Attack Carrier [SR] | 75 | 10 | 12 | 12 | 12 | 25.9 | 0.5 | 0.5 | 5000 | 3 |
| Battlecruiser | 65 | 10 | 8 | 8 | 8 | 12 | 0.5 | 0.5 | 6000 | 3 |
| Battleship | 70 | 10 | 8 | 8 | 8 | 14 | 0.5 | 0.5 | 7000 | 3 |
| Carrier [SR] | 50 | 10 | 18 | 18 | 18 | 12 | 0.5 | 0.5 | 50000 | 1 |
| Corvette | 90 | 20 | 37 | 37 | 37 | 31.6 | 0.5 | 0.5 | 625 | 2 |
| Cruiser | 75 | 10 | 8 | 8 | 8 | 10 | 0.5 | 0.5 | 5000 | 2 |
| Destroyer | 75 | 10 | 10 | 10 | 10 | 9 | 0.5 | 0.5 | 2500 | 2 |
| Devastator | 75 | 10 | 10 | 10 | 10 | 9 | 0.5 | 0.5 | 2500 | 1 |
| Enh Carrier [SR] | 70 | 10 | 18 | 18 | 18 | 12 | 0.5 | 0.5 | 50000 | 1 |
| Flagship.1 [AtFr] | 75 | 25 | 10 | 10 | 10 | 6.8 | 0.5 | 0.5 | 2000 | 1 |
| Flagship.2 [AtDv] | 75 | 25 | 10 | 10 | 10 | 6.8 | 0.5 | 0.5 | 2000 | 1 |
| Flagship.3 [AtCr] | 70 | 12.5 | 10 | 10 | 10 | 13.6 | 0.5 | 0.5 | 4000 | 1 |
| Flagship.4 [AsCr] | 70 | 8.9 | 10 | 10 | 10 | 19.1 | 0.5 | 0.5 | 5600 | 1 |
| Frigate | 85 | 10 | 8 | 8 | 8 | 9 | 0.5 | 0.5 | 2500 | 2 |
| Guardian | 20 | 5 | 57 | 57 | 57 | 6.6 | 0.5 | 0.5 | 40 | 1 |
| Harbinger of Doom | 60 | 10 | 8 | 8 | 8 | 7.9 | 0.5 | 0.5 | 2500 | 1 |
| Lt Carrier [SR] | 50 | 10 | 18 | 18 | 18 | 12 | 0.5 | 0.5 | 50000 | 1 |
| Lt Cruiser | 85 | 10 | 8 | 8 | 8 | 10 | 0.5 | 0.5 | 2500 | 1 |

## Misc / Drone

| Ship | maxSpeed | accel | yaw°/s | pitch°/s | roll°/s | driftYaw° | side | back | mass | variants |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Derelict Hull | 160 | 30 | 6 | 6 | 6 | 10 | 0.5 | 0.25 | 400 | 1 |
| Lifepod | 60 | 15 | 40 | 40 | 40 | 8 | 1 | 1 | 10 | 4 |
| Mine Layer Drone | 100 | 25 | 32 | 32 | 32 | 12 | 1 | 1 | 30 | 1 |
| Rescue Drone | 60 | 25 | 32 | 32 | 32 | 12 | 1 | 0.5 | 30 | 1 |
| Tower Layer Drone | 100 | 25 | 32 | 32 | 32 | 12 | 1 | 1 | 30 | 2 |

## Other

| Ship | maxSpeed | accel | yaw°/s | pitch°/s | roll°/s | driftYaw° | side | back | mass | variants |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Mustang | 120 | 30.2 | 43 | 43 | 43 | 3.1 | 0.5 | 0.5 | 43 | 1 |
