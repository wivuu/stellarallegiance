# Allegiance Audio Index

Searchable catalog of every audio file under `pick-assets/sound-effects` (**1142 `.ogg` files**), grouped and tagged by **how the engine actually uses each one** — derived from the sound-definition scripts and the IGC sound bindings, not filename guesswork.

## How audio is wired (the real pipeline)

1. **Game code fires a sound-event ID.** `src/Igc/sounds.h` declares events with the `DEFSOUND(x)` macro (e.g. `DEFSOUND(myShieldHit)` → `myShieldHitSound`). Gameplay code plays these by ID.
2. **`soundList` binds IDs → logical sounds.** In `pick-assets/sounddef.mdl` the `soundList` table maps every IGC id to a logical sound definition, e.g. `(myShieldHitSoundId, myShieldHitSound)`.
3. **Logical sounds wrap the `.ogg` with playback semantics.** e.g. `myShieldHitSound = GainSound(SFXGain, PrioritySound(-1.5, ThreeDSound(150, RandomSound([ImportWave("hitmeshield1")...]))))`. The wrappers encode the *real* behavior:
   - **Gain bus** — `SFXGain` (effects) vs `VoiceOverGain` (spoken) vs `InterfaceGain`.
   - **`ThreeDSound(range, …)`** — positional 3D audio with an audible range in meters.
   - **`LoopingSound` / `IntermittentSound` / `RandomSound`** — ambient beds and randomized variant pools.
   - **`SerializedSound(MutexSal|MutexVO, timeout, …)`** — voice-over queued through a mutex so lines never overlap (`MutexSal` = station/commander announcer, `MutexVO` = player/pilot radio).
   - **`RepeatingFireSound` / `ASRSound` / `…BurstSound`** — sustained/automatic weapon fire.
   - **`ActivationSound` / `…pwrup`** — weapon spin-up / charge.
4. **Four definition files** feed the sound map:
   - `sounddef.mdl` — master runtime SFX + voice-over + ambient (the default game).
   - `sounddefvalor.mdl` — the *Valor* variant/mod of the same map.
   - `trainingsounddef.mdl` — the **272** `tm_*` music & narration tracks used by the tutorial.
   - `notrainingsounddef.mdl` — silent stand-ins when training audio is absent.
5. **Music = training slideshow narration.** The `tm_N_*.mdl` files (e.g. `tm_2_basic_flight.mdl`, `tm_4_enemy_engagement.mdl`) are slideshow scripts that pair a slide image with a `tm_slide_*SoundId`; the `tm_slide_*` and `tm_*` tracks are the music/voice for each tutorial mission. Loaded through `src/WinTrek/soundinit.cpp` → `treksound.cpp` → `src/soundengine`.


## Coverage

| Metric | Count |
|---|---:|
| Total `.ogg` files on disk | 1142 |
| Bound to a logical sound (actually used) | 1096 |
| Unused / orphaned (present but not referenced) | 46 |
| Referenced in a script but no matching `.ogg` (broken/legacy/`.wav`) | 39 |

### By usage group

| Group | Files |
|---|---:|
| Commander voice-over (SAL system) | 183 |
| Player & comms voice-over (radio callouts) | 236 |
| Mission-advisor voice-over | 69 |
| Builder voice-over | 15 |
| Miner voice-over | 8 |
| Ambient / environmental loops | 148 |
| Weapons (fire / burst / power-up) | 97 |
| Positional 3D sound effects | 35 |
| UI & 2D sound effects | 35 |
| Music & training slideshow tracks | 270 |
| Unused / orphaned assets | 46 |

**Tag legend for searching:** `commander-vo`, `player-vo`, `mission-advisor`, `miner-vo`, `builder-vo`, `callout`, `announcement`, `countdown`, `ambient`, `loop`, `weapon`, `power-up`, `weapon-fire`, `impact`, `hit`, `creature`, `faction`, `music`, `training-slideshow`, `radio-static`, `effect`, `ctf`, `dark-nebula` (mod).

## Commander voice-over (SAL system)

| File | Logical sound(s) | Bus | Playback role | IGC event | Tags |
|---|---|---|---|---|---|
| `countdown1.ogg` | countdown1Sound, ripcord1Sound | VoiceOver | Commander VO (SAL, serialized) | countdown1, ripcord1 | countdown, timer |
| `countdown10.ogg` | countdown10Sound, ripcord10Sound | VoiceOver | Commander VO (SAL, serialized) | countdown10, ripcord10 | countdown, timer |
| `countdown2.ogg` | countdown2Sound, ripcord2Sound | VoiceOver | Commander VO (SAL, serialized) | countdown2, ripcord2 | countdown, timer |
| `countdown3.ogg` | countdown3Sound, ripcord3Sound | VoiceOver | Commander VO (SAL, serialized) | countdown3, ripcord3 | countdown, timer |
| `countdown4.ogg` | countdown4Sound, ripcord4Sound | VoiceOver | Commander VO (SAL, serialized) | countdown4, ripcord4 | countdown, timer |
| `countdown5.ogg` | countdown5Sound, ripcord5Sound | VoiceOver | Commander VO (SAL, serialized) | countdown5, ripcord5 | countdown, timer |
| `countdown6.ogg` | countdown6Sound, ripcord6Sound | VoiceOver | Commander VO (SAL, serialized) | countdown6, ripcord6 | countdown, timer |
| `countdown7.ogg` | countdown7Sound, ripcord7Sound | VoiceOver | Commander VO (SAL, serialized) | countdown7, ripcord7 | countdown, timer |
| `countdown8.ogg` | countdown8Sound, ripcord8Sound | VoiceOver | Commander VO (SAL, serialized) | countdown8, ripcord8 | countdown, timer |
| `countdown9.ogg` | countdown9Sound, ripcord9Sound | VoiceOver | Commander VO (SAL, serialized) | countdown9, ripcord9 | countdown, timer |
| `dn_vo_attack_transeiver.ogg` | dn_tranAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo, dark-nebula, mod |
| `dn_vo_attack_transeiverbuilder.ogg` | dn_tranBuilderAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo, dark-nebula, mod |
| `dn_vo_builder_transeiver.ogg` | dn_tranCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | builder-vo, dark-nebula, mod |
| `dn_vo_capture_enemytranseiver.ogg` | dn_tranEnemyCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo, dark-nebula, mod |
| `dn_vo_capture_transeiver.ogg` | dn_tranCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo, dark-nebula, mod |
| `dn_vo_destroy_enemytranseiver.ogg` | dn_tranEnemyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, dark-nebula, destroy, mod |
| `dn_vo_destroy_transeiver.ogg` | dn_tranBuilderDestroyedSound, dn_tranDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, dark-nebula, destroy, mod |
| `drop.ogg` | dropSound | SFX | Commander VO (SAL, serialized) | drop | — |
| `vo_attack_expansion.ogg` | expansionAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_expansionbuilder.ogg` | expansionBuilderAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_garrison.ogg` | garrisonAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_garrisonbuilder.ogg` | garrisonBuilderAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_mine.ogg` | mineAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_minebuilder.ogg` | mineBuilderAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_miner.ogg` | minerUnderAttackSound | VoiceOver | Commander VO (SAL, serialized) | minerUnderAttack | attack-order, commander-vo |
| `vo_attack_outpost.ogg` | outpostAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_outpostbuilder.ogg` | outpostBuilderAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_palisade.ogg` | palisadeAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_palisadebuilder.ogg` | palisadeBuilderAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_refinery.ogg` | refineryAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_refinerybuilder.ogg` | refineryBuilderAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_research.ogg` | researchAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_researchbuilder.ogg` | researchBuilderAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_shipyard.ogg` | shipyardAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_shipyardbuilder.ogg` | shipyardBuilderAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_supremecy.ogg` | supremecyAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_supremecybuilder.ogg` | supremecyBuilderAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_tactical.ogg` | tacticalAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_tacticalbuilder.ogg` | tacticalBuilderAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_teleport.ogg` | teleportAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_attack_teleportbuilder.ogg` | teleportBuilderAttackedSound | VoiceOver | Commander VO (SAL, serialized) | — | attack-order, commander-vo |
| `vo_capture_enemyexpansion.ogg` | expansionEnemyCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_enemygarrison.ogg` | garrisonEnemyCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_enemymine.ogg` | mineEnemyCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_enemyoutpost.ogg` | outpostEnemyCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_enemypalisade.ogg` | palisadeEnemyCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_enemyresearch.ogg` | researchEnemyCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_enemyshipyard.ogg` | shipyardEnemyCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_enemysupremecy.ogg` | supremecyEnemyCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_enemytactical.ogg` | tacticalEnemyCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_expansion.ogg` | expansionCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_garrison.ogg` | garrisonCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_mine.ogg` | mineCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_outpost.ogg` | outpostCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_palisade.ogg` | palisadeCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_research.ogg` | researchCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_shipyard.ogg` | shipyardCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_supremecy.ogg` | supremecyCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_capture_tactical.ogg` | tacticalCapturedSound | VoiceOver | Commander VO (SAL, serialized) | — | capture, commander-vo |
| `vo_cover_builder.ogg` | constructorRunningSound | VoiceOver | Commander VO (SAL, serialized) | constructorRunning | — |
| `vo_critical_station.ogg` | dn_tranCritical, garrisonCritical | VoiceOver | Commander VO (SAL, serialized) | — | — |
| `vo_destroy_carrier.ogg` | carrierDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | carrierDestroyed | commander-vo, destroy |
| `vo_destroy_enemyexpansion.ogg` | expansionEnemyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_enemygarrison.ogg` | garrisonEnemyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_enemymine.ogg` | mineEnemyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_enemyoutpost.ogg` | outpostEnemyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_enemypalisade.ogg` | palisadeEnemyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_enemyrefinery.ogg` | refineryEnemyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_enemyresearch.ogg` | researchEnemyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_enemyshipyard.ogg` | shipyardEnemyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_enemysupremecy.ogg` | supremecyEnemyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_enemytactical.ogg` | tacticalEnemyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_enemyteleport.ogg` | teleportEnemyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_expansion.ogg` | expansionBuilderDestroyedSound, expansionDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_garrison.ogg` | garrisonBuilderDestroyedSound, garrisonDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_mine.ogg` | mineDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_minebuilder.ogg` | mineBuilderDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_miner.ogg` | minerDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | minerDestroyed | commander-vo, destroy |
| `vo_destroy_outpost.ogg` | outpostBuilderDestroyedSound, outpostDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_palisade.ogg` | palisadeBuilderDestroyedSound, palisadeDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_refinery.ogg` | refineryBuilderDestroyedSound, refineryDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_research.ogg` | researchBuilderDestroyedSound, researchDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_shipyard.ogg` | shipyardBuilderDestroyedSound, shipyardDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_supremecy.ogg` | supremecyBuilderDestroyedSound, supremecyDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_tactical.ogg` | tacticalBuilderDestroyedSound, tacticalDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_destroy_teleport.ogg` | teleportBuilderDestroyedSound, teleportDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | — | commander-vo, destroy |
| `vo_detect_builder.ogg` | constructorThreatenedSound | VoiceOver | Commander VO (SAL, serialized) | constructorThreatened | — |
| `vo_detect_miner.ogg` | minerThreatenedSound | VoiceOver | Commander VO (SAL, serialized) | minerThreatened | — |
| `vo_request_buildercarbon.ogg` | constructorReqCarbonSound | VoiceOver | Commander VO (SAL, serialized) | — | request-vo |
| `vo_request_buildergeneric.ogg` | constructorReqGenericSound | VoiceOver | Commander VO (SAL, serialized) | — | request-vo |
| `vo_request_builderhelium.ogg` | constructorReqHeliumSound | VoiceOver | Commander VO (SAL, serialized) | — | request-vo |
| `vo_request_buildersilicon.ogg` | constructorReqSiliconSound | VoiceOver | Commander VO (SAL, serialized) | — | request-vo |
| `vo_request_builderuranium.ogg` | constructorReqUraniumSound | VoiceOver | Commander VO (SAL, serialized) | — | request-vo |
| `vo_request_minefield.ogg` | droneWhereToLayMinefieldSound | VoiceOver | Commander VO (SAL, serialized) | droneWhereToLayMinefield | request-vo |
| `vo_request_miner.ogg` | droneTooCrowdedSound, droneWhereToSound | VoiceOver | Commander VO (SAL, serialized) | droneTooCrowded, droneWhereTo | request-vo |
| `vo_request_special.ogg` | constructorReqSpecialSound | VoiceOver | Commander VO (SAL, serialized) | — | request-vo |
| `vo_request_tower.ogg` | droneWhereToLayTowerSound | VoiceOver | Commander VO (SAL, serialized) | droneWhereToLayTower | request-vo |
| `vo_sal_artifact.ogg` | artifactFoundSound | VoiceOver | Commander VO (SAL, serialized) | artifactFound | announcement, commander-vo, sal |
| `vo_sal_autopilotdisengage.ogg` | salAutopilotDisengageSound | VoiceOver | Commander VO (SAL, serialized) | salAutopilotDisengage | announcement, commander-vo, sal |
| `vo_sal_autopilotengage.ogg` | salAutopilotEngageSound | VoiceOver | Commander VO (SAL, serialized) | salAutopilotEngage | announcement, commander-vo, sal |
| `vo_sal_bombersighted.ogg` | bomberDetectedSound | VoiceOver | Commander VO (SAL, serialized) | bomberDetected | announcement, commander-vo, sal |
| `vo_sal_booted.ogg` | salBootedSound | VoiceOver | Commander VO (SAL, serialized) | salBooted | announcement, commander-vo, sal |
| `vo_sal_boundryexceeded.ogg` | salBoundryExceededSound | VoiceOver | Commander VO (SAL, serialized) | salBoundryExceeded | announcement, commander-vo, sal |
| `vo_sal_capitalsighted.ogg` | capitalDetectedSound | VoiceOver | Commander VO (SAL, serialized) | capitalDetected | announcement, commander-vo, sal |
| `vo_sal_cargoejected.ogg` | dropSound | SFX | Commander VO (SAL, serialized) | drop | announcement, commander-vo, sal |
| `vo_sal_cargofull.ogg` | salCargoFullSound | VoiceOver | Commander VO (SAL, serialized) | salCargoFull | announcement, commander-vo, sal |
| `vo_sal_carrieratrisk.ogg` | carrierThreatenedSound | VoiceOver | Commander VO (SAL, serialized) | carrierThreatened | announcement, commander-vo, sal |
| `vo_sal_carrierattack.ogg` | carrierUnderAttackSound | VoiceOver | Commander VO (SAL, serialized) | carrierUnderAttack | announcement, commander-vo, sal |
| `vo_sal_carrierdetected.ogg` | carrierDetectedSound | VoiceOver | Commander VO (SAL, serialized) | carrierDetected | announcement, commander-vo, sal |
| `vo_sal_chaffdepleted.ogg` | salChaffDepletedSound | VoiceOver | Commander VO (SAL, serialized) | salChaffDepleted | announcement, commander-vo, sal |
| `vo_sal_clearedwall.ogg` | salClearedShieldWallSound | VoiceOver | Commander VO (SAL, serialized) | salClearedShieldWall | announcement, commander-vo, sal |
| `vo_sal_cloakdisengage.ogg` | salCloakDisengageSound | VoiceOver | Commander VO (SAL, serialized) | salCloakDisengage | announcement, commander-vo, sal |
| `vo_sal_cloakengage.ogg` | salCloakEngageSound | VoiceOver | Commander VO (SAL, serialized) | salCloakEngage | announcement, commander-vo, sal |
| `vo_sal_commencescan.ogg` | salCommenceScanSound | VoiceOver | Commander VO (SAL, serialized) | salCommenceScan | announcement, commander-vo, sal |
| `vo_sal_completeddev.ogg` | salCompletedEquipmentSound | VoiceOver | Commander VO (SAL, serialized) | salCompletedEquipment | announcement, commander-vo, sal |
| `vo_sal_completeddramatic.ogg` | salCompletedDramaticSound | VoiceOver | Commander VO (SAL, serialized) | salCompletedDramatic | announcement, commander-vo, sal |
| `vo_sal_completedfabrication.ogg` | salCompletedShipSound | VoiceOver | Commander VO (SAL, serialized) | salCompletedShip | announcement, commander-vo, sal |
| `vo_sal_completedmod.ogg` | salCompletedModsSound | VoiceOver | Commander VO (SAL, serialized) | salCompletedMods | announcement, commander-vo, sal |
| `vo_sal_completedradical.ogg` | salCompletedRadicalSound | VoiceOver | Commander VO (SAL, serialized) | salCompletedRadical | announcement, commander-vo, sal |
| `vo_sal_contactsinrange.ogg` | salContactsInRangeSound | VoiceOver | Commander VO (SAL, serialized) | salContactsInRange | announcement, commander-vo, sal |
| `vo_sal_coremeltdown.ogg` | salCoreMeltdownSound | VoiceOver | Commander VO (SAL, serialized) | salCoreMeltdown | announcement, commander-vo, sal |
| `vo_sal_crimsonsighted.ogg` | salCrimsonSightedSound | VoiceOver | Commander VO (SAL, serialized) | salCrimsonSighted | announcement, commander-vo, sal |
| `vo_sal_dispenserempty.ogg` | salDispenserEmptySound | VoiceOver | Commander VO (SAL, serialized) | salDispenserEmpty | announcement, commander-vo, sal |
| `vo_sal_enemyjoiners.ogg` | salEnemyJoinersSound | VoiceOver | Commander VO (SAL, serialized) | salEnemyJoiners | announcement, commander-vo, sal |
| `vo_sal_enemyleaves.ogg` | salEnemyLeavesSound | VoiceOver | Commander VO (SAL, serialized) | salEnemyLeaves | announcement, commander-vo, sal |
| `vo_sal_enemytroops.ogg` | salEnemyTroopsSound | VoiceOver | Commander VO (SAL, serialized) | salEnemyTroops | announcement, commander-vo, sal |
| `vo_sal_enteredwall.ogg` | salEnteredShieldWallSound | VoiceOver | Commander VO (SAL, serialized) | salEnteredShieldWall | announcement, commander-vo, sal |
| `vo_sal_hullcritical.ogg` | hullLowSound | VoiceOver | Commander VO (SAL, serialized) | hullLow | announcement, commander-vo, sal |
| `vo_sal_ironsighted.ogg` | salIronSightedSound | VoiceOver | Commander VO (SAL, serialized) | salIronSighted | announcement, commander-vo, sal |
| `vo_sal_lowenergy.ogg` | energyLowSound | VoiceOver | Commander VO (SAL, serialized) | energyLow | announcement, commander-vo, sal |
| `vo_sal_madecommander.ogg` | commanderSound | VoiceOver | Commander VO (SAL, serialized) | commander | announcement, commander-vo, sal |
| `vo_sal_madeinvestor.ogg` | investorSound | VoiceOver | Commander VO (SAL, serialized) | investor | announcement, commander-vo, sal |
| `vo_sal_mia.ogg` | salMIASound, salQuitSound | VoiceOver | Commander VO (SAL, serialized) | salMIA, salQuit | announcement, commander-vo, sal |
| `vo_sal_minercritical.ogg` | minerCriticalSound | VoiceOver | Commander VO (SAL, serialized) | minerCritical | announcement, commander-vo, sal |
| `vo_sal_minerpartial.ogg` | droneComingHomeEmptySound | VoiceOver | Commander VO (SAL, serialized) | droneComingHomeEmpty | announcement, commander-vo, sal |
| `vo_sal_missilesdepleted.ogg` | salMissilesDepletedSound | VoiceOver | Commander VO (SAL, serialized) | salMissilesDepleted | announcement, commander-vo, sal |
| `vo_sal_missiletech.ogg` | newMissilesCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | announcement, commander-vo, sal |
| `vo_sal_mustbeatstation.ogg` | salMustBeAtStationSound | VoiceOver | Commander VO (SAL, serialized) | salMustBeAtStation | announcement, commander-vo, sal |
| `vo_sal_newequip.ogg` | newEquipmentCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | announcement, commander-vo, sal |
| `vo_sal_noammo.ogg` | salNoAmmoSound | VoiceOver | Commander VO (SAL, serialized) | salNoAmmo | announcement, commander-vo, sal |
| `vo_sal_nofuel.ogg` | salNoFuelSound | VoiceOver | Commander VO (SAL, serialized) | salNoFuel | announcement, commander-vo, sal |
| `vo_sal_noripcord.ogg` | salNoRipcordSound, salRipcordDestroyedSound | VoiceOver | Commander VO (SAL, serialized) | salNoRipcord, salRipcordDestroyed | announcement, commander-vo, sal |
| `vo_sal_noturrets.ogg` | noTurretsSound | VoiceOver | Commander VO (SAL, serialized) | noTurrets | announcement, commander-vo, sal |
| `vo_sal_playerboarded.ogg` | boardSound | VoiceOver | Commander VO (SAL, serialized) | board | announcement, commander-vo, sal |
| `vo_sal_playerwaiting.ogg` | salPlayerWaitingSound | VoiceOver | Commander VO (SAL, serialized) | salPlayerWaiting | announcement, commander-vo, sal |
| `vo_sal_recruitsarrived.ogg` | salRecruitsArrivedSound | VoiceOver | Commander VO (SAL, serialized) | salRecruitsArrived | announcement, commander-vo, sal |
| `vo_sal_redalert.ogg` | salRedAlertSound | VoiceOver | Commander VO (SAL, serialized) | salRedAlert | announcement, commander-vo, sal |
| `vo_sal_reloading.ogg` | salReloadingSound | VoiceOver | Commander VO (SAL, serialized) | salReloading | announcement, commander-vo, sal |
| `vo_sal_reloadingammo.ogg` | salReloadingAmmoSound | VoiceOver | Commander VO (SAL, serialized) | salReloadingAmmo | announcement, commander-vo, sal |
| `vo_sal_reloadingchaff.ogg` | salReloadingChaffSound | VoiceOver | Commander VO (SAL, serialized) | salReloadingChaff | announcement, commander-vo, sal |
| `vo_sal_reloadingdispenser.ogg` | salReloadingDispenserSound | VoiceOver | Commander VO (SAL, serialized) | salReloadingDispenser | announcement, commander-vo, sal |
| `vo_sal_reloadingfuel.ogg` | salReloadingFuelSound | VoiceOver | Commander VO (SAL, serialized) | salReloadingFuel | announcement, commander-vo, sal |
| `vo_sal_reloadingmissiles.ogg` | salReloadingMissilesSound | VoiceOver | Commander VO (SAL, serialized) | salReloadingMissiles | announcement, commander-vo, sal |
| `vo_sal_researchcomplete.ogg` | newResearchCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | announcement, commander-vo, sal |
| `vo_sal_ripcordaborted.ogg` | salRipcordAbortedSound | VoiceOver | Commander VO (SAL, serialized) | salRipcordAborted | announcement, commander-vo, sal |
| `vo_sal_rixsighted.ogg` | salRixSightedSound | VoiceOver | Commander VO (SAL, serialized) | salRixSighted | announcement, commander-vo, sal |
| `vo_sal_sectorcrowded.ogg` | salSectorCrowdedSound | VoiceOver | Commander VO (SAL, serialized) | salSectorCrowded | announcement, commander-vo, sal |
| `vo_sal_sectorlost.ogg` | sectorLostSound | VoiceOver | Commander VO (SAL, serialized) | sectorLost | announcement, commander-vo, sal |
| `vo_sal_sectorsecured.ogg` | sectorSecuredSound | VoiceOver | Commander VO (SAL, serialized) | sectorSecured | announcement, commander-vo, sal |
| `vo_sal_shieldsdown.ogg` | salShieldsDownSound | VoiceOver | Commander VO (SAL, serialized) | salShieldsDown | announcement, commander-vo, sal |
| `vo_sal_shieldsoffline.ogg` | salShieldsOfflineSound | VoiceOver | Commander VO (SAL, serialized) | salShieldsOffline | announcement, commander-vo, sal |
| `vo_sal_shipcantripcord.ogg` | salShipCantRipcordSound | VoiceOver | Commander VO (SAL, serialized) | salShipCantRipcord | announcement, commander-vo, sal |
| `vo_sal_shipisrepaired.ogg` | salRepairedAtCarrierSound | VoiceOver | Commander VO (SAL, serialized) | salRepairedAtCarrier | announcement, commander-vo, sal |
| `vo_sal_shiptech.ogg` | newShipCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | announcement, commander-vo, sal |
| `vo_sal_stationrisk.ogg` | stationThreatenedSound | VoiceOver | Commander VO (SAL, serialized) | stationThreatened | announcement, commander-vo, sal |
| `vo_sal_stationupgrade.ogg` | newStationUpgradeSound | VoiceOver | Commander VO (SAL, serialized) | — | announcement, commander-vo, sal |
| `vo_sal_stationwarning.ogg` | ILStarbaseAlertSound | SFX | Ambient / looping bed | — | announcement, commander-vo, sal |
| `vo_sal_teleportsighted.ogg` | teleportDetectedSound | VoiceOver | Commander VO (SAL, serialized) | teleportDetected | announcement, commander-vo, sal |
| `vo_sal_traitor.ogg` | salChangeSidesSound | VoiceOver | Commander VO (SAL, serialized) | salChangeSides | announcement, commander-vo, sal |
| `vo_sal_transportsighted.ogg` | transportDetectedSound | VoiceOver | Commander VO (SAL, serialized) | transportDetected | announcement, commander-vo, sal |
| `vo_sal_watchfire.ogg` | droneWatchFireSound | VoiceOver | Commander VO (SAL, serialized) | droneWatchFire | announcement, commander-vo, sal |
| `vo_sal_welcomehome.ogg` | salWelcomeHomeSound | VoiceOver | Commander VO (SAL, serialized) | salWelcomeHome | announcement, commander-vo, sal |
| `vo_sal_weptech.ogg` | newWeaponCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | announcement, commander-vo, sal |
| `vo_secured_artifact.ogg` | artifactSecuredSound | VoiceOver | Commander VO (SAL, serialized) | artifactSecured | ctf, objective |
| `vo_secured_enemyflag.ogg` | enemyFlagSecuredSound | VoiceOver | Commander VO (SAL, serialized) | enemyFlagSecured | ctf, objective |
| `vo_secured_flag.ogg` | flagSecuredSound | VoiceOver | Commander VO (SAL, serialized) | flagSecured | ctf, objective |
| `vo_stolen_enemyflag.ogg` | enemyFlagLostSound | VoiceOver | Commander VO (SAL, serialized) | enemyFlagLost | ctf, objective |
| `vo_stolen_flag.ogg` | flagLostSound | VoiceOver | Commander VO (SAL, serialized) | flagLost | ctf, objective |
| `vo_time_10min.ogg` | countdown10minSound | VoiceOver | Commander VO (SAL, serialized) | countdown10min | countdown, timer, vo |
| `vo_time_15sec.ogg` | countdown15Sound | VoiceOver | Commander VO (SAL, serialized) | countdown15 | countdown, timer, vo |
| `vo_time_1min.ogg` | countdown1minSound | VoiceOver | Commander VO (SAL, serialized) | countdown1min | countdown, timer, vo |
| `vo_time_30sec.ogg` | countdown30Sound | VoiceOver | Commander VO (SAL, serialized) | countdown30 | countdown, timer, vo |
| `vo_time_5min.ogg` | countdown5minSound | VoiceOver | Commander VO (SAL, serialized) | countdown5min | countdown, timer, vo |

## Player & comms voice-over (radio callouts)

| File | Logical sound(s) | Bus | Playback role | IGC event | Tags |
|---|---|---|---|---|---|
| `oink1.ogg` | oink1Sound | VoiceOver | Player/Comms VO (serialized) | oink1 | creature, oink |
| `oink2.ogg` | oink2Sound | VoiceOver | Player/Comms VO (serialized) | oink2 | creature, oink |
| `oink3.ogg` | oink3Sound | VoiceOver | Player/Comms VO (serialized) | oink3 | creature, oink |
| `oink4.ogg` | oink4Sound | VoiceOver | Player/Comms VO (serialized) | oink4 | creature, oink |
| `oink5.ogg` | oink5Sound | VoiceOver | Player/Comms VO (serialized) | oink5 | creature, oink |
| `vo_player_aaaaoooo.ogg` | voAaaaOooo | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_acknowledged.ogg` | voAcknowledgedSound | VoiceOver | Player/Comms VO (serialized) | voAcknowledged | callout, comms, player-vo |
| `vo_player_affirmative.ogg` | voAffirmativeSound | VoiceOver | Player/Comms VO (serialized) | voAffirmative | callout, comms, player-vo |
| `vo_player_aintnothin.ogg` | voAintNoThinSound | VoiceOver | Player/Comms VO (serialized) | voAintNoThin | callout, comms, player-vo |
| `vo_player_alephmined.ogg` | voAlephMinedSound | VoiceOver | Player/Comms VO (serialized) | voAlephMined | callout, comms, player-vo |
| `vo_player_almosthadyou.ogg` | voAlmostHadYouSound | VoiceOver | Player/Comms VO (serialized) | voAlmostHadYou | callout, comms, player-vo |
| `vo_player_argh.ogg` | voArghSound | VoiceOver | Player/Comms VO (serialized) | voArgh | callout, comms, player-vo |
| `vo_player_attack.ogg` | voAttackSound | VoiceOver | Player/Comms VO (serialized) | voAttack | callout, comms, player-vo |
| `vo_player_attackbase.ogg` | voAttackBaseSound | VoiceOver | Player/Comms VO (serialized) | voAttackBase | callout, comms, player-vo |
| `vo_player_attackbomber.ogg` | voAttackBomberSound | VoiceOver | Player/Comms VO (serialized) | voAttackBomber | callout, comms, player-vo |
| `vo_player_attackcapital.ogg` | voAttackCapitalSound | VoiceOver | Player/Comms VO (serialized) | voAttackCapital | callout, comms, player-vo |
| `vo_player_attackconstuctor.ogg` | voAttackConstructorSound | VoiceOver | Player/Comms VO (serialized) | voAttackConstructor | callout, comms, player-vo |
| `vo_player_attackdefender.ogg` | voAttackDefenderSound | VoiceOver | Player/Comms VO (serialized) | voAttackDefender | callout, comms, player-vo |
| `vo_player_attackfighter.ogg` | voAttackFighterSound | VoiceOver | Player/Comms VO (serialized) | voAttackFighter | callout, comms, player-vo |
| `vo_player_attackinterceptor.ogg` | voAttackInterceptorSound | VoiceOver | Player/Comms VO (serialized) | voAttackInterceptor | callout, comms, player-vo |
| `vo_player_attackminer.ogg` | voAttackMinerSound | VoiceOver | Player/Comms VO (serialized) | voAttackMiner | callout, comms, player-vo |
| `vo_player_attackripcord.ogg` | voAttackRipcordSound | VoiceOver | Player/Comms VO (serialized) | voAttackRipcord | callout, comms, player-vo |
| `vo_player_attackscout.ogg` | voAttackScoutSound | VoiceOver | Player/Comms VO (serialized) | voAttackScout | callout, comms, player-vo |
| `vo_player_attackstealth.ogg` | voAttackStealthSound | VoiceOver | Player/Comms VO (serialized) | voAttackStealth | callout, comms, player-vo |
| `vo_player_attacktarget.ogg` | voAttackTargetSound | VoiceOver | Player/Comms VO (serialized) | voAttackTarget | callout, comms, player-vo |
| `vo_player_attacktower.ogg` | voAttackTowerSound | VoiceOver | Player/Comms VO (serialized) | voAttackTower | callout, comms, player-vo |
| `vo_player_attacktransport.ogg` | voAttackTransportSound | VoiceOver | Player/Comms VO (serialized) | voAttackTransport | callout, comms, player-vo |
| `vo_player_awesome.ogg` | voAwsomeSound | VoiceOver | Player/Comms VO (serialized) | voAwsome | callout, comms, player-vo |
| `vo_player_basecaptured.ogg` | voBaseCapturedSound | VoiceOver | Player/Comms VO (serialized) | voBaseCaptured | callout, comms, player-vo |
| `vo_player_baseunderattack.ogg` | voBaseUnderAttackSound | VoiceOver | Player/Comms VO (serialized) | voBaseUnderAttack | callout, comms, player-vo |
| `vo_player_bomberwaiting.ogg` | voBomberWaitingSound | VoiceOver | Player/Comms VO (serialized) | voBomberWaiting | callout, comms, player-vo |
| `vo_player_bye.ogg` | voByeSound | VoiceOver | Player/Comms VO (serialized) | voBye | callout, comms, player-vo |
| `vo_player_cantholdem.ogg` | voCantHoldEmSound | VoiceOver | Player/Comms VO (serialized) | voCantHoldEm | callout, comms, player-vo |
| `vo_player_capshipwaiting.ogg` | voCapShipWaitingSound | VoiceOver | Player/Comms VO (serialized) | voCapShipWaiting | callout, comms, player-vo |
| `vo_player_changedsettings.ogg` | voChangedSettingsSound | VoiceOver | Player/Comms VO (serialized) | voChangedSettings | callout, comms, player-vo |
| `vo_player_changethemap.ogg` | voChangeTheMapSound | VoiceOver | Player/Comms VO (serialized) | voChangeTheMap | callout, comms, player-vo |
| `vo_player_checkwing.ogg` | voCheckWingSound | VoiceOver | Player/Comms VO (serialized) | voCheckWing | callout, comms, player-vo |
| `vo_player_comebackandfight.ogg` | voComeBackAndFightSound | VoiceOver | Player/Comms VO (serialized) | voComeBackAndFight | callout, comms, player-vo |
| `vo_player_comeondown.ogg` | voComeOnDown | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_complete.ogg` | voCompleteSound | VoiceOver | Player/Comms VO (serialized) | voComplete | callout, comms, player-vo |
| `vo_player_constructorlaunching.ogg` | voConstructorLaunchingSound | VoiceOver | Player/Comms VO (serialized) | voConstructorLaunching | callout, comms, player-vo |
| `vo_player_cool.ogg` | voCoolSound | VoiceOver | Player/Comms VO (serialized) | voCool | callout, comms, player-vo |
| `vo_player_coupleminutes.ogg` | voCoupleMinutesSound | VoiceOver | Player/Comms VO (serialized) | voCoupleMinutes | callout, comms, player-vo |
| `vo_player_coverme.ogg` | voCoverMeSound | VoiceOver | Player/Comms VO (serialized) | voCoverMe | callout, comms, player-vo |
| `vo_player_dang.ogg` | voDangSound | VoiceOver | Player/Comms VO (serialized) | voDang | callout, comms, player-vo |
| `vo_player_deathbecomesyou.ogg` | voDeathBecomesYouSound | VoiceOver | Player/Comms VO (serialized) | voDeathBecomesYou | callout, comms, player-vo |
| `vo_player_defend.ogg` | voDefendSound | VoiceOver | Player/Comms VO (serialized) | voDefend | callout, comms, player-vo |
| `vo_player_defendbase.ogg` | voDefendBaseSound | VoiceOver | Player/Comms VO (serialized) | voDefendBase | callout, comms, player-vo |
| `vo_player_defendbomber.ogg` | voDefendBomberSound | VoiceOver | Player/Comms VO (serialized) | voDefendBomber | callout, comms, player-vo |
| `vo_player_defendcapital.ogg` | voDefendCapitalSound | VoiceOver | Player/Comms VO (serialized) | voDefendCapital | callout, comms, player-vo |
| `vo_player_defendconstuctor.ogg` | voDefendConstructorSound | VoiceOver | Player/Comms VO (serialized) | voDefendConstructor | callout, comms, player-vo |
| `vo_player_defenddefender.ogg` | voDefendDefenderSound | VoiceOver | Player/Comms VO (serialized) | voDefendDefender | callout, comms, player-vo |
| `vo_player_defendfighter.ogg` | voDefendFighterSound | VoiceOver | Player/Comms VO (serialized) | voDefendFighter | callout, comms, player-vo |
| `vo_player_defendinterceptor.ogg` | voDefendInterceptorSound | VoiceOver | Player/Comms VO (serialized) | voDefendInterceptor | callout, comms, player-vo |
| `vo_player_defendminer.ogg` | voDefendMinerSound | VoiceOver | Player/Comms VO (serialized) | voDefendMiner | callout, comms, player-vo |
| `vo_player_defendripcord.ogg` | voDefendRipcordSound | VoiceOver | Player/Comms VO (serialized) | voDefendRipcord | callout, comms, player-vo |
| `vo_player_defendscout.ogg` | voDefendScoutSound | VoiceOver | Player/Comms VO (serialized) | voDefendScout | callout, comms, player-vo |
| `vo_player_defendstealth.ogg` | voDefendStealthSound | VoiceOver | Player/Comms VO (serialized) | voDefendStealth | callout, comms, player-vo |
| `vo_player_defendtower.ogg` | voDefendTowerSound | VoiceOver | Player/Comms VO (serialized) | voDefendTower | callout, comms, player-vo |
| `vo_player_defendtransport.ogg` | voDefendTransportSound | VoiceOver | Player/Comms VO (serialized) | voDefendTransport | callout, comms, player-vo |
| `vo_player_deploymines.ogg` | voDeployMinesSound | VoiceOver | Player/Comms VO (serialized) | voDeployMines | callout, comms, player-vo |
| `vo_player_deployprobes.ogg` | voDeployProbesSound | VoiceOver | Player/Comms VO (serialized) | voDeployProbes | callout, comms, player-vo |
| `vo_player_didyoucopy.ogg` | voDidYouCopy | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_doah.ogg` | voDoahSound | VoiceOver | Player/Comms VO (serialized) | voDoah | callout, comms, player-vo |
| `vo_player_donatecredits.ogg` | voDonateCreditsSound | VoiceOver | Player/Comms VO (serialized) | voDonateCredits | callout, comms, player-vo |
| `vo_player_donateinvestor.ogg` | voDonateInvestorSound | VoiceOver | Player/Comms VO (serialized) | voDonateInvestor | callout, comms, player-vo |
| `vo_player_enemyhasflag.ogg` | voEnemyHasFlagSound | VoiceOver | Player/Comms VO (serialized) | voEnemyHasFlag | callout, comms, player-vo |
| `vo_player_escortbuilder.ogg` | voEscortBuilderSound | VoiceOver | Player/Comms VO (serialized) | voEscortBuilder | callout, comms, player-vo |
| `vo_player_escortminer.ogg` | voEscortMinerSound | VoiceOver | Player/Comms VO (serialized) | voEscortMiner | callout, comms, player-vo |
| `vo_player_everyoneready.ogg` | voEveryoneReadySound | VoiceOver | Player/Comms VO (serialized) | voEveryoneReady | callout, comms, player-vo |
| `vo_player_fiesty.ogg` | voFiestySound | VoiceOver | Player/Comms VO (serialized) | voFiesty | callout, comms, player-vo |
| `vo_player_findalephs.ogg` | voFindAlephsSound | VoiceOver | Player/Comms VO (serialized) | voFindAlephs | callout, comms, player-vo |
| `vo_player_findcarbonacous.ogg` | voFindCarbonacousSound | VoiceOver | Player/Comms VO (serialized) | voFindCarbonacous | callout, comms, player-vo |
| `vo_player_findenemy.ogg` | voFindEnemySound | VoiceOver | Player/Comms VO (serialized) | voFindEnemy | callout, comms, player-vo |
| `vo_player_findfreakinbase.ogg` | voFindFreakinBaseSound | VoiceOver | Player/Comms VO (serialized) | voFindFreakinBase | callout, comms, player-vo |
| `vo_player_findhelium.ogg` | voFindHeliumSound | VoiceOver | Player/Comms VO (serialized) | voFindHelium | callout, comms, player-vo |
| `vo_player_findiron.ogg` | voFindIronSound | VoiceOver | Player/Comms VO (serialized) | voFindIron | callout, comms, player-vo |
| `vo_player_findlava.ogg` | voFindLavaSound | VoiceOver | Player/Comms VO (serialized) | voFindLava | callout, comms, player-vo |
| `vo_player_findminer.ogg` | voFindMinerSound | VoiceOver | Player/Comms VO (serialized) | voFindMiner | callout, comms, player-vo |
| `vo_player_findprobes.ogg` | voFindProbesSound | VoiceOver | Player/Comms VO (serialized) | voFindProbes | callout, comms, player-vo |
| `vo_player_findsilicon.ogg` | voFindSiliconSound | VoiceOver | Player/Comms VO (serialized) | voFindSilicon | callout, comms, player-vo |
| `vo_player_finduranium.ogg` | voFindUraniumSound | VoiceOver | Player/Comms VO (serialized) | voFindUranium | callout, comms, player-vo |
| `vo_player_follow.ogg` | voFollowSound | VoiceOver | Player/Comms VO (serialized) | voFollow | callout, comms, player-vo |
| `vo_player_formate.ogg` | voFormateSound | VoiceOver | Player/Comms VO (serialized) | voFormate | callout, comms, player-vo |
| `vo_player_formonmywing.ogg` | voFormOnMyWingSound | VoiceOver | Player/Comms VO (serialized) | voFormOnMyWing | callout, comms, player-vo |
| `vo_player_foundaleph.ogg` | voFoundAlephSound | VoiceOver | Player/Comms VO (serialized) | voFoundAleph | callout, comms, player-vo |
| `vo_player_foundcarbonacous.ogg` | voFoundCarbonacousSound | VoiceOver | Player/Comms VO (serialized) | voFoundCarbonacous | callout, comms, player-vo |
| `vo_player_foundenemybase.ogg` | voFoundEnemyBaseSound | VoiceOver | Player/Comms VO (serialized) | voFoundEnemyBase | callout, comms, player-vo |
| `vo_player_foundenemyconstruct.ogg` | voFoundEnemyConstructSound | VoiceOver | Player/Comms VO (serialized) | voFoundEnemyConstruct | callout, comms, player-vo |
| `vo_player_foundenemyminer.ogg` | voFoundEnemyMinerSound | VoiceOver | Player/Comms VO (serialized) | voFoundEnemyMiner | callout, comms, player-vo |
| `vo_player_foundenemyships.ogg` | voFoundEnemyShipsSound | VoiceOver | Player/Comms VO (serialized) | voFoundEnemyShips | callout, comms, player-vo |
| `vo_player_foundhelium.ogg` | voFoundHeliumSound | VoiceOver | Player/Comms VO (serialized) | voFoundHelium | callout, comms, player-vo |
| `vo_player_foundiron.ogg` | voFoundIronSound | VoiceOver | Player/Comms VO (serialized) | voFoundIron | callout, comms, player-vo |
| `vo_player_foundlava.ogg` | voFoundLavaSound | VoiceOver | Player/Comms VO (serialized) | voFoundLava | callout, comms, player-vo |
| `vo_player_getoffme.ogg` | voGetOffMeSound | VoiceOver | Player/Comms VO (serialized) | voGetOffMe | callout, comms, player-vo |
| `vo_player_gimmesomething.ogg` | voGimmeSomethingSound | VoiceOver | Player/Comms VO (serialized) | voGimmeSomething | callout, comms, player-vo |
| `vo_player_gogogo.ogg` | voGoGoGoSound | VoiceOver | Player/Comms VO (serialized) | voGoGoGo | callout, comms, player-vo |
| `vo_player_gotcha.ogg` | voGotchaSound | VoiceOver | Player/Comms VO (serialized) | voGotcha | callout, comms, player-vo |
| `vo_player_gototeamonly.ogg` | voGoToTeamOnlySound | VoiceOver | Player/Comms VO (serialized) | voGoToTeamOnly | callout, comms, player-vo |
| `vo_player_gunnerready.ogg` | voGunnerReadySound | VoiceOver | Player/Comms VO (serialized) | voGunnerReady | callout, comms, player-vo |
| `vo_player_haveanartifact.ogg` | voHaveAnArtifactSound | VoiceOver | Player/Comms VO (serialized) | voHaveAnArtifact | callout, comms, player-vo |
| `vo_player_havecapitols.ogg` | voHaveCapsSound | VoiceOver | Player/Comms VO (serialized) | voHaveCaps | callout, comms, player-vo |
| `vo_player_haveshipyard.ogg` | voHaveShipyardSound | VoiceOver | Player/Comms VO (serialized) | voHaveShipyard | callout, comms, player-vo |
| `vo_player_headback.ogg` | voHeadBackSound | VoiceOver | Player/Comms VO (serialized) | voHeadBack | callout, comms, player-vo |
| `vo_player_heeheehee.ogg` | voHeeHeeHee | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_hellyeah.ogg` | voHellYeahSound | VoiceOver | Player/Comms VO (serialized) | voHellYeah | callout, comms, player-vo |
| `vo_player_hi.ogg` | dummySound, voHiSound | VoiceOver | Player/Comms VO (serialized) | voHi | callout, comms, player-vo |
| `vo_player_holdup.ogg` | voHoldUpSound | VoiceOver | Player/Comms VO (serialized) | voHoldUp | callout, comms, player-vo |
| `vo_player_holdurehorses.ogg` | voHoldYourHorsesSound | VoiceOver | Player/Comms VO (serialized) | voHoldYourHorses | callout, comms, player-vo |
| `vo_player_howdthatfeel.ogg` | voHowdThatFeelSound | VoiceOver | Player/Comms VO (serialized) | voHowdThatFeel | callout, comms, player-vo |
| `vo_player_howwasthat.ogg` | voHowWasThat | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_imbusy.ogg` | voImBusySound | VoiceOver | Player/Comms VO (serialized) | voImBusy | callout, comms, player-vo |
| `vo_player_imclueless.ogg` | voImCluelessSound | VoiceOver | Player/Comms VO (serialized) | voImClueless | callout, comms, player-vo |
| `vo_player_imissed.ogg` | voIMissed | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_imonit.ogg` | voImOnItSound | VoiceOver | Player/Comms VO (serialized) | voImOnIt | callout, comms, player-vo |
| `vo_player_inbound.ogg` | voInboundSound | VoiceOver | Player/Comms VO (serialized) | voInbound | callout, comms, player-vo |
| `vo_player_inboundenemybomb.ogg` | voInboundBombSound | VoiceOver | Player/Comms VO (serialized) | voInboundBomb | callout, comms, player-vo |
| `vo_player_inboundenemycap.ogg` | voEnemyCapSound | VoiceOver | Player/Comms VO (serialized) | voEnemyCap | callout, comms, player-vo |
| `vo_player_inboundenemyfight.ogg` | voEnemyFightersSound | VoiceOver | Player/Comms VO (serialized) | voEnemyFighters | callout, comms, player-vo |
| `vo_player_inboundenemytrans.ogg` | voEnemyTransSound | VoiceOver | Player/Comms VO (serialized) | voEnemyTrans | callout, comms, player-vo |
| `vo_player_iquit.ogg` | voIQuit | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_isourbaseclear.ogg` | voIsOurBaseClearSound | VoiceOver | Player/Comms VO (serialized) | voIsOurBaseClear | callout, comms, player-vo |
| `vo_player_ispdrop.ogg` | voISPDropSound | VoiceOver | Player/Comms VO (serialized) | voISPDrop | callout, comms, player-vo |
| `vo_player_itslate.ogg` | voItsLateSound | VoiceOver | Player/Comms VO (serialized) | voItsLate | callout, comms, player-vo |
| `vo_player_joiningturret.ogg` | voJoiningTurretSound | VoiceOver | Player/Comms VO (serialized) | voJoiningTurret | callout, comms, player-vo |
| `vo_player_justasec.ogg` | voJustASecSound | VoiceOver | Player/Comms VO (serialized) | voJustASec | callout, comms, player-vo |
| `vo_player_kinguniverse.ogg` | voKingUniverseSound | VoiceOver | Player/Comms VO (serialized) | voKingUniverse | callout, comms, player-vo |
| `vo_player_laylow.ogg` | voLayLowSound | VoiceOver | Player/Comms VO (serialized) | voLayLow | callout, comms, player-vo |
| `vo_player_likelambs.ogg` | voLikeLambsSound | VoiceOver | Player/Comms VO (serialized) | voLikeLambs | callout, comms, player-vo |
| `vo_player_makeme.ogg` | voMakeMeSound | VoiceOver | Player/Comms VO (serialized) | voMakeMe | callout, comms, player-vo |
| `vo_player_meetatjump.ogg` | voMeetAtJumpSound | VoiceOver | Player/Comms VO (serialized) | voMeetAtJump | callout, comms, player-vo |
| `vo_player_minerhunt.ogg` | voMinerHuntSound | VoiceOver | Player/Comms VO (serialized) | voMinerHunt | callout, comms, player-vo |
| `vo_player_minersaredead.ogg` | voMinersAreDeadSound | VoiceOver | Player/Comms VO (serialized) | voMinersAreDead | callout, comms, player-vo |
| `vo_player_minershammered.ogg` | voMinersHammeredSound | VoiceOver | Player/Comms VO (serialized) | voMinersHammered | callout, comms, player-vo |
| `vo_player_misc17.ogg` | voMisc17 | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_misc2.ogg` | voMisc2 | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_misc20.ogg` | voMisc20 | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_misc21.ogg` | voMisc21 | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_misc25.ogg` | voMisc25 | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_misc27.ogg` | voMisc27 | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_misc3.ogg` | voMisc3 | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_misc9.ogg` | voMisc9 | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_needammo.ogg` | voNeedAmmoSound | VoiceOver | Player/Comms VO (serialized) | voNeedAmmo | callout, comms, player-vo |
| `vo_player_needbase.ogg` | voNeedBaseSound | VoiceOver | Player/Comms VO (serialized) | voNeedBase | callout, comms, player-vo |
| `vo_player_needbetterfighters.ogg` | voNeedBetterFightersSound | VoiceOver | Player/Comms VO (serialized) | voNeedBetterFighters | callout, comms, player-vo |
| `vo_player_needbetterwep.ogg` | voNeedBetterWepSound | VoiceOver | Player/Comms VO (serialized) | voNeedBetterWep | callout, comms, player-vo |
| `vo_player_needbomber.ogg` | voNeedBomberSound | VoiceOver | Player/Comms VO (serialized) | voNeedBomber | callout, comms, player-vo |
| `vo_player_needcapital.ogg` | voNeedCapitalSound | VoiceOver | Player/Comms VO (serialized) | voNeedCapital | callout, comms, player-vo |
| `vo_player_needconstructor.ogg` | voNeedConstructorSound | VoiceOver | Player/Comms VO (serialized) | voNeedConstructor | callout, comms, player-vo |
| `vo_player_needcruiser.ogg` | voNeedCruiserSound | VoiceOver | Player/Comms VO (serialized) | voNeedCruiser | callout, comms, player-vo |
| `vo_player_needdefender.ogg` | voNeedDefenderSound | VoiceOver | Player/Comms VO (serialized) | voNeedDefender | callout, comms, player-vo |
| `vo_player_needfighter.ogg` | voNeedFighterSound | VoiceOver | Player/Comms VO (serialized) | voNeedFighter | callout, comms, player-vo |
| `vo_player_needfightersupport.ogg` | voNeedFighterSupportSound | VoiceOver | Player/Comms VO (serialized) | voNeedFighterSupport | callout, comms, player-vo |
| `vo_player_needfuel.ogg` | voNeedFuelSound | VoiceOver | Player/Comms VO (serialized) | voNeedFuel | callout, comms, player-vo |
| `vo_player_needhelp.ogg` | voNeedHelpSound | VoiceOver | Player/Comms VO (serialized) | voNeedHelp | callout, comms, player-vo |
| `vo_player_needinterceptor.ogg` | voNeedInterceptorSound | VoiceOver | Player/Comms VO (serialized) | voNeedInterceptor | callout, comms, player-vo |
| `vo_player_needminer.ogg` | voNeedMinerSound | VoiceOver | Player/Comms VO (serialized) | voNeedMiner | callout, comms, player-vo |
| `vo_player_needmoney.ogg` | voNeedMoneySound | VoiceOver | Player/Comms VO (serialized) | voNeedMoney | callout, comms, player-vo |
| `vo_player_needobjective.ogg` | voNeedObjectiveSound | VoiceOver | Player/Comms VO (serialized) | voNeedObjective | callout, comms, player-vo |
| `vo_player_needpickup.ogg` | voNeedPickupSound | VoiceOver | Player/Comms VO (serialized) | voNeedPickup | callout, comms, player-vo |
| `vo_player_needrepairs.ogg` | voNeedRepairsSound | VoiceOver | Player/Comms VO (serialized) | voNeedRepairs | callout, comms, player-vo |
| `vo_player_needrescue.ogg` | voNeedRescueSound | VoiceOver | Player/Comms VO (serialized) | voNeedRescue | callout, comms, player-vo |
| `vo_player_needripcord.ogg` | voNeedRipcordSound | VoiceOver | Player/Comms VO (serialized) | voNeedRipcord | callout, comms, player-vo |
| `vo_player_needscout.ogg` | voNeedScoutSound | VoiceOver | Player/Comms VO (serialized) | voNeedScout | callout, comms, player-vo |
| `vo_player_needstealth.ogg` | voNeedStealthSound | VoiceOver | Player/Comms VO (serialized) | voNeedStealth | callout, comms, player-vo |
| `vo_player_needtower.ogg` | voNeedTowerSound | VoiceOver | Player/Comms VO (serialized) | voNeedTower | callout, comms, player-vo |
| `vo_player_needtransport.ogg` | voNeedTransportSound | VoiceOver | Player/Comms VO (serialized) | voNeedTransport | callout, comms, player-vo |
| `vo_player_needturret.ogg` | voNeedTurretSound | VoiceOver | Player/Comms VO (serialized) | voNeedTurret | callout, comms, player-vo |
| `vo_player_negative.ogg` | voNegativeSound | VoiceOver | Player/Comms VO (serialized) | voNegative | callout, comms, player-vo |
| `vo_player_niceweather.ogg` | voNiceWeatherSound | VoiceOver | Player/Comms VO (serialized) | voNiceWeather | callout, comms, player-vo |
| `vo_player_nicework.ogg` | voNiceWorkSound | VoiceOver | Player/Comms VO (serialized) | voNiceWork | callout, comms, player-vo |
| `vo_player_nomoney.ogg` | voNoMoneySound | VoiceOver | Player/Comms VO (serialized) | voNoMoney | callout, comms, player-vo |
| `vo_player_nooo.ogg` | voNoooSound | VoiceOver | Player/Comms VO (serialized) | voNooo | callout, comms, player-vo |
| `vo_player_objectivecomplete.ogg` | voObjectiveCompleteSound | VoiceOver | Player/Comms VO (serialized) | voObjectiveComplete | callout, comms, player-vo |
| `vo_player_ohyeah.ogg` | voOhYeahSound | VoiceOver | Player/Comms VO (serialized) | voOhYeah | callout, comms, player-vo |
| `vo_player_onmyway.ogg` | voOnMyWaySound | VoiceOver | Player/Comms VO (serialized) | voOnMyWay | callout, comms, player-vo |
| `vo_player_ooohsorry.ogg` | voOoohSorrySound | VoiceOver | Player/Comms VO (serialized) | voOoohSorry | callout, comms, player-vo |
| `vo_player_ooooaaaa.ogg` | voOoooAaaa | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_oops.ogg` | voOopsSound | VoiceOver | Player/Comms VO (serialized) | voOops | callout, comms, player-vo |
| `vo_player_outofammohmmm.ogg` | voOutOfAmmoSound | VoiceOver | Player/Comms VO (serialized) | voOutOfAmmo | callout, comms, player-vo |
| `vo_player_payloaddelivered.ogg` | voPayloadDeliveredSound | VoiceOver | Player/Comms VO (serialized) | voPayloadDelivered | callout, comms, player-vo |
| `vo_player_pheww.ogg` | voPhewSound | VoiceOver | Player/Comms VO (serialized) | voPhew | callout, comms, player-vo |
| `vo_player_pitstop.ogg` | voPitstopSound | VoiceOver | Player/Comms VO (serialized) | voPitstop | callout, comms, player-vo |
| `vo_player_pursuingelectron.ogg` | voPursuingElectronicsSound | VoiceOver | Player/Comms VO (serialized) | voPursuingElectronics | callout, comms, player-vo |
| `vo_player_pursuingenergy.ogg` | voPursuingEnergySound | VoiceOver | Player/Comms VO (serialized) | voPursuingEnergy | callout, comms, player-vo |
| `vo_player_pursuingordinance.ogg` | voPursuingOrdinanceSound | VoiceOver | Player/Comms VO (serialized) | voPursuingOrdinance | callout, comms, player-vo |
| `vo_player_readchat.ogg` | voReadTheChat | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_reallife.ogg` | voRealLifeSound | VoiceOver | Player/Comms VO (serialized) | voRealLife | callout, comms, player-vo |
| `vo_player_regroup.ogg` | voRegroupSound | VoiceOver | Player/Comms VO (serialized) | voRegroup | callout, comms, player-vo |
| `vo_player_rematch.ogg` | voRematchSound | VoiceOver | Player/Comms VO (serialized) | voRematch | callout, comms, player-vo |
| `vo_player_repairstation.ogg` | voRepairStationSound | VoiceOver | Player/Comms VO (serialized) | voRepairStation | callout, comms, player-vo |
| `vo_player_retreat.ogg` | voRetreatSound | VoiceOver | Player/Comms VO (serialized) | voRetreat | callout, comms, player-vo |
| `vo_player_ripcordhome.ogg` | voRipcordHomeSound | VoiceOver | Player/Comms VO (serialized) | voRipcordHome | callout, comms, player-vo |
| `vo_player_ripcording.ogg` | voRipcordingSound | VoiceOver | Player/Comms VO (serialized) | voRipcording | callout, comms, player-vo |
| `vo_player_ripcordlastresort.ogg` | voRipcordLastResortSound | VoiceOver | Player/Comms VO (serialized) | voRipcordLastResort | callout, comms, player-vo |
| `vo_player_roger.ogg` | voRoger | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_savingup.ogg` | voSavingUpSound | VoiceOver | Player/Comms VO (serialized) | voSavingUp | callout, comms, player-vo |
| `vo_player_scoutaleph.ogg` | voScoutAlephSound | VoiceOver | Player/Comms VO (serialized) | voScoutAleph | callout, comms, player-vo |
| `vo_player_scuseme.ogg` | voScuseMe | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_shallwebegin.ogg` | voShallWeBeginSound | VoiceOver | Player/Comms VO (serialized) | voShallWeBegin | callout, comms, player-vo |
| `vo_player_shieldsaredown.ogg` | voShieldsAreDownSound | VoiceOver | Player/Comms VO (serialized) | voShieldsAreDown | callout, comms, player-vo |
| `vo_player_shoot.ogg` | voShootSound | VoiceOver | Player/Comms VO (serialized) | voShoot | callout, comms, player-vo, weapon-fire |
| `vo_player_shootcommander.ogg` | voShootCommanderSound | VoiceOver | Player/Comms VO (serialized) | voShootCommander | callout, comms, player-vo, weapon-fire |
| `vo_player_shootingfish.ogg` | voShootingFishSound | VoiceOver | Player/Comms VO (serialized) | voShootingFish | callout, comms, player-vo, weapon-fire |
| `vo_player_slapinvestor.ogg` | voSlapInvestorSound | VoiceOver | Player/Comms VO (serialized) | voSlapInvestor | callout, comms, player-vo |
| `vo_player_startthegame.ogg` | voStartTheGameSound | VoiceOver | Player/Comms VO (serialized) | voStartTheGame | callout, comms, player-vo |
| `vo_player_stationrepair.ogg` | voStationNeedsRepairsSound | VoiceOver | Player/Comms VO (serialized) | voStationNeedsRepairs | callout, comms, player-vo |
| `vo_player_stayontarget.ogg` | voStayOnTargetSound | VoiceOver | Player/Comms VO (serialized) | voStayOnTarget | callout, comms, player-vo |
| `vo_player_staytogether.ogg` | voStayTogetherSound | VoiceOver | Player/Comms VO (serialized) | voStayTogether | callout, comms, player-vo |
| `vo_player_steadytiger.ogg` | voSteadyTigerSound | VoiceOver | Player/Comms VO (serialized) | voSteadyTiger | callout, comms, player-vo |
| `vo_player_surelyjoking.ogg` | voSurelyJokingSound | VoiceOver | Player/Comms VO (serialized) | voSurelyJoking | callout, comms, player-vo |
| `vo_player_sweet.ogg` | voSweetSound | VoiceOver | Player/Comms VO (serialized) | voSweet | callout, comms, player-vo |
| `vo_player_takingittothem.ogg` | voTakingItToThemSound | VoiceOver | Player/Comms VO (serialized) | voTakingItToThem | callout, comms, player-vo |
| `vo_player_targetneeded.ogg` | voTargetNeededSound | VoiceOver | Player/Comms VO (serialized) | voTargetNeeded | callout, comms, player-vo |
| `vo_player_thanks.ogg` | voThanksSound | VoiceOver | Player/Comms VO (serialized) | voThanks | callout, comms, player-vo |
| `vo_player_thankslift.ogg` | voThanksLiftSound | VoiceOver | Player/Comms VO (serialized) | voThanksLift | callout, comms, player-vo |
| `vo_player_thathurt.ogg` | voThatHurtSound | VoiceOver | Player/Comms VO (serialized) | voThatHurt | callout, comms, player-vo |
| `vo_player_traitor.ogg` | voTraitorSound | VoiceOver | Player/Comms VO (serialized) | voTraitor | callout, comms, player-vo |
| `vo_player_transportoutbound.ogg` | voTransportOutboundSound | VoiceOver | Player/Comms VO (serialized) | voTransportOutbound | callout, comms, player-vo |
| `vo_player_turretsattack.ogg` | voTurretsAttackSound | VoiceOver | Player/Comms VO (serialized) | voTurretsAttack | callout, comms, player-vo |
| `vo_player_udiedwithdignity.ogg` | voYouDiedWithDignitySound | VoiceOver | Player/Comms VO (serialized) | voYouDiedWithDignity | callout, comms, player-vo |
| `vo_player_uhavenohonor.ogg` | voYouHaveNoHonorSound | VoiceOver | Player/Comms VO (serialized) | voYouHaveNoHonor | callout, comms, player-vo |
| `vo_player_ullbesorry.ogg` | voYoullBeSorrySound | VoiceOver | Player/Comms VO (serialized) | voYoullBeSorry | callout, comms, player-vo |
| `vo_player_urgoodbut.ogg` | voYouAreGoodButSound | VoiceOver | Player/Comms VO (serialized) | voYouAreGoodBut | callout, comms, player-vo |
| `vo_player_wait4signal.ogg` | voWaitForSignalSound | VoiceOver | Player/Comms VO (serialized) | voWaitForSignal | callout, comms, player-vo |
| `vo_player_wantapiece.ogg` | voWantAPieceSound | VoiceOver | Player/Comms VO (serialized) | voWantAPiece | callout, comms, player-vo |
| `vo_player_watchfire.ogg` | voWatchFireSound | VoiceOver | Player/Comms VO (serialized) | voWatchFire | callout, comms, player-vo |
| `vo_player_what.ogg` | voWhat | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_whatnow.ogg` | voWhatNow | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_whocommander.ogg` | voWhoCommanderSound | VoiceOver | Player/Comms VO (serialized) | voWhoCommander | callout, comms, player-vo |
| `vo_player_yeeehaa.ogg` | voYeeHaSound | VoiceOver | Player/Comms VO (serialized) | voYeeHa | callout, comms, player-vo |
| `vo_player_yes.ogg` | voYesSound | VoiceOver | Player/Comms VO (serialized) | voYes | callout, comms, player-vo |
| `vo_player_yourecrazy.ogg` | voYoureCrazySound | VoiceOver | Player/Comms VO (serialized) | voYoureCrazy | callout, comms, player-vo |
| `vo_player_yourmad.ogg` | voYoureMadSound | VoiceOver | Player/Comms VO (serialized) | voYoureMad | callout, comms, player-vo |
| `vo_player_yousuck.ogg` | voYouSuck | VoiceOver | Player/Comms VO (serialized) | — | callout, comms, player-vo |
| `vo_player_yowie.ogg` | voYowieSound | VoiceOver | Player/Comms VO (serialized) | voYowie | callout, comms, player-vo |

## Mission-advisor voice-over

| File | Logical sound(s) | Bus | Playback role | IGC event | Tags |
|---|---|---|---|---|---|
| `vo_ma_alephmineddroned.ogg` | voalephmineddroned | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_asseskicked.ogg` | voasseskicked | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_attackminefield.ogg` | voattackminefield | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_attacknanite.ogg` | voattacknanite | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_attackprobe.ogg` | voattackprobe | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_attackrescue.ogg` | voattackrescue | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_attackspecial.ogg` | voattackspecial | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_bugout.ogg` | vobugout | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_campaleph.ogg` | vocampaleph | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_campbase.ogg` | vocampbase | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_campteleport.ogg` | vocampteleport | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_cantpodded.ogg` | vocantpodded | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_capturingbase.ogg` | vocapturingbase | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_cloaked.ogg` | vocloaked | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_defendersclose.ogg` | vodefendersclose | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_defendspecial.ogg` | vodefendspecial | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_deployrescue.ogg` | vodeployrescue | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_dogmeat.ogg` | vodogmeat | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_dontdefend.ogg` | vodontdefend | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_dontkillpods.ogg` | vodontkillpods | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_dontthinkso.ogg` | vodontthinkso | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_evenup.ogg` | voevenup | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_findass.ogg` | vofindass | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_findcash.ogg` | vofindcash | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_findconst.ogg` | vofindconst | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_findscout.ogg` | vofindscout | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_gameover.ogg` | vogameover | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_gametime.ogg` | vogametime | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_harshlanguage.ogg` | voharshlanguage | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_hello.ogg` | vohello | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_howfar.ogg` | vohowfar | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_iqs.ogg` | voiqs | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_isalephmined.ogg` | voisalephmined | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_leavemark.ogg` | voleavemark | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_letsgo.ogg` | voletsgo | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_mutiny.ogg` | vomutiny | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_naniteslaunch.ogg` | vonaniteslaunch | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_nanitesrip.ogg` | vonanitesrip | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_needdefense.ogg` | voneeddefense | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_needmines.ogg` | voneedmines | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_neednanonconst.ogg` | voneednanonconst | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_needoffense.ogg` | voneedoffense | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_needrescue.ogg` | voneedrescue | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_needtpscout.ogg` | voneedtpscout | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_noImeanit.ogg` | vonoImeanit | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_notenough.ogg` | vonotenough | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_notime.ogg` | vonotime | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_notseen.ogg` | vonotseen | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_nowyouseeme.ogg` | vonowyouseeme | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_offturret.ogg` | vooffturret | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_pickteams.ogg` | vopickteams | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_pickuppods.ogg` | vopickuppods | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_pickuptech.ogg` | vopickuptech | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_podded.ogg` | vopodded | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_pushop.ogg` | vopushop | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_ready.ogg` | voready | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_ripped.ogg` | voripped | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_rush.ogg` | vorush | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_scoutahead.ogg` | voscoutahead | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_settingssuck.ogg` | vosettingssuck | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_spotted.ogg` | vospotted | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_stayoutofmiddle.ogg` | vostayoutofmiddle | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_stopgrin.ogg` | vostopgrin | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_stopspam.ogg` | vostopspam | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_theyexp.ogg` | votheyexp | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_theysup.ogg` | votheysup | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_theytac.ogg` | votheytac | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_wakeup.ogg` | vowakeup | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |
| `vo_ma_wonttolerate.ogg` | vowonttolerate | VoiceOver | Player/Comms VO (serialized) | — | mission-advisor, taunt, vo |

## Builder voice-over

| File | Logical sound(s) | Bus | Playback role | IGC event | Tags |
|---|---|---|---|---|---|
| `vo_builder_aintgonnawork.ogg` | droneAintGonnaWorkSound | VoiceOver | Commander VO (SAL, serialized) | droneAintGonnaWork | builder-vo |
| `vo_builder_expansion.ogg` | expansionCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | builder-vo |
| `vo_builder_garrison.ogg` | garrisonCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | builder-vo |
| `vo_builder_mine.ogg` | mineCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | builder-vo |
| `vo_builder_nobuild.ogg` | droneWhereToBuildSound | VoiceOver | Commander VO (SAL, serialized) | droneWhereToBuild | builder-vo |
| `vo_builder_outpost.ogg` | outpostCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | builder-vo |
| `vo_builder_palisade.ogg` | palisadeCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | builder-vo |
| `vo_builder_refinery.ogg` | refineryCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | builder-vo |
| `vo_builder_research.ogg` | researchCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | builder-vo |
| `vo_builder_shipyard.ogg` | shipyardCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | builder-vo |
| `vo_builder_supremecy.ogg` | supremecyCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | builder-vo |
| `vo_builder_tactical.ogg` | tacticalCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | builder-vo |
| `vo_builder_teleport.ogg` | teleportCompletedSound | VoiceOver | Commander VO (SAL, serialized) | — | builder-vo |
| `vo_builder_teleportenemy.ogg` | teleportProbeSpottedSound | VoiceOver | Commander VO (SAL, serialized) | teleportProbeSpotted | builder-vo |
| `vo_builder_teleportprobe.ogg` | teleportProbeActiveSound | VoiceOver | Commander VO (SAL, serialized) | teleportProbeActive | builder-vo |

## Miner voice-over

| File | Logical sound(s) | Bus | Playback role | IGC event | Tags |
|---|---|---|---|---|---|
| `vo_miner_cantdothat.ogg` | droneCantDoThatSound | VoiceOver | Commander VO (SAL, serialized) | droneCantDoThat | comms, miner-vo |
| `vo_miner_dontgetpaid.ogg` | droneTooMuchDamageSound | VoiceOver | Commander VO (SAL, serialized) | droneTooMuchDamage | comms, miner-vo |
| `vo_miner_enemyonscope.ogg` | droneEnemyOnScopeSound | VoiceOver | Commander VO (SAL, serialized) | droneEnemyOnScope | comms, miner-vo |
| `vo_miner_intransit.ogg` | droneInTransitSound | VoiceOver | Commander VO (SAL, serialized) | droneInTransit | comms, miner-vo |
| `vo_miner_mining.ogg` | droneMiningSound | VoiceOver | Commander VO (SAL, serialized) | droneMining | comms, miner-vo |
| `vo_miner_report4duty.ogg` | droneReport4DutySound | VoiceOver | Commander VO (SAL, serialized) | droneReport4Duty | comms, miner-vo |
| `vo_miner_underattack.ogg` | droneUnderAttackSound | VoiceOver | Commander VO (SAL, serialized) | droneUnderAttack | comms, miner-vo |
| `vo_miner_yougotit.ogg` | droneYouGotItSound | VoiceOver | Commander VO (SAL, serialized) | droneYouGotIt | comms, miner-vo |

## Ambient / environmental loops

| File | Logical sound(s) | Bus | Playback role | IGC event | Tags |
|---|---|---|---|---|---|
| `accelcapital.ogg` | CapSidethrustExteriorSound, CapSidethrustInteriorSound, capitalShipThrustExteriorSound … | SFX | Ambient / looping bed | CapSidethrustExterior, CapSidethrustInterior | — |
| `accelsmall.ogg` | SidethrustExteriorSound, SidethrustInteriorSound, capitalShipTurnExteriorSound … | SFX | Ambient / looping bed | SidethrustExterior, SidethrustInterior | — |
| `adjust.ogg` | smallShipTurnExteriorSound, smallShipTurnInteriorSound | SFX | Ambient / looping bed | — | — |
| `alephloop.ogg` | alephSound | SFX | Ambient / looping bed | aleph | — |
| `amb10_48int.ogg` | RUStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, interior, loop |
| `amb10_49int.ogg` | RUStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, interior, loop |
| `amb10_50int.ogg` | RUStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, interior, loop |
| `amb10_52int.ogg` | RUStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, interior, loop |
| `amb10_53int.ogg` | RUStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, interior, loop |
| `amb10_54int.ogg` | RUStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, interior, loop |
| `amb10_55int.ogg` | RUStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, interior, loop |
| `amb10_56int.ogg` | RUStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, interior, loop |
| `amb10_57int.ogg` | RUStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, interior, loop |
| `amb10_59int.ogg` | RUStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, interior, loop |
| `amb10_62int.ogg` | RUStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, interior, loop |
| `announce1.ogg` | ILStarbaseInternalSound | SFX | Ambient / looping bed | — | announcement |
| `announce4.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | announcement |
| `announce5.ogg` | BLStarbaseInternalSound, ILStarbaseInternalSound | SFX | Ambient / looping bed | — | announcement |
| `asteroid.ogg` | asteroidSound | SFX | Ambient / looping bed | asteroid | — |
| `beltambient_1.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_2.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_22.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_3b.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_3c.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_4.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_5.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_5b.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_6.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_6b.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_7.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_8.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_8b.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `beltambient_9.ogg` | BLStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, asteroid-belt, loop |
| `bioambient_1.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_2.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_21.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_22.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_23.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_24.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_25.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_26.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_27.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_3.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_4.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_5.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_6.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `bioambient_7.ogg` | BOStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, biological, loop |
| `boundsalert.ogg` | outOfBoundsLoopSound | SFX | Ambient / looping bed | outOfBoundsLoop | — |
| `clank.ogg` | ILStationExternalSound | SFX | Ambient / looping bed | — | — |
| `constructionsound.ogg` | buildSound | SFX | Ambient / looping bed | build | — |
| `dn_fagiptpdes.ogg` | dn_ptdebshlDropSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `dn_faphbkamb01.ogg` | dn_PhStarbaseInternalSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `dn_faphbkamb02.ogg` | dn_PhStarbaseInternalSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `dn_faphbkamb03.ogg` | dn_PhStarbaseInternalSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `dn_faphbkamb04.ogg` | dn_PhStarbaseInternalSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `dn_faphbkamb05.ogg` | dn_PhStarbaseInternalSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `dn_faphbkamb06.ogg` | dn_PhStarbaseInternalSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `dn_faphbkamb07.ogg` | dn_PhStarbaseInternalSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `dn_faphbkamb08.ogg` | dn_PhStarbaseInternalSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `dn_faphbkamb09.ogg` | dn_PhStarbaseInternalSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `dn_faphbkamb10.ogg` | dn_PhStarbaseInternalSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `dn_faphbkamb11.ogg` | dn_PhStarbaseInternalSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `dn_ppedrp.ogg` | dn_ptplsgenDropSound | SFX | Ambient / looping bed | — | dark-nebula, mod |
| `faohbkamb01.ogg` | OHStarbaseInternalSound | SFX | Ambient / looping bed | — | faction, faoh |
| `faohbkamb02.ogg` | OHStarbaseInternalSound | SFX | Ambient / looping bed | — | faction, faoh |
| `faohbkamb03.ogg` | OHStarbaseInternalSound | SFX | Ambient / looping bed | — | faction, faoh |
| `faohbkamb04.ogg` | OHStarbaseInternalSound | SFX | Ambient / looping bed | — | faction, faoh |
| `faohbkamb05.ogg` | OHStarbaseInternalSound | SFX | Ambient / looping bed | — | faction, faoh |
| `faohbkamb06.ogg` | OHStarbaseInternalSound | SFX | Ambient / looping bed | — | faction, faoh |
| `faohbkamb07.ogg` | OHStarbaseInternalSound | SFX | Ambient / looping bed | — | faction, faoh |
| `faohbkamb08.ogg` | OHStarbaseInternalSound | SFX | Ambient / looping bed | — | faction, faoh |
| `faohbkamb09.ogg` | OHStarbaseInternalSound | SFX | Ambient / looping bed | — | faction, faoh |
| `faohbkamb10.ogg` | OHStarbaseInternalSound | SFX | Ambient / looping bed | — | faction, faoh |
| `gigambient_1.ogg` | GCStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, gig, loop |
| `gigambient_2.ogg` | GCStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, gig, loop |
| `gigambient_21.ogg` | GCStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, gig, loop |
| `gigambient_25.ogg` | GCStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, gig, loop |
| `gigambient_3.ogg` | GCStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, gig, loop |
| `gigambient_4.ogg` | GCStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, gig, loop |
| `gigambient_5.ogg` | GCStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, gig, loop |
| `gigambient_6.ogg` | GCStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, gig, loop |
| `gigambient_7.ogg` | GCStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, gig, loop |
| `ironambient_1.ogg` | ILStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, faction, iron, loop |
| `ironambient_2.ogg` | ILStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, faction, iron, loop |
| `ironambient_21.ogg` | ILStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, faction, iron, loop |
| `ironambient_24.ogg` | ILStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, faction, iron, loop |
| `ironambient_3.ogg` | ILStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, faction, iron, loop |
| `ironambient_4.ogg` | ILStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, faction, iron, loop |
| `ironambient_5.ogg` | ILStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, faction, iron, loop |
| `ironambient_6.ogg` | ILStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, faction, iron, loop |
| `ironambient_7.ogg` | ILStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, faction, iron, loop |
| `ironambient_8.ogg` | ILStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, faction, iron, loop |
| `ironambient_9.ogg` | ILStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, faction, iron, loop |
| `lizard01.ogg` | DEStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, lizard |
| `lizard02.ogg` | DEStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, lizard |
| `lizard03.ogg` | DEStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, lizard |
| `lizard04.ogg` | DEStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, lizard |
| `lizard05.ogg` | DEStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, lizard |
| `lizard06.ogg` | DEStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, lizard |
| `lizard07.ogg` | DEStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, lizard |
| `lizard08.ogg` | DEStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, lizard |
| `lizard09.ogg` | DEStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, lizard |
| `lizard10.ogg` | DEStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, lizard |
| `lshipint.ogg` | capitalShipInteriorSound | SFX | Ambient / looping bed | — | — |
| `miningsound.ogg` | miningSound | SFX | Ambient / looping bed | mining | — |
| `missilelockonme.ogg` | missileLockSound | SFX | Ambient / looping bed | missileLock | — |
| `p1_ambient1.ogg` | p1_StarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, player-faction |
| `p1_ambient10.ogg` | p1_StarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, player-faction |
| `p1_ambient11.ogg` | p1_StarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, player-faction |
| `p1_ambient12.ogg` | p1_StarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, player-faction |
| `p1_ambient2.ogg` | p1_StarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, player-faction |
| `p1_ambient3.ogg` | p1_StarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, player-faction |
| `p1_ambient4.ogg` | p1_StarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, player-faction |
| `p1_ambient5.ogg` | p1_StarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, player-faction |
| `p1_ambient6.ogg` | p1_StarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, player-faction |
| `p1_ambient7.ogg` | p1_StarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, player-faction |
| `p1_ambient8.ogg` | p1_StarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, player-faction |
| `p1_ambient9.ogg` | p1_StarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, player-faction |
| `probe.ogg` | probe1Sound | SFX | Ambient / looping bed | — | — |
| `shipint.ogg` | BLStarbaseInternalSound, BOStarbaseInternalSound, DEStarbaseInternalSound … | SFX | Ambient / looping bed | — | — |
| `shipout.ogg` | ILStationExternalSound, asteroidSound, capitalShipExteriorSound … | SFX | Ambient / looping bed | asteroid | — |
| `sshipint.ogg` | smallShipInteriorSound | SFX | Ambient / looping bed | — | — |
| `static1.ogg` | ILStationExternalSound | SFX | Ambient / looping bed | — | comms, radio-static |
| `static2.ogg` | ILStationExternalSound | SFX | Ambient / looping bed | — | comms, radio-static |
| `ta_drac01.ogg` | ta_dracStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, draconian |
| `ta_drac02.ogg` | ta_dracStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, draconian |
| `ta_drac03.ogg` | ta_dracStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, draconian |
| `ta_drac04.ogg` | ta_dracStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, draconian |
| `ta_drac06.ogg` | ta_dracStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, draconian |
| `ta_drac07.ogg` | ta_dracStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, draconian |
| `tfambient1.ogg` | TFStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, tf |
| `tfambient12.ogg` | TFStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, tf |
| `tfambient2.ogg` | TFStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, tf |
| `tfambient3.ogg` | TFStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, tf |
| `tfambient4.ogg` | TFStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, tf |
| `tfambient5.ogg` | TFStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, tf |
| `tfambient6.ogg` | TFStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, tf |
| `tfambient7.ogg` | TFStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, tf |
| `tfambient8.ogg` | TFStarbaseInternalSound | SFX | Ambient / looping bed | — | ambient, loop, tf |
| `weed_valkamb01.ogg` | weed_valkStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, weed |
| `weed_valkamb02.ogg` | weed_valkStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, weed |
| `weed_valkamb03.ogg` | weed_valkStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, weed |
| `weed_valkamb04.ogg` | weed_valkStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, weed |
| `weed_valkamb05.ogg` | weed_valkStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, weed |
| `weed_valkamb06.ogg` | weed_valkStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, weed |
| `weed_valkamb07.ogg` | weed_valkStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, weed |
| `weed_valkamb08.ogg` | weed_valkStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, weed |
| `weed_valkamb09.ogg` | weed_valkStarbaseInternalSound | SFX | Ambient / looping bed | — | creature, weed |

## Weapons (fire / burst / power-up)

| File | Logical sound(s) | Bus | Playback role | IGC event | Tags |
|---|---|---|---|---|---|
| `basecannon.ogg` | basecannonShotSound | SFX | Weapon shot | — | cannon, weapon |
| `cannon1.ogg` | cannon1ShotSound | SFX | Weapon shot | — | cannon, weapon |
| `cannon1pwrup.ogg` | cannon1ActivationSound | SFX | Weapon power-up / activation | — | cannon, power-up, weapon |
| `cannon2.ogg` | cannon2ShotSound | SFX | Weapon shot | — | cannon, weapon |
| `cannon2pwrup.ogg` | cannon2ActivationSound | SFX | Weapon power-up / activation | — | cannon, power-up, weapon |
| `dn_ptaftcrs.ogg` | dn_AftCrsInSound | SFX | Weapon fire / burst | — | dark-nebula, mod |
| `dn_ptafthpr.ogg` | dn_AftHprInSound, dn_AftHprOutSound | SFX | Weapon fire / burst | — | dark-nebula, mod |
| `dn_ptafthvy.ogg` | dn_AftHvyInSound | SFX | Weapon fire / burst | — | dark-nebula, mod |
| `dn_ptaftion.ogg` | dn_AftIonInSound | SFX | Weapon fire / burst | — | dark-nebula, mod |
| `dn_ptaftlgt.ogg` | dn_AftLgtInSound | SFX | Weapon fire / burst | — | dark-nebula, mod |
| `dn_ptlasbls1drop.ogg` | dn_ptlasbls1DropSound | SFX | Weapon fire / burst | — | dark-nebula, mod |
| `dn_ptlasbls2drop.ogg` | dn_ptlasbls2DropSound | SFX | Weapon fire / burst | — | dark-nebula, mod |
| `dn_ptlascan1pwrup.ogg` | dn_ptlascan1ActivationSound | SFX | Weapon power-up / activation | — | dark-nebula, mod, power-up |
| `dn_ptlascan1shoot.ogg` | dn_ptlascan1BurstSound, dn_ptlascan1ShotSound | SFX | Weapon shot | — | dark-nebula, mod, weapon-fire |
| `dn_ptlascan2pwrup.ogg` | dn_ptlascan2ActivationSound | SFX | Weapon power-up / activation | — | dark-nebula, mod, power-up |
| `dn_ptlascan2shoot.ogg` | dn_ptlascan2BurstSound, dn_ptlascan2ShotSound | SFX | Weapon shot | — | dark-nebula, mod, weapon-fire |
| `dn_ptlngtompwrup.ogg` | dn_ptlngtomActivationSound | SFX | Weapon power-up / activation | — | dark-nebula, mod, power-up |
| `dn_ptlngtomshoot.ogg` | dn_ptlngtomBurstSound, dn_ptlngtomShotSound | SFX | Weapon shot | — | dark-nebula, mod, weapon-fire |
| `dn_ptvulcan1pwrup.ogg` | dn_ptvulcan1ActivationSound | SFX | Weapon power-up / activation | — | dark-nebula, mod, power-up |
| `dn_ptvulcan1shoot.ogg` | dn_ptvulcan1BurstSound, dn_ptvulcan1ShotSound | SFX | Weapon shot | — | dark-nebula, mod, weapon-fire |
| `dn_ptvulcan2pwrup.ogg` | dn_ptvulcan2ActivationSound | SFX | Weapon power-up / activation | — | dark-nebula, mod, power-up |
| `dn_ptvulcan2shoot.ogg` | dn_ptvulcan2BurstSound, dn_ptvulcan2ShotSound | SFX | Weapon shot | — | dark-nebula, mod, weapon-fire |
| `ef42.ogg` | antishieldShotSound | SFX | Weapon shot | — | effect |
| `er30mmi.ogg` | er_thirtymmburst | SFX | Weapon fire / burst | — | — |
| `er9mmi.ogg` | er_ninemmburst | SFX | Weapon fire / burst | — | — |
| `faohhammer1.ogg` | faohhammer1BurstSound, faohhammer1ShotSound | SFX | Weapon shot | — | faction, faoh |
| `faohhammer2.ogg` | faohhammer2BurstSound, faohhammer2ShotSound | SFX | Weapon shot | — | faction, faoh |
| `faohhammer3.ogg` | faohhammer3BurstSound, faohhammer3ShotSound | SFX | Weapon shot | — | faction, faoh |
| `faohomega1.ogg` | faohomega1BurstSound, faohomega1ShotSound | SFX | Weapon shot | — | faction, faoh |
| `faohomega2.ogg` | faohomega2BurstSound, faohomega2ShotSound | SFX | Weapon shot | — | faction, faoh |
| `faohomega3.ogg` | faohomega3BurstSound, faohomega3ShotSound | SFX | Weapon shot | — | faction, faoh |
| `faohtau1.ogg` | faohtau1BurstSound, faohtau1ShotSound | SFX | Weapon shot | — | faction, faoh |
| `faohtau2.ogg` | faohtau2BurstSound, faohtau2ShotSound | SFX | Weapon shot | — | faction, faoh |
| `faohtau3.ogg` | faohtau3BurstSound, faohtau3ShotSound | SFX | Weapon shot | — | faction, faoh |
| `gauss.ogg` | gaussShotSound | SFX | Weapon shot | — | gauss, weapon |
| `gausspwrup.ogg` | gaussActivationSound | SFX | Weapon power-up / activation | — | gauss, power-up, weapon |
| `hvycannon.ogg` | hvycannonShotSound | SFX | Weapon shot | — | cannon, weapon |
| `iafter.ogg` | afterburner1InSound | SFX | Weapon fire / burst | — | — |
| `lturret.ogg` | largeTurretSound | SFX | Weapon fire / burst | — | — |
| `machinegun1.ogg` | machineGun1BurstSound, machineGun1ShotSound | SFX | Weapon shot | — | machinegun, weapon |
| `machinegun1pwrup.ogg` | machineGun1ActivationSound, plasmagat1ActivationSound | SFX | Weapon power-up / activation | — | machinegun, power-up, weapon |
| `machinegun2.ogg` | machineGun2BurstSound, machineGun2ShotSound | SFX | Weapon shot | — | machinegun, weapon |
| `machinegun2pwrup.ogg` | machineGun2ActivationSound, plasmagat2ActivationSound | SFX | Weapon power-up / activation | — | machinegun, power-up, weapon |
| `machinegun3.ogg` | machineGun3BurstSound, machineGun3ShotSound | SFX | Weapon shot | — | machinegun, weapon |
| `machinegun3pwrup.ogg` | machineGun3ActivationSound, plasmagat3ActivationSound | SFX | Weapon power-up / activation | — | machinegun, power-up, weapon |
| `minigun1.ogg` | miniGun1BurstSound, miniGun1ShotSound | SFX | Weapon shot | — | minigun, weapon |
| `minigun1pwrup.ogg` | antiBaseCap1ActivationSound, miniGun1ActivationSound, plasmaac1ActivationSound … | SFX | Weapon power-up / activation | — | minigun, power-up, weapon |
| `minigun2.ogg` | miniGun2BurstSound, miniGun2ShotSound | SFX | Weapon shot | — | minigun, weapon |
| `minigun2pwrup.ogg` | antiBaseCap2ActivationSound, miniGun2ActivationSound, plasmaac2ActivationSound … | SFX | Weapon power-up / activation | — | minigun, power-up, weapon |
| `minigun3.ogg` | miniGun3BurstSound, miniGun3ShotSound | SFX | Weapon shot | — | minigun, weapon |
| `minigun3pwrup.ogg` | miniGun3ActivationSound, plasmaac3ActivationSound, plasmamini3ActivationSound | SFX | Weapon power-up / activation | — | minigun, power-up, weapon |
| `mmissile.ogg` | HornetMissileFlight | SFX | Weapon fire / burst | — | — |
| `mturret.ogg` | mediumTurretSound | SFX | Weapon fire / burst | — | — |
| `p1_hyperi.ogg` | p1_hyperiInSound | SFX | Weapon fire / burst | — | — |
| `p1_hypero.ogg` | p1_hyperoOutSound | SFX | Weapon fire / burst | — | — |
| `plasmaac1.ogg` | plasmaac1BurstSound, plasmaac1ShotSound | SFX | Weapon shot | — | plasma, weapon |
| `plasmaac2.ogg` | plasmaac2BurstSound, plasmaac2ShotSound | SFX | Weapon shot | — | plasma, weapon |
| `plasmaac3.ogg` | plasmaac3BurstSound, plasmaac3ShotSound | SFX | Weapon shot | — | plasma, weapon |
| `plasmagat1.ogg` | plasmagat1BurstSound, plasmagat1ShotSound | SFX | Weapon shot | — | plasma, weapon |
| `plasmagat2.ogg` | plasmagat2BurstSound, plasmagat2ShotSound | SFX | Weapon shot | — | plasma, weapon |
| `plasmagat3.ogg` | plasmagat3BurstSound, plasmagat3ShotSound | SFX | Weapon shot | — | plasma, weapon |
| `plasmamini1.ogg` | plasmamini1BurstSound, plasmamini1ShotSound | SFX | Weapon shot | — | plasma, weapon |
| `plasmamini2.ogg` | plasmamini2BurstSound, plasmamini2ShotSound | SFX | Weapon shot | — | plasma, weapon |
| `plasmamini3.ogg` | plasmamini3BurstSound, plasmamini3ShotSound | SFX | Weapon shot | — | plasma, weapon |
| `plasmarifle1.ogg` | antiShield1BurstSound, antiShield1ShotSound, antiUtility1BurstSound … | SFX | Weapon shot | — | plasma, weapon |
| `plasmarifle1pwrup.ogg` | antiShield1ActivationSound, antiUtility1ActivationSound | SFX | Weapon power-up / activation | — | plasma, power-up, weapon |
| `plasmarifle2.ogg` | antiShield2BurstSound, antiShield2ShotSound, antiUtility2BurstSound … | SFX | Weapon shot | — | plasma, weapon |
| `plasmarifle2pwrup.ogg` | antiShield2ActivationSound, antiUtility2ActivationSound | SFX | Weapon power-up / activation | — | plasma, power-up, weapon |
| `plasmarifle3.ogg` | antiShield3BurstSound, antiShield3ShotSound, antiUtility3BurstSound … | SFX | Weapon shot | — | plasma, weapon |
| `plasmarifle3pwrup.ogg` | antiShield3ActivationSound, antiUtility3ActivationSound, sbcannonActivationSound | SFX | Weapon power-up / activation | — | plasma, power-up, weapon |
| `pulselaser.ogg` | pulselaserBurstSound, pulselaserShotSound | SFX | Weapon shot | — | — |
| `pwrup.ogg` | undockSound | SFX | Weapon power-up / activation | undock | power-up |
| `repairweapon.ogg` | repairWeapon1ShotSound, repairWeapon2ShotSound | SFX | Weapon shot | — | — |
| `sbcannon.ogg` | sbcannonShotSound | SFX | Weapon shot | — | cannon, weapon |
| `sniperlaser1.ogg` | sniperLaser1BurstSound, sniperLaser1ShotSound | SFX | Weapon shot | — | laser, sniper, weapon |
| `sniperlaser1pwrup.ogg` | repairWeapon1ActivationSound, repairWeapon2ActivationSound, sniperLaser1ActivationSound | SFX | Weapon power-up / activation | — | laser, power-up, sniper, weapon |
| `sniperlaser2.ogg` | sniperLaser2BurstSound, sniperLaser2ShotSound | SFX | Weapon shot | — | laser, sniper, weapon |
| `sniperlaser2pwrup.ogg` | sniperLaser2ActivationSound | SFX | Weapon power-up / activation | — | laser, power-up, sniper, weapon |
| `striker.ogg` | strikerBurstSound, strikerShotSound | SFX | Weapon shot | — | — |
| `sturret.ogg` | smallTurretSound | SFX | Weapon fire / burst | — | — |
| `ta_taser.ogg` | ta_taserBurstSound, ta_taserShotSound | SFX | Weapon shot | — | — |
| `tfpwrup.ogg` | antishieldActivationSound | SFX | Weapon power-up / activation | — | power-up |
| `turretcannon1.ogg` | turretCannon1ShotSound | SFX | Weapon shot | — | cannon, turret, weapon |
| `turretcannon1pwrup.ogg` | turretCannon1ActivationSound | SFX | Weapon power-up / activation | — | cannon, power-up, turret, weapon |
| `turretcannon2.ogg` | skyRipper1BurstSound, skyRipper1ShotSound, turretCannon2ShotSound | SFX | Weapon shot | — | cannon, turret, weapon |
| `turretcannon2pwrup.ogg` | basecannonActivationSound, skyRipper1ActivationSound, turretCannon2ActivationSound | SFX | Weapon power-up / activation | — | cannon, power-up, turret, weapon |
| `turretcannon3.ogg` | skyRipper2BurstSound, skyRipper2ShotSound, turretCannon3ShotSound | SFX | Weapon shot | — | cannon, turret, weapon |
| `turretcannon3pwrup.ogg` | hvycannonActivationSound, skyRipper2ActivationSound, turretCannon3ActivationSound | SFX | Weapon power-up / activation | — | cannon, power-up, turret, weapon |
| `turretgun1.ogg` | antiBaseCap1BurstSound, antiBaseCap1ShotSound, turretGun1BurstSound … | SFX | Weapon shot | — | gun, turret, weapon |
| `turretgun1pwrup.ogg` | turretGun1ActivationSound | SFX | Weapon power-up / activation | — | gun, power-up, turret, weapon |
| `turretgun2.ogg` | antiBaseCap2BurstSound, antiBaseCap2ShotSound, turretGun2BurstSound … | SFX | Weapon shot | — | gun, turret, weapon |
| `turretgun2pwrup.ogg` | turretGun2ActivationSound | SFX | Weapon power-up / activation | — | gun, power-up, turret, weapon |
| `turretgun3.ogg` | turretGun3BurstSound, turretGun3ShotSound | SFX | Weapon shot | — | gun, turret, weapon |
| `turretgun3pwrup.ogg` | turretGun3ActivationSound | SFX | Weapon power-up / activation | — | gun, power-up, turret, weapon |
| `weed_burni.ogg` | weed_burniInSound | SFX | Weapon fire / burst | — | creature, weed |
| `weed_burno.ogg` | weed_burnoOutSound | SFX | Weapon fire / burst | — | creature, weed |
| `xafter.ogg` | afterburner1OutSound | SFX | Weapon fire / burst | — | — |

## Positional 3D sound effects

| File | Logical sound(s) | Bus | Playback role | IGC event | Tags |
|---|---|---|---|---|---|
| `capsulestart.ogg` | capsulein | SFX | Positional 3D SFX | — | — |
| `capsulestop.ogg` | capsuleout | SFX | Positional 3D SFX | — | — |
| `cloakoff.ogg` | cloak1OffSound | SFX | Positional 3D SFX | — | — |
| `cloakon.ogg` | cloak1OnSound | SFX | Positional 3D SFX | — | — |
| `colisionlarge.ogg` | collisionSound | SFX | Positional 3D SFX | collision | — |
| `collisionmedium.ogg` | turretLimitSound | SFX | Positional 3D SFX | turretLimit | — |
| `dn_mqflch.ogg` | dn_MRMQuickFireLaunchSound | SFX | Positional 3D SFX | — | dark-nebula, mod |
| `ef10.ogg` | dn_MRPLaunchSound | SFX | Positional 3D SFX | — | effect |
| `ef100.ogg` | dn_SRPLaunchSound | SFX | Positional 3D SFX | — | effect |
| `ef11.ogg` | dn_LRPLaunchSound, dn_XRPLaunchSound | SFX | Positional 3D SFX | — | effect |
| `ef120.ogg` | otherHullHitSound, smallExplosionSound | SFX | Positional 3D SFX | otherHullHit, smallExplosion | effect |
| `ef24.ogg` | mediumExplosionSound | SFX | Positional 3D SFX | mediumExplosion | effect |
| `er30mm.ogg` | er_thirtymm | SFX | Positional 3D SFX | — | — |
| `er800mm.ogg` | er_eighthundredmm | SFX | Positional 3D SFX | — | — |
| `er9mm.ogg` | er_ninemm | SFX | Positional 3D SFX | — | — |
| `erbeam.ogg` | er_beam | SFX | Positional 3D SFX | — | — |
| `erflaka.ogg` | er_flaka, er_flakaburst | SFX | Positional 3D SFX | — | — |
| `erflakb.ogg` | er_flakb | SFX | Positional 3D SFX | — | — |
| `erflakc.ogg` | er_flakc | SFX | Positional 3D SFX | — | — |
| `explosionshockwave.ogg` | largeExplosionSound | SFX | Positional 3D SFX | largeExplosion | — |
| `hitmearmor1.ogg` | myHullHitSound | SFX | Positional 3D SFX | myHullHit | hit, impact |
| `hitmearmor2.ogg` | myHullHitSound | SFX | Positional 3D SFX | myHullHit | hit, impact |
| `hitmearmor3.ogg` | myHullHitSound | SFX | Positional 3D SFX | myHullHit | hit, impact |
| `hitmeshield1.ogg` | myShieldHitSound | SFX | Positional 3D SFX | myShieldHit | hit, impact |
| `hitmeshield2.ogg` | myShieldHitSound | SFX | Positional 3D SFX | myShieldHit | hit, impact |
| `hitmeshield3.ogg` | myShieldHitSound | SFX | Positional 3D SFX | myShieldHit | hit, impact |
| `hitrock.ogg` | rockHitSound | SFX | Positional 3D SFX | rockHit | — |
| `hoover.ogg` | pickUpCashSound, pickUpPartSound, rescuePlayerSound | SFX | Positional 3D SFX | pickUpCash, pickUpPart, rescuePlayer | — |
| `lrgslideout.ogg` | HornetMissileLaunch | SFX | Positional 3D SFX | — | — |
| `newtargetneutral.ogg` | newTargetSound | SFX | Positional 3D SFX | newTarget | — |
| `nofuel.ogg` | outOfFuelSound | SFX | Positional 3D SFX | outOfFuel | — |
| `noshld.ogg` | fastShieldDeactivate | SFX | Positional 3D SFX | — | — |
| `powerupdevelopment.ogg` | pickUpDevelopmentSound | SFX | Positional 3D SFX | pickUpDevelopment | — |
| `powerupshield.ogg` | pickUpPowerupSound | SFX | Positional 3D SFX | pickUpPowerup | — |
| `shieldup.ogg` | fastShieldActivate | SFX | Positional 3D SFX | — | — |

## UI & 2D sound effects

| File | Logical sound(s) | Bus | Playback role | IGC event | Tags |
|---|---|---|---|---|---|
| `accept.ogg` | acceptCommandSound, positiveButtonClickSound | SFX | 2D SFX / UI | acceptCommand, positiveButtonClick | — |
| `button/sound/cancel.ogg` | negativeButtonClickSound | SFX | 2D SFX / UI | negativeButtonClick | — |
| `countdownstart.ogg` | countdownStartingSound, ripcordOnLoopSound | SFX | 2D SFX / UI | countdownStarting, ripcordOnLoop | countdown, timer |
| `criticalmessage.ogg` | newCriticalMsgSound | SFX | 2D SFX / UI | newCriticalMsg | — |
| `dn_version.ogg` | dn_VersionSound | SFX | 2D SFX / UI | — | dark-nebula, mod |
| `dropmine.ogg` | deployMineSound | SFX | 2D SFX / UI | deployMine | — |
| `dropobject.ogg` | deployChaffSound, deployProbeSound | SFX | 2D SFX / UI | deployChaff, deployProbe | — |
| `ef03.ogg` | errorSound | SFX | 2D SFX / UI | error | effect |
| `enttxt.ogg` | composeChatMsgSound | SFX | 2D SFX / UI | composeChatMsg | — |
| `fuellow.ogg` | fuelLowSound | SFX | 2D SFX / UI | fuelLow | — |
| `groupweapons.ogg` | groupWeaponsSound | SFX | 2D SFX / UI | groupWeapons | — |
| `invest.ogg` | investSound | SFX | 2D SFX / UI | invest | — |
| `jumpin.ogg` | jumpSound, personalJumpSound | SFX | 2D SFX / UI | jump, personalJump | — |
| `landed.ogg` | dockedSound | SFX | 2D SFX / UI | docked | — |
| `loadoutship.ogg` | changeLoadoutSound | SFX | 2D SFX / UI | changeLoadout | — |
| `missilelock.ogg` | missileToneSound | SFX | 2D SFX / UI | missileTone | — |
| `mount.ogg` | mountSound, startReloadSound | SFX | 2D SFX / UI | mount, startReload | — |
| `mousedown.ogg` | mouseclickSound | SFX | 2D SFX / UI | mouseclick | — |
| `button/sound/mouseover.ogg` | mouseoverSound | SFX | 2D SFX / UI | mouseover | — |
| `newtarget.ogg` | newShipSound | SFX | 2D SFX / UI | newShip | — |
| `newtargetenemy.ogg` | newEnemySound | SFX | 2D SFX / UI | newEnemy | — |
| `noncriticalmessage.ogg` | newNonCriticalMsgSound | SFX | 2D SFX / UI | newNonCriticalMsg | — |
| `outofammo.ogg` | ammoLowSound | SFX | 2D SFX / UI | ammoLow | — |
| `panel.ogg` | panelSound | SFX | 2D SFX / UI | panel | — |
| `payday.ogg` | paydaySound | SFX | 2D SFX / UI | payday | — |
| `receivechat.ogg` | newChatMsgSound, newOfflineChatMsgSound | SFX | 2D SFX / UI | newChatMsg, newOfflineChatMsg | — |
| `receivedcomchat.ogg` | newChatMsgFromCommanderSound | SFX | 2D SFX / UI | newChatMsgFromCommander | — |
| `receivedobjective.ogg` | newCommandMsgSound | SFX | 2D SFX / UI | newCommandMsg | — |
| `receivepersonalchat.ogg` | newPersonalMsgSound | SFX | 2D SFX / UI | newPersonalMsg | — |
| `send.ogg` | sendChatMsgSound | SFX | 2D SFX / UI | sendChatMsg | — |
| `text.ogg` | chatKeySound | SFX | 2D SFX / UI | chatKey | — |
| `ungroupweapons.ogg` | ungroupWeaponsSound | SFX | 2D SFX / UI | ungroupWeapons | — |
| `unmount.ogg` | mountedSound, unmountSound | SFX | 2D SFX / UI | mounted, unmount | — |
| `vector.ogg` | vectorLockSound | SFX | 2D SFX / UI | vectorLock | — |
| `windowslides.ogg` | paneSlideSound | SFX | 2D SFX / UI | paneSlide | — |

## Music & training slideshow tracks

| File | Logical sound(s) | Bus | Playback role | IGC event | Tags |
|---|---|---|---|---|---|
| `tm_0_blank.ogg` | tm_0_blankSound | VoiceOver | Music / slideshow narration | tm_0_blank | music, training-track |
| `tm_2_01.ogg` | tm_2_01Sound | VoiceOver | Music / slideshow narration | tm_2_01 | music, training-track |
| `tm_2_02.ogg` | tm_2_02Sound | VoiceOver | Music / slideshow narration | tm_2_02 | music, training-track |
| `tm_2_03.ogg` | tm_2_03Sound | VoiceOver | Music / slideshow narration | tm_2_03 | music, training-track |
| `tm_2_04.ogg` | tm_2_04Sound | VoiceOver | Music / slideshow narration | tm_2_04 | music, training-track |
| `tm_2_05.ogg` | tm_2_05Sound | VoiceOver | Music / slideshow narration | tm_2_05 | music, training-track |
| `tm_2_06.ogg` | tm_2_06Sound | VoiceOver | Music / slideshow narration | tm_2_06 | music, training-track |
| `tm_2_07.ogg` | tm_2_07Sound | VoiceOver | Music / slideshow narration | tm_2_07 | music, training-track |
| `tm_2_08.ogg` | tm_2_08Sound | VoiceOver | Music / slideshow narration | tm_2_08 | music, training-track |
| `tm_2_09.ogg` | tm_2_09Sound | VoiceOver | Music / slideshow narration | tm_2_09 | music, training-track |
| `tm_2_10.ogg` | tm_2_10Sound | VoiceOver | Music / slideshow narration | tm_2_10 | music, training-track |
| `tm_2_11.ogg` | tm_2_11Sound | VoiceOver | Music / slideshow narration | tm_2_11 | music, training-track |
| `tm_2_11r.ogg` | tm_2_11rSound | VoiceOver | Music / slideshow narration | tm_2_11r | music, training-track |
| `tm_2_12.ogg` | tm_2_12Sound | VoiceOver | Music / slideshow narration | tm_2_12 | music, training-track |
| `tm_2_13.ogg` | tm_2_13Sound | VoiceOver | Music / slideshow narration | tm_2_13 | music, training-track |
| `tm_2_14.ogg` | tm_2_14Sound | VoiceOver | Music / slideshow narration | tm_2_14 | music, training-track |
| `tm_2_15.ogg` | tm_2_15Sound | VoiceOver | Music / slideshow narration | tm_2_15 | music, training-track |
| `tm_2_16.ogg` | tm_2_16Sound | VoiceOver | Music / slideshow narration | tm_2_16 | music, training-track |
| `tm_2_16r.ogg` | tm_2_16rSound | VoiceOver | Music / slideshow narration | tm_2_16r | music, training-track |
| `tm_2_17.ogg` | tm_2_17Sound | VoiceOver | Music / slideshow narration | tm_2_17 | music, training-track |
| `tm_2_18.ogg` | tm_2_18Sound | VoiceOver | Music / slideshow narration | tm_2_18 | music, training-track |
| `tm_2_18r.ogg` | tm_2_18rSound | VoiceOver | Music / slideshow narration | tm_2_18r | music, training-track |
| `tm_2_19.ogg` | tm_2_19Sound | VoiceOver | Music / slideshow narration | tm_2_19 | music, training-track |
| `tm_2_20.ogg` | tm_2_20Sound | VoiceOver | Music / slideshow narration | tm_2_20 | music, training-track |
| `tm_2_21.ogg` | tm_2_21Sound | VoiceOver | Music / slideshow narration | tm_2_21 | music, training-track |
| `tm_2_22.ogg` | tm_2_22Sound | VoiceOver | Music / slideshow narration | tm_2_22 | music, training-track |
| `tm_2_23.ogg` | tm_2_23Sound | VoiceOver | Music / slideshow narration | tm_2_23 | music, training-track |
| `tm_2_24.ogg` | tm_2_24Sound | VoiceOver | Music / slideshow narration | tm_2_24 | music, training-track |
| `tm_2_24r.ogg` | tm_2_24rSound | VoiceOver | Music / slideshow narration | tm_2_24r | music, training-track |
| `tm_2_25.ogg` | tm_2_25Sound | VoiceOver | Music / slideshow narration | tm_2_25 | music, training-track |
| `tm_2_26.ogg` | tm_2_26Sound | VoiceOver | Music / slideshow narration | tm_2_26 | music, training-track |
| `tm_2_27.ogg` | tm_2_27Sound | VoiceOver | Music / slideshow narration | tm_2_27 | music, training-track |
| `tm_2_28.ogg` | tm_2_28Sound | VoiceOver | Music / slideshow narration | tm_2_28 | music, training-track |
| `tm_2_29.ogg` | tm_2_29Sound | VoiceOver | Music / slideshow narration | tm_2_29 | music, training-track |
| `tm_2_30.ogg` | tm_2_30Sound | VoiceOver | Music / slideshow narration | tm_2_30 | music, training-track |
| `tm_2_31.ogg` | tm_2_31Sound | VoiceOver | Music / slideshow narration | tm_2_31 | music, training-track |
| `tm_2_32.ogg` | tm_2_32Sound | VoiceOver | Music / slideshow narration | tm_2_32 | music, training-track |
| `tm_2_33.ogg` | tm_2_33Sound | VoiceOver | Music / slideshow narration | tm_2_33 | music, training-track |
| `tm_2_34.ogg` | tm_2_34Sound | VoiceOver | Music / slideshow narration | tm_2_34 | music, training-track |
| `tm_2_35.ogg` | tm_2_35Sound | VoiceOver | Music / slideshow narration | tm_2_35 | music, training-track |
| `tm_2_36.ogg` | tm_2_36Sound | VoiceOver | Music / slideshow narration | tm_2_36 | music, training-track |
| `tm_2_37.ogg` | tm_2_37Sound | VoiceOver | Music / slideshow narration | tm_2_37 | music, training-track |
| `tm_2_38.ogg` | tm_2_38Sound | VoiceOver | Music / slideshow narration | tm_2_38 | music, training-track |
| `tm_3_01.ogg` | tm_3_01Sound | VoiceOver | Music / slideshow narration | tm_3_01 | music, training-track |
| `tm_3_02.ogg` | tm_3_02Sound | VoiceOver | Music / slideshow narration | tm_3_02 | music, training-track |
| `tm_3_03.ogg` | tm_3_03Sound | VoiceOver | Music / slideshow narration | tm_3_03 | music, training-track |
| `tm_3_04.ogg` | tm_3_04Sound | VoiceOver | Music / slideshow narration | tm_3_04 | music, training-track |
| `tm_3_05.ogg` | tm_3_05Sound | VoiceOver | Music / slideshow narration | tm_3_05 | music, training-track |
| `tm_3_06.ogg` | tm_3_06Sound | VoiceOver | Music / slideshow narration | tm_3_06 | music, training-track |
| `tm_3_07.ogg` | tm_3_07Sound | VoiceOver | Music / slideshow narration | tm_3_07 | music, training-track |
| `tm_3_08.ogg` | tm_3_08Sound | VoiceOver | Music / slideshow narration | tm_3_08 | music, training-track |
| `tm_3_09.ogg` | tm_3_09Sound | VoiceOver | Music / slideshow narration | tm_3_09 | music, training-track |
| `tm_3_10.ogg` | tm_3_10Sound | VoiceOver | Music / slideshow narration | tm_3_10 | music, training-track |
| `tm_3_11.ogg` | tm_3_11Sound | VoiceOver | Music / slideshow narration | tm_3_11 | music, training-track |
| `tm_3_11r.ogg` | tm_3_11rSound | VoiceOver | Music / slideshow narration | tm_3_11r | music, training-track |
| `tm_3_12.ogg` | tm_3_12Sound | VoiceOver | Music / slideshow narration | tm_3_12 | music, training-track |
| `tm_3_12r.ogg` | tm_3_12rSound | VoiceOver | Music / slideshow narration | tm_3_12r | music, training-track |
| `tm_3_13.ogg` | tm_3_13Sound | VoiceOver | Music / slideshow narration | tm_3_13 | music, training-track |
| `tm_3_14.ogg` | tm_3_14Sound | VoiceOver | Music / slideshow narration | tm_3_14 | music, training-track |
| `tm_3_15.ogg` | tm_3_15Sound | VoiceOver | Music / slideshow narration | tm_3_15 | music, training-track |
| `tm_3_16.ogg` | tm_3_16Sound | VoiceOver | Music / slideshow narration | tm_3_16 | music, training-track |
| `tm_3_17.ogg` | tm_3_17Sound | VoiceOver | Music / slideshow narration | tm_3_17 | music, training-track |
| `tm_3_18.ogg` | tm_3_18Sound | VoiceOver | Music / slideshow narration | tm_3_18 | music, training-track |
| `tm_3_19.ogg` | tm_3_19Sound | VoiceOver | Music / slideshow narration | tm_3_19 | music, training-track |
| `tm_3_20.ogg` | tm_3_20Sound | VoiceOver | Music / slideshow narration | tm_3_20 | music, training-track |
| `tm_3_21.ogg` | tm_3_21Sound | VoiceOver | Music / slideshow narration | tm_3_21 | music, training-track |
| `tm_3_22.ogg` | tm_3_22Sound | VoiceOver | Music / slideshow narration | tm_3_22 | music, training-track |
| `tm_3_23.ogg` | tm_3_23Sound | VoiceOver | Music / slideshow narration | tm_3_23 | music, training-track |
| `tm_3_23r.ogg` | tm_3_23rSound | VoiceOver | Music / slideshow narration | tm_3_23r | music, training-track |
| `tm_3_24.ogg` | tm_3_24Sound | VoiceOver | Music / slideshow narration | tm_3_24 | music, training-track |
| `tm_3_25.ogg` | tm_3_25Sound | VoiceOver | Music / slideshow narration | tm_3_25 | music, training-track |
| `tm_3_26.ogg` | tm_3_26Sound | VoiceOver | Music / slideshow narration | tm_3_26 | music, training-track |
| `tm_3_27.ogg` | tm_3_27Sound | VoiceOver | Music / slideshow narration | tm_3_27 | music, training-track |
| `tm_3_28.ogg` | tm_3_28Sound | VoiceOver | Music / slideshow narration | tm_3_28 | music, training-track |
| `tm_3_29.ogg` | tm_3_29Sound | VoiceOver | Music / slideshow narration | tm_3_29 | music, training-track |
| `tm_3_30.ogg` | tm_3_30Sound | VoiceOver | Music / slideshow narration | tm_3_30 | music, training-track |
| `tm_3_31.ogg` | tm_3_31Sound | VoiceOver | Music / slideshow narration | tm_3_31 | music, training-track |
| `tm_3_31r.ogg` | tm_3_31rSound | VoiceOver | Music / slideshow narration | tm_3_31r | music, training-track |
| `tm_3_32.ogg` | tm_3_32Sound | VoiceOver | Music / slideshow narration | tm_3_32 | music, training-track |
| `tm_3_33.ogg` | tm_3_33Sound | VoiceOver | Music / slideshow narration | tm_3_33 | music, training-track |
| `tm_3_34.ogg` | tm_3_34Sound | VoiceOver | Music / slideshow narration | tm_3_34 | music, training-track |
| `tm_3_35.ogg` | tm_3_35Sound | VoiceOver | Music / slideshow narration | tm_3_35 | music, training-track |
| `tm_3_36.ogg` | tm_3_36Sound | VoiceOver | Music / slideshow narration | tm_3_36 | music, training-track |
| `tm_3_37.ogg` | tm_3_37Sound | VoiceOver | Music / slideshow narration | tm_3_37 | music, training-track |
| `tm_3_38.ogg` | tm_3_38Sound | VoiceOver | Music / slideshow narration | tm_3_38 | music, training-track |
| `tm_3_39.ogg` | tm_3_39Sound | VoiceOver | Music / slideshow narration | tm_3_39 | music, training-track |
| `tm_3_40.ogg` | tm_3_40Sound | VoiceOver | Music / slideshow narration | tm_3_40 | music, training-track |
| `tm_3_41.ogg` | tm_3_41Sound | VoiceOver | Music / slideshow narration | tm_3_41 | music, training-track |
| `tm_3_42.ogg` | tm_3_42Sound | VoiceOver | Music / slideshow narration | tm_3_42 | music, training-track |
| `tm_3_42r.ogg` | tm_3_42rSound | VoiceOver | Music / slideshow narration | tm_3_42r | music, training-track |
| `tm_3_43.ogg` | tm_3_43Sound | VoiceOver | Music / slideshow narration | tm_3_43 | music, training-track |
| `tm_3_44.ogg` | tm_3_44Sound | VoiceOver | Music / slideshow narration | tm_3_44 | music, training-track |
| `tm_3_45.ogg` | tm_3_45Sound | VoiceOver | Music / slideshow narration | tm_3_45 | music, training-track |
| `tm_3_46.ogg` | tm_3_46Sound | VoiceOver | Music / slideshow narration | tm_3_46 | music, training-track |
| `tm_3_47.ogg` | tm_3_47Sound | VoiceOver | Music / slideshow narration | tm_3_47 | music, training-track |
| `tm_3_48.ogg` | tm_3_48Sound | VoiceOver | Music / slideshow narration | tm_3_48 | music, training-track |
| `tm_3_49.ogg` | tm_3_49Sound | VoiceOver | Music / slideshow narration | tm_3_49 | music, training-track |
| `tm_3_49r.ogg` | tm_3_49rSound | VoiceOver | Music / slideshow narration | tm_3_49r | music, training-track |
| `tm_4_01.ogg` | tm_4_01Sound | VoiceOver | Music / slideshow narration | tm_4_01 | music, training-track |
| `tm_4_02.ogg` | tm_4_02Sound | VoiceOver | Music / slideshow narration | tm_4_02 | music, training-track |
| `tm_4_03.ogg` | tm_4_03Sound | VoiceOver | Music / slideshow narration | tm_4_03 | music, training-track |
| `tm_4_03r.ogg` | tm_4_03rSound | VoiceOver | Music / slideshow narration | tm_4_03r | music, training-track |
| `tm_4_04.ogg` | tm_4_04Sound | VoiceOver | Music / slideshow narration | tm_4_04 | music, training-track |
| `tm_4_04r.ogg` | tm_4_04rSound | VoiceOver | Music / slideshow narration | tm_4_04r | music, training-track |
| `tm_4_05.ogg` | tm_4_05Sound | VoiceOver | Music / slideshow narration | tm_4_05 | music, training-track |
| `tm_4_06.ogg` | tm_4_06Sound | VoiceOver | Music / slideshow narration | tm_4_06 | music, training-track |
| `tm_4_06r.ogg` | tm_4_06rSound | VoiceOver | Music / slideshow narration | tm_4_06r | music, training-track |
| `tm_4_07.ogg` | tm_4_07Sound | VoiceOver | Music / slideshow narration | tm_4_07 | music, training-track |
| `tm_4_08.ogg` | tm_4_08Sound | VoiceOver | Music / slideshow narration | tm_4_08 | music, training-track |
| `tm_4_08r.ogg` | tm_4_08rSound | VoiceOver | Music / slideshow narration | tm_4_08r | music, training-track |
| `tm_4_09.ogg` | tm_4_09Sound | VoiceOver | Music / slideshow narration | tm_4_09 | music, training-track |
| `tm_4_10.ogg` | tm_4_10Sound | VoiceOver | Music / slideshow narration | tm_4_10 | music, training-track |
| `tm_4_11.ogg` | tm_4_11Sound | VoiceOver | Music / slideshow narration | tm_4_11 | music, training-track |
| `tm_4_12.ogg` | tm_4_12Sound | VoiceOver | Music / slideshow narration | tm_4_12 | music, training-track |
| `tm_4_13.ogg` | tm_4_13Sound | VoiceOver | Music / slideshow narration | tm_4_13 | music, training-track |
| `tm_4_14.ogg` | tm_4_14Sound | VoiceOver | Music / slideshow narration | tm_4_14 | music, training-track |
| `tm_4_15.ogg` | tm_4_15Sound | VoiceOver | Music / slideshow narration | tm_4_15 | music, training-track |
| `tm_4_16.ogg` | tm_4_16Sound | VoiceOver | Music / slideshow narration | tm_4_16 | music, training-track |
| `tm_4_17.ogg` | tm_4_17Sound | VoiceOver | Music / slideshow narration | tm_4_17 | music, training-track |
| `tm_4_17r.ogg` | tm_4_17Sound, tm_4_17rSound | VoiceOver | Music / slideshow narration | tm_4_17, tm_4_17r | music, training-track |
| `tm_4_18.ogg` | tm_4_18Sound | VoiceOver | Music / slideshow narration | tm_4_18 | music, training-track |
| `tm_4_19.ogg` | tm_4_19Sound | VoiceOver | Music / slideshow narration | tm_4_19 | music, training-track |
| `tm_4_20.ogg` | tm_4_20Sound | VoiceOver | Music / slideshow narration | tm_4_20 | music, training-track |
| `tm_4_21.ogg` | tm_4_21Sound | VoiceOver | Music / slideshow narration | tm_4_21 | music, training-track |
| `tm_4_22.ogg` | tm_4_22Sound | VoiceOver | Music / slideshow narration | tm_4_22 | music, training-track |
| `tm_4_23.ogg` | tm_4_23Sound | VoiceOver | Music / slideshow narration | tm_4_23 | music, training-track |
| `tm_4_24.ogg` | tm_4_24Sound | VoiceOver | Music / slideshow narration | tm_4_24 | music, training-track |
| `tm_4_25.ogg` | tm_4_25Sound | VoiceOver | Music / slideshow narration | tm_4_25 | music, training-track |
| `tm_4_26.ogg` | tm_4_26Sound | VoiceOver | Music / slideshow narration | tm_4_26 | music, training-track |
| `tm_4_26r.ogg` | tm_4_26rSound | VoiceOver | Music / slideshow narration | tm_4_26r | music, training-track |
| `tm_4_27.ogg` | tm_4_27Sound | VoiceOver | Music / slideshow narration | tm_4_27 | music, training-track |
| `tm_4_28.ogg` | tm_4_28Sound | VoiceOver | Music / slideshow narration | tm_4_28 | music, training-track |
| `tm_4_29.ogg` | tm_4_29Sound | VoiceOver | Music / slideshow narration | tm_4_29 | music, training-track |
| `tm_4_29r.ogg` | tm_4_29rSound | VoiceOver | Music / slideshow narration | tm_4_29r | music, training-track |
| `tm_4_30.ogg` | tm_4_30Sound | VoiceOver | Music / slideshow narration | tm_4_30 | music, training-track |
| `tm_4_31.ogg` | tm_4_31Sound | VoiceOver | Music / slideshow narration | tm_4_31 | music, training-track |
| `tm_4_32.ogg` | tm_4_32Sound | VoiceOver | Music / slideshow narration | tm_4_32 | music, training-track |
| `tm_4_33.ogg` | tm_4_33Sound | VoiceOver | Music / slideshow narration | tm_4_33 | music, training-track |
| `tm_4_33r.ogg` | tm_4_33rSound | VoiceOver | Music / slideshow narration | tm_4_33r | music, training-track |
| `tm_4_34.ogg` | tm_4_34Sound | VoiceOver | Music / slideshow narration | tm_4_34 | music, training-track |
| `tm_4_35.ogg` | tm_4_35Sound | VoiceOver | Music / slideshow narration | tm_4_35 | music, training-track |
| `tm_5_01.ogg` | tm_5_01Sound | VoiceOver | Music / slideshow narration | tm_5_01 | music, training-track |
| `tm_5_02.ogg` | tm_5_02Sound | VoiceOver | Music / slideshow narration | tm_5_02 | music, training-track |
| `tm_5_03.ogg` | tm_5_03Sound | VoiceOver | Music / slideshow narration | tm_5_03 | music, training-track |
| `tm_5_03r.ogg` | tm_5_03rSound | VoiceOver | Music / slideshow narration | tm_5_03r | music, training-track |
| `tm_5_04.ogg` | tm_5_04Sound | VoiceOver | Music / slideshow narration | tm_5_04 | music, training-track |
| `tm_5_04r.ogg` | tm_5_04rSound | VoiceOver | Music / slideshow narration | tm_5_04r | music, training-track |
| `tm_5_05.ogg` | tm_5_05Sound | VoiceOver | Music / slideshow narration | tm_5_05 | music, training-track |
| `tm_5_05r.ogg` | tm_5_05rSound | VoiceOver | Music / slideshow narration | tm_5_05r | music, training-track |
| `tm_5_06.ogg` | tm_5_06Sound | VoiceOver | Music / slideshow narration | tm_5_06 | music, training-track |
| `tm_5_06r.ogg` | tm_5_06rSound | VoiceOver | Music / slideshow narration | tm_5_06r | music, training-track |
| `tm_5_07.ogg` | tm_5_07Sound | VoiceOver | Music / slideshow narration | tm_5_07 | music, training-track |
| `tm_5_07r.ogg` | tm_5_07rSound | VoiceOver | Music / slideshow narration | tm_5_07r | music, training-track |
| `tm_5_08.ogg` | tm_5_08Sound | VoiceOver | Music / slideshow narration | tm_5_08 | music, training-track |
| `tm_5_08r.ogg` | tm_5_08rSound | VoiceOver | Music / slideshow narration | tm_5_08r | music, training-track |
| `tm_5_09.ogg` | tm_5_09Sound | VoiceOver | Music / slideshow narration | tm_5_09 | music, training-track |
| `tm_5_10.ogg` | tm_5_10Sound | VoiceOver | Music / slideshow narration | tm_5_10 | music, training-track |
| `tm_5_11.ogg` | tm_5_11Sound | VoiceOver | Music / slideshow narration | tm_5_11 | music, training-track |
| `tm_5_12.ogg` | tm_5_12Sound | VoiceOver | Music / slideshow narration | tm_5_12 | music, training-track |
| `tm_5_13.ogg` | tm_5_13Sound | VoiceOver | Music / slideshow narration | tm_5_13 | music, training-track |
| `tm_5_13r.ogg` | tm_5_13rSound | VoiceOver | Music / slideshow narration | tm_5_13r | music, training-track |
| `tm_5_14.ogg` | tm_5_14Sound | VoiceOver | Music / slideshow narration | tm_5_14 | music, training-track |
| `tm_5_15.ogg` | tm_5_15Sound | VoiceOver | Music / slideshow narration | tm_5_15 | music, training-track |
| `tm_5_15r.ogg` | tm_5_15rSound | VoiceOver | Music / slideshow narration | tm_5_15r | music, training-track |
| `tm_5_16.ogg` | tm_5_16Sound | VoiceOver | Music / slideshow narration | tm_5_16 | music, training-track |
| `tm_5_17.ogg` | tm_5_17Sound | VoiceOver | Music / slideshow narration | tm_5_17 | music, training-track |
| `tm_5_18.ogg` | tm_5_18Sound | VoiceOver | Music / slideshow narration | tm_5_18 | music, training-track |
| `tm_5_18r.ogg` | tm_5_18rSound | VoiceOver | Music / slideshow narration | tm_5_18r | music, training-track |
| `tm_5_19.ogg` | tm_5_19Sound | VoiceOver | Music / slideshow narration | tm_5_19 | music, training-track |
| `tm_5_20.ogg` | tm_5_20Sound | VoiceOver | Music / slideshow narration | tm_5_20 | music, training-track |
| `tm_6_01.ogg` | tm_6_01Sound | VoiceOver | Music / slideshow narration | tm_6_01 | music, training-track |
| `tm_6_02.ogg` | tm_6_02Sound | VoiceOver | Music / slideshow narration | tm_6_02 | music, training-track |
| `tm_6_03.ogg` | tm_6_03Sound | VoiceOver | Music / slideshow narration | tm_6_03 | music, training-track |
| `tm_8_01.ogg` | tm_8_01Sound | VoiceOver | Music / slideshow narration | tm_8_01 | music, training-track |
| `tm_8_02.ogg` | tm_8_02Sound | VoiceOver | Music / slideshow narration | tm_8_02 | music, training-track |
| `tm_8_03.ogg` | tm_8_03Sound | VoiceOver | Music / slideshow narration | tm_8_03 | music, training-track |
| `tm_8_04.ogg` | tm_8_04Sound | VoiceOver | Music / slideshow narration | tm_8_04 | music, training-track |
| `tm_8_05.ogg` | tm_8_05Sound | VoiceOver | Music / slideshow narration | tm_8_05 | music, training-track |
| `tm_8_06.ogg` | tm_8_06Sound | VoiceOver | Music / slideshow narration | tm_8_06 | music, training-track |
| `tm_8_07.ogg` | tm_8_07Sound | VoiceOver | Music / slideshow narration | tm_8_07 | music, training-track |
| `tm_8_08.ogg` | tm_8_08Sound | VoiceOver | Music / slideshow narration | tm_8_08 | music, training-track |
| `tm_8_09.ogg` | tm_8_09Sound | VoiceOver | Music / slideshow narration | tm_8_09 | music, training-track |
| `tm_8_10.ogg` | tm_8_10Sound | VoiceOver | Music / slideshow narration | tm_8_10 | music, training-track |
| `tm_8_11.ogg` | tm_8_11Sound | VoiceOver | Music / slideshow narration | tm_8_11 | music, training-track |
| `tm_8_12.ogg` | tm_8_12Sound | VoiceOver | Music / slideshow narration | tm_8_12 | music, training-track |
| `tm_8_13.ogg` | tm_8_13Sound | VoiceOver | Music / slideshow narration | tm_8_13 | music, training-track |
| `tm_8_14.ogg` | tm_8_14Sound | VoiceOver | Music / slideshow narration | tm_8_14 | music, training-track |
| `tm_8_15.ogg` | tm_8_15Sound | VoiceOver | Music / slideshow narration | tm_8_15 | music, training-track |
| `tm_8_16.ogg` | tm_8_16Sound | VoiceOver | Music / slideshow narration | tm_8_16 | music, training-track |
| `tm_8_17.ogg` | tm_8_17Sound | VoiceOver | Music / slideshow narration | tm_8_17 | music, training-track |
| `tm_8_18.ogg` | tm_8_18Sound | VoiceOver | Music / slideshow narration | tm_8_18 | music, training-track |
| `tm_8_19.ogg` | tm_8_19Sound | VoiceOver | Music / slideshow narration | tm_8_19 | music, training-track |
| `tm_8_20.ogg` | tm_8_20Sound | VoiceOver | Music / slideshow narration | tm_8_20 | music, training-track |
| `tm_8_21.ogg` | tm_8_21Sound | VoiceOver | Music / slideshow narration | tm_8_21 | music, training-track |
| `tm_slide_1_01.ogg` | tm_slide_1_01Sound | VoiceOver | Music / slideshow narration | tm_slide_1_01 | music, narration, training-slideshow, training-track |
| `tm_slide_1_02.ogg` | tm_slide_1_02Sound | VoiceOver | Music / slideshow narration | tm_slide_1_02 | music, narration, training-slideshow, training-track |
| `tm_slide_1_03.ogg` | tm_slide_1_03Sound | VoiceOver | Music / slideshow narration | tm_slide_1_03 | music, narration, training-slideshow, training-track |
| `tm_slide_1_04a.ogg` | tm_slide_1_04aSound | VoiceOver | Music / slideshow narration | tm_slide_1_04a | music, narration, training-slideshow, training-track |
| `tm_slide_1_04b.ogg` | tm_slide_1_04bSound | VoiceOver | Music / slideshow narration | tm_slide_1_04b | music, narration, training-slideshow, training-track |
| `tm_slide_1_05.ogg` | tm_slide_1_05Sound | VoiceOver | Music / slideshow narration | tm_slide_1_05 | music, narration, training-slideshow, training-track |
| `tm_slide_1_06.ogg` | tm_slide_1_06Sound | VoiceOver | Music / slideshow narration | tm_slide_1_06 | music, narration, training-slideshow, training-track |
| `tm_slide_1_07.ogg` | tm_slide_1_07Sound | VoiceOver | Music / slideshow narration | tm_slide_1_07 | music, narration, training-slideshow, training-track |
| `tm_slide_1_08.ogg` | tm_slide_1_08Sound | VoiceOver | Music / slideshow narration | tm_slide_1_08 | music, narration, training-slideshow, training-track |
| `tm_slide_1_09.ogg` | tm_slide_1_09Sound | VoiceOver | Music / slideshow narration | tm_slide_1_09 | music, narration, training-slideshow, training-track |
| `tm_slide_1_10.ogg` | tm_slide_1_10Sound | VoiceOver | Music / slideshow narration | tm_slide_1_10 | music, narration, training-slideshow, training-track |
| `tm_slide_1_11.ogg` | tm_slide_1_11Sound | VoiceOver | Music / slideshow narration | tm_slide_1_11 | music, narration, training-slideshow, training-track |
| `tm_slide_1_12.ogg` | tm_slide_1_12Sound | VoiceOver | Music / slideshow narration | tm_slide_1_12 | music, narration, training-slideshow, training-track |
| `tm_slide_1_13.ogg` | tm_slide_1_13Sound | VoiceOver | Music / slideshow narration | tm_slide_1_13 | music, narration, training-slideshow, training-track |
| `tm_slide_1_14.ogg` | tm_slide_1_14Sound | VoiceOver | Music / slideshow narration | tm_slide_1_14 | music, narration, training-slideshow, training-track |
| `tm_slide_2_01.ogg` | tm_slide_2_01Sound | VoiceOver | Music / slideshow narration | tm_slide_2_01 | music, narration, training-slideshow, training-track |
| `tm_slide_2_post_01.ogg` | tm_slide_2_post_01Sound | VoiceOver | Music / slideshow narration | tm_slide_2_post_01 | music, narration, training-slideshow, training-track |
| `tm_slide_3_01.ogg` | tm_slide_3_01Sound | VoiceOver | Music / slideshow narration | tm_slide_3_01 | music, narration, training-slideshow, training-track |
| `tm_slide_3_02.ogg` | tm_slide_3_02Sound | VoiceOver | Music / slideshow narration | tm_slide_3_02 | music, narration, training-slideshow, training-track |
| `tm_slide_3_03.ogg` | tm_slide_3_03Sound | VoiceOver | Music / slideshow narration | tm_slide_3_03 | music, narration, training-slideshow, training-track |
| `tm_slide_3_04.ogg` | tm_slide_3_04Sound | VoiceOver | Music / slideshow narration | tm_slide_3_04 | music, narration, training-slideshow, training-track |
| `tm_slide_3_05.ogg` | tm_slide_3_05Sound | VoiceOver | Music / slideshow narration | tm_slide_3_05 | music, narration, training-slideshow, training-track |
| `tm_slide_3_06.ogg` | tm_slide_3_06Sound | VoiceOver | Music / slideshow narration | tm_slide_3_06 | music, narration, training-slideshow, training-track |
| `tm_slide_3_07.ogg` | tm_slide_3_07Sound | VoiceOver | Music / slideshow narration | tm_slide_3_07 | music, narration, training-slideshow, training-track |
| `tm_slide_3_08.ogg` | tm_slide_3_08Sound | VoiceOver | Music / slideshow narration | tm_slide_3_08 | music, narration, training-slideshow, training-track |
| `tm_slide_3_09.ogg` | tm_slide_3_09Sound | VoiceOver | Music / slideshow narration | tm_slide_3_09 | music, narration, training-slideshow, training-track |
| `tm_slide_3_10.ogg` | tm_slide_3_10Sound | VoiceOver | Music / slideshow narration | tm_slide_3_10 | music, narration, training-slideshow, training-track |
| `tm_slide_3_11.ogg` | tm_slide_3_11Sound | VoiceOver | Music / slideshow narration | tm_slide_3_11 | music, narration, training-slideshow, training-track |
| `tm_slide_3_12.ogg` | tm_slide_3_12Sound | VoiceOver | Music / slideshow narration | tm_slide_3_12 | music, narration, training-slideshow, training-track |
| `tm_slide_3_13.ogg` | tm_slide_3_13Sound | VoiceOver | Music / slideshow narration | tm_slide_3_13 | music, narration, training-slideshow, training-track |
| `tm_slide_3_post_01.ogg` | tm_slide_3_post_01Sound | VoiceOver | Music / slideshow narration | tm_slide_3_post_01 | music, narration, training-slideshow, training-track |
| `tm_slide_4_01.ogg` | tm_slide_4_01Sound | VoiceOver | Music / slideshow narration | tm_slide_4_01 | music, narration, training-slideshow, training-track |
| `tm_slide_4_02.ogg` | tm_slide_4_02Sound | VoiceOver | Music / slideshow narration | tm_slide_4_02 | music, narration, training-slideshow, training-track |
| `tm_slide_4_post_01.ogg` | tm_slide_4_post_01Sound | VoiceOver | Music / slideshow narration | tm_slide_4_post_01 | music, narration, training-slideshow, training-track |
| `tm_slide_5_01.ogg` | tm_slide_5_01Sound | VoiceOver | Music / slideshow narration | tm_slide_5_01 | music, narration, training-slideshow, training-track |
| `tm_slide_5_02.ogg` | tm_slide_5_02Sound | VoiceOver | Music / slideshow narration | tm_slide_5_02 | music, narration, training-slideshow, training-track |
| `tm_slide_5_03.ogg` | tm_slide_5_03Sound | VoiceOver | Music / slideshow narration | tm_slide_5_03 | music, narration, training-slideshow, training-track |
| `tm_slide_5_04.ogg` | tm_slide_5_04Sound | VoiceOver | Music / slideshow narration | tm_slide_5_04 | music, narration, training-slideshow, training-track |
| `tm_slide_5_05.ogg` | tm_slide_5_05Sound | VoiceOver | Music / slideshow narration | tm_slide_5_05 | music, narration, training-slideshow, training-track |
| `tm_slide_5_06.ogg` | tm_slide_5_06Sound | VoiceOver | Music / slideshow narration | tm_slide_5_06 | music, narration, training-slideshow, training-track |
| `tm_slide_5_07.ogg` | tm_slide_5_07Sound | VoiceOver | Music / slideshow narration | tm_slide_5_07 | music, narration, training-slideshow, training-track |
| `tm_slide_5_08.ogg` | tm_slide_5_08Sound | VoiceOver | Music / slideshow narration | tm_slide_5_08 | music, narration, training-slideshow, training-track |
| `tm_slide_5_09.ogg` | tm_slide_5_09Sound | VoiceOver | Music / slideshow narration | tm_slide_5_09 | music, narration, training-slideshow, training-track |
| `tm_slide_5_10.ogg` | tm_slide_5_10Sound | VoiceOver | Music / slideshow narration | tm_slide_5_10 | music, narration, training-slideshow, training-track |
| `tm_slide_5_11.ogg` | tm_slide_5_11Sound | VoiceOver | Music / slideshow narration | tm_slide_5_11 | music, narration, training-slideshow, training-track |
| `tm_slide_5_12.ogg` | tm_slide_5_12Sound | VoiceOver | Music / slideshow narration | tm_slide_5_12 | music, narration, training-slideshow, training-track |
| `tm_slide_5_13.ogg` | tm_slide_5_13Sound | VoiceOver | Music / slideshow narration | tm_slide_5_13 | music, narration, training-slideshow, training-track |
| `tm_slide_5_14.ogg` | tm_slide_5_14Sound | VoiceOver | Music / slideshow narration | tm_slide_5_14 | music, narration, training-slideshow, training-track |
| `tm_slide_5_15.ogg` | tm_slide_5_15Sound | VoiceOver | Music / slideshow narration | tm_slide_5_15 | music, narration, training-slideshow, training-track |
| `tm_slide_5_16.ogg` | tm_slide_5_16Sound | VoiceOver | Music / slideshow narration | tm_slide_5_16 | music, narration, training-slideshow, training-track |
| `tm_slide_5_17.ogg` | tm_slide_5_17Sound | VoiceOver | Music / slideshow narration | tm_slide_5_17 | music, narration, training-slideshow, training-track |
| `tm_slide_5_18.ogg` | tm_slide_5_18Sound | VoiceOver | Music / slideshow narration | tm_slide_5_18 | music, narration, training-slideshow, training-track |
| `tm_slide_5_19.ogg` | tm_slide_5_19Sound | VoiceOver | Music / slideshow narration | tm_slide_5_19 | music, narration, training-slideshow, training-track |
| `tm_slide_5_20.ogg` | tm_slide_5_20Sound | VoiceOver | Music / slideshow narration | tm_slide_5_20 | music, narration, training-slideshow, training-track |
| `tm_slide_5_21.ogg` | tm_slide_5_21Sound | VoiceOver | Music / slideshow narration | tm_slide_5_21 | music, narration, training-slideshow, training-track |
| `tm_slide_5_22.ogg` | tm_slide_5_22Sound | VoiceOver | Music / slideshow narration | tm_slide_5_22 | music, narration, training-slideshow, training-track |
| `tm_slide_5_23.ogg` | tm_slide_5_23Sound | VoiceOver | Music / slideshow narration | tm_slide_5_23 | music, narration, training-slideshow, training-track |
| `tm_slide_5_24.ogg` | tm_slide_5_24Sound | VoiceOver | Music / slideshow narration | tm_slide_5_24 | music, narration, training-slideshow, training-track |
| `tm_slide_5_25.ogg` | tm_slide_5_25Sound | VoiceOver | Music / slideshow narration | tm_slide_5_25 | music, narration, training-slideshow, training-track |
| `tm_slide_5_26.ogg` | tm_slide_5_26Sound | VoiceOver | Music / slideshow narration | tm_slide_5_26 | music, narration, training-slideshow, training-track |
| `tm_slide_5_post_01.ogg` | tm_slide_5_post_01Sound | VoiceOver | Music / slideshow narration | tm_slide_5_post_01 | music, narration, training-slideshow, training-track |
| `tm_slide_5_post_02.ogg` | tm_slide_5_post_02Sound | VoiceOver | Music / slideshow narration | tm_slide_5_post_02 | music, narration, training-slideshow, training-track |
| `tm_slide_5_post_03.ogg` | tm_slide_5_post_03Sound | VoiceOver | Music / slideshow narration | tm_slide_5_post_03 | music, narration, training-slideshow, training-track |
| `tm_slide_5_post_04.ogg` | tm_slide_5_post_04Sound | VoiceOver | Music / slideshow narration | tm_slide_5_post_04 | music, narration, training-slideshow, training-track |
| `tm_slide_6_01.ogg` | tm_slide_6_01Sound | VoiceOver | Music / slideshow narration | tm_slide_6_01 | music, narration, training-slideshow, training-track |
| `tm_slide_6_post_01.ogg` | tm_slide_6_post_01Sound | VoiceOver | Music / slideshow narration | tm_slide_6_post_01 | music, narration, training-slideshow, training-track |
| `tm_slide_8_01.ogg` | tm_slide_8_01Sound | VoiceOver | Music / slideshow narration | tm_slide_8_01 | music, narration, training-slideshow, training-track |
| `tm_slide_8_02.ogg` | tm_slide_8_02Sound | VoiceOver | Music / slideshow narration | tm_slide_8_02 | music, narration, training-slideshow, training-track |
| `tm_slide_8_03.ogg` | tm_slide_8_03Sound | VoiceOver | Music / slideshow narration | tm_slide_8_03 | music, narration, training-slideshow, training-track |
| `tm_slide_8_04.ogg` | tm_slide_8_04Sound | VoiceOver | Music / slideshow narration | tm_slide_8_04 | music, narration, training-slideshow, training-track |
| `tm_slide_8_05.ogg` | tm_slide_8_05Sound | VoiceOver | Music / slideshow narration | tm_slide_8_05 | music, narration, training-slideshow, training-track |
| `tm_slide_8_post_01.ogg` | tm_slide_8_post_01Sound | VoiceOver | Music / slideshow narration | tm_slide_8_post_01 | music, narration, training-slideshow, training-track |
| `tm_slide_8_post_02.ogg` | tm_slide_8_post_02Sound | VoiceOver | Music / slideshow narration | tm_slide_8_post_02 | music, narration, training-slideshow, training-track |
| `tm_slide_8_post_03.ogg` | tm_slide_8_post_03Sound | VoiceOver | Music / slideshow narration | tm_slide_8_post_03 | music, narration, training-slideshow, training-track |
| `tm_slide_8_post_04.ogg` | tm_slide_8_post_04Sound | VoiceOver | Music / slideshow narration | tm_slide_8_post_04 | music, narration, training-slideshow, training-track |
| `tm_slide_8_post_05.ogg` | tm_slide_8_post_05Sound | VoiceOver | Music / slideshow narration | tm_slide_8_post_05 | music, narration, training-slideshow, training-track |

## Unused / orphaned assets

46 files exist under `pick-assets/` but are **not** referenced by any `ImportWave(...)` in the sound-definition scripts — legacy, replaced, or reserved assets. Filenames still indicate intended purpose.

| File | Likely purpose (from name) | Tags |
|---|---|---|
| `acceptcom.ogg` | acceptcom | — |
| `alephenter.ogg` | alephenter | — |
| `announce2.ogg` | announce2 | announcement |
| `announce3.ogg` | announce3 | announcement |
| `colonialambient_10.ogg` | colonialambient_10 | ambient, colonial, loop |
| `colonialambient_2.ogg` | colonialambient_2 | ambient, colonial, loop |
| `colonialambient_3.ogg` | colonialambient_3 | ambient, colonial, loop |
| `colonialambient_4.ogg` | colonialambient_4 | ambient, colonial, loop |
| `colonialambient_5.ogg` | colonialambient_5 | ambient, colonial, loop |
| `colonialambient_6.ogg` | colonialambient_6 | ambient, colonial, loop |
| `colonialambient_7.ogg` | colonialambient_7 | ambient, colonial, loop |
| `colonialambient_8.ogg` | colonialambient_8 | ambient, colonial, loop |
| `colonialambient_9.ogg` | colonialambient_9 | ambient, colonial, loop |
| `countdown0.ogg` | countdown0 | countdown, timer |
| `ef1000.ogg` | ef1000 | effect |
| `ef1001.ogg` | ef1001 | effect |
| `ef1002.ogg` | ef1002 | effect |
| `ef1003.ogg` | ef1003 | effect |
| `ef1004.ogg` | ef1004 | effect |
| `EF1005.ogg` | ef1005 | effect |
| `ef1006.ogg` | ef1006 | effect |
| `ef125.ogg` | ef125 | effect |
| `ef29.ogg` | ef29 | effect |
| `ef77.ogg` | ef77 | effect |
| `ioncannon.ogg` | ioncannon | cannon, weapon |
| `outofmissiles.ogg` | outofmissiles | — |
| `ripcord.ogg` | ripcord | — |
| `ripcordon.ogg` | ripcordon | — |
| `stationinvest_mouseover.ogg` | stationinvest_mouseover | — |
| `stationloadout_mouseover.ogg` | stationloadout_mouseover | — |
| `stationteam_mouseover.ogg` | stationteam_mouseover | — |
| `tm_2_39.ogg` | tm_2_39 | music, training-track |
| `tm_2_40.ogg` | tm_2_40 | music, training-track |
| `tm_2_41.ogg` | tm_2_41 | music, training-track |
| `tm_slide_1_15.ogg` | tm_slide_1_15 | music, narration, training-slideshow, training-track |
| `tm_slide_1_16.ogg` | tm_slide_1_16 | music, narration, training-slideshow, training-track |
| `tm_slide_1_17.ogg` | tm_slide_1_17 | music, narration, training-slideshow, training-track |
| `tm_slide_1_18.ogg` | tm_slide_1_18 | music, narration, training-slideshow, training-track |
| `tm_slide_4_03.ogg` | tm_slide_4_03 | music, narration, training-slideshow, training-track |
| `vo_builder_completeconst.ogg` | vo_builder_completeconst | builder-vo |
| `vo_layer_defendconstructor.ogg` | vo_layer_defendconstructor | — |
| `vo_miner_cominghomeempty.ogg` | vo_miner_cominghomeempty | comms, miner-vo |
| `vo_miner_crowded.ogg` | vo_miner_crowded | comms, miner-vo |
| `vo_player_misc19.ogg` | vo_player_misc19 | callout, comms, player-vo |
| `vo_sal_quit.ogg` | vo_sal_quit | announcement, commander-vo, sal |
| `WEPIN.ogg` | wepin | — |

## Appendix — training-mission music scripts

Each tutorial mission has a `tm_N_*.mdl` slideshow script (and a `_post` variant) that sequences slide images against `tm_slide_*SoundId` narration/music cues:

- `tm_1_introduction.mdl`
- `tm_2_basic_flight.mdl`
- `tm_2_basic_flight_post.mdl`
- `tm_3_basic_weaponry.mdl`
- `tm_3_basic_weaponry_post.mdl`
- `tm_4_enemy_engagement.mdl`
- `tm_4_enemy_engagement_post.mdl`
- `tm_5_command_view.mdl`
- `tm_5_command_view_post.mdl`
- `tm_6_practice_arena.mdl`
- `tm_6_practice_arena_post.mdl`
- `tm_8_nanite.mdl`
- `tm_8_nanite_post.mdl`

## Appendix — broken / legacy script references

39 names are `ImportWave`'d in the scripts but have **no matching `.ogg`** on disk (some resolve to `.wav`, some are removed easter-egg voice lines, some are case/number typos like `lizard1` vs `lizard01.ogg`):

`bddsilent15`, `faohaspg`, `faohmu1`, `faohmu2`, `faohmu3`, `faohpsi1`, `faohpsi2`, `faohpsi3`, `faohtheta1`, `faohtheta2`, `faohtheta3`, `lizard1`, `lizard2`, `lizard3`, `lizard4`, `lizard5`, `lizard6`, `lizard7`, `lizard8`, `lizard9`, `tm_slide_8_06`, `tm_slide_8_post_06`, `vo_ma_nanitesclose`, `vo_ma_woopie`, `vo_player_gohanhello`, `vo_player_gokuspace`, `vo_player_krillinpossible`, `vo_player_piccolotrash`, `vo_player_piccoloweak`, `vo_player_piccolowuss`, `vo_player_raditzbirdie`, `vo_player_raditzcookmeat`, `vo_player_raditzlisten`, `vo_player_raditzreadyornot`, `vo_player_raditzteach`, `vo_player_soloattack`, `vo_player_vaderdestroy`, `vo_player_vegetalaugh`, `vo_player_vegetaoppose`
