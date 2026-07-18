# Pull a thin Allegiance tech stack (for Iron Coalition) and adopt it

- ☐ **[L]** **Allegiance Stub tech tree**  — Take a thin slice of the allegiance tech tree by implementing SOME of iron coalitions ships, bases, weapons, research etc
  - Ships: 
    - Starting: Scout, Lt Interceptor
    - Supremacy Center: Enh. Fighter and Adv. Fighter upgrades
    - Basic: Bomber (Must research before launching)
    - Shipyard: Devastator
  - Weapons: 
    - Gatling Gun (1, 2, and 3), (for scouts, fighters, and bombers)
    - Nanite (inverse damage)
    - Mini-Gun (for interceptors)
  - Bases:
    - Garrison -> Starbase
    - Outpost -> Heavy Outpost (each has to be upgraded?)
    - Supremacy Center -> Adv. Supremacy Center
    - Shipyard

The source of all of this information can be found in: /Users/erik/projects/Allegiance/artwork-full/PCore014.igc and read using the `igc-format` skill

I've also put together a very high level summary of the Iron Coalition faction's hulls, weapons, bases, etc. /Users/erik/projects/Allegiance/IronCoalition.md but it includes both too much information (too many things to import all at once, but also not enough information to fully understand the stats of each item).

The `pick-assets` folder can be use to hunt for meshes mentioned

Use teh `tech-tree-content` skill to understand how this application stores its tech tree

The mission for this task

**Ultimately we want to build a plan which adopts most of the stats/feel for how the Iron Coalition faction operates in Allegiance into Stellar Allegiance**

- Update any schema and code we need to update in order to incorporate things like faction-level stat multipliers (e.g. Station Max Armor: ×1.15)
- Update default bases/garrison/outposts/meshes
- Remove hardcoded assets/assumptions where what we've decided here should 'win'
- Defer, for now, new technologies that we cannot incorporate due to lack of, for example, teleporters (ripcord)

Create a phased approach for implementing the THIN tech tree from the igc into our yaml to import some ships, bases, weapoons, etc. Remove any default weapons with made up names and replace with the ones from this updated tech tree.