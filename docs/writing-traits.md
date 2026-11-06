# Writing entad traits in XML

This covers the trait shapes most add-ons need: stat traits, building multipliers, weapon damage, equipped hediffs, triggers and the abilities they cast, abilities, auras, moods, visuals, drawbacks and exclusivity. Everything here is XML. The less common shapes (persona weapons, portable space, power network effects, effects and abilities from compat modules) are listed at the end with the files that show them.

`docs/entad-traits.csv` lists every built-in trait with its rarity range, points, numbers, what reveals it and what it never shares an item with. The XML behind any of them is the best example of its shape.

## Setup

A trait is an `EntadFramework.EntadTraitDef`. Put your traits in any file under `Defs/`.

Your mod needs Entad Framework loaded first. For a hard dependency, add `SaintWacko.EntadFramework` to `<modDependencies>` and `<loadAfter>` in your About.xml. For a soft one, put the trait files in a folder that only loads when the framework is active:

```xml
<!-- LoadFolders.xml -->
<loadFolders>
  <v1.6>
    <li>/</li>
    <li IfModActive="SaintWacko.EntadFramework">Mods/Entad</li>
  </v1.6>
</loadFolders>
```

and keep `<loadAfter>` so the framework's defs are there when yours load.

Once a trait has shipped, keep its `defName`. Saved items refer to their traits by defName, and the player's list of disabled traits (the Traits tab in the mod settings) does too.

## A minimal trait

```xml
<Defs>
  <EntadFramework.EntadTraitDef>
    <defName>MyMod_Featherstep</defName>
    <label>Featherstep</label>
    <description>Its bearer barely leaves footprints.</description>
    <categories><li>Apparel</li></categories>
    <rarity>Uncommon</rarity>
    <scalesWithRarity>true</scalesWithRarity>
    <statOffsets>
      <li><stat>MoveSpeed</stat><min>0.2</min><max>0.5</max></li>
    </statOffsets>
  </EntadFramework.EntadTraitDef>
</Defs>
```

The framework adds its comp to every weapon, apparel and furniture def on its own, so there is nothing to patch onto items. The info card shows what the trait does from its effects, so `description` is flavour text: write what it feels like, not the numbers.

## Common fields

| Field | Default | What it does |
|---|---|---|
| `label` | | The trait's name on the card. The built-in traits use title case ("Long Sight"). |
| `description` | | Flavour text shown under the name. |
| `descriptionMelee`, `descriptionRanged` | unset | Replace `description` on a melee or a ranged weapon, for text that only reads right on one ("its shots leave burns"). |
| `categories` | any | `Weapon`, `WeaponMelee`, `WeaponRanged`, `Apparel`, `Furniture`, one or more. Empty means any kind of item. `Weapon` is any weapon; `WeaponMelee` or `WeaponRanged` limits a trait to that kind, which is needed when its stats are pawn stats that work through any held item (aiming time on a sword does nothing). |
| `rarity` | `Common` | `Common`, `Uncommon`, `Rare`, `Epic`, `Legendary`. Shown on the card, and sets the default cost. |
| `scalesWithRarity` | false | The trait can also roll at higher rarities, with its values scaled up (see below). |
| `points` | from rarity | Generation cost at the trait's own rarity. Unset uses the settings table: Common 1, Uncommon 2, Rare 3, Epic 5, Legendary 8. Negative makes a drawback. 0 is an error. |
| `bodyPartGroups` | any | Apparel only: the garment must cover at least one of these `BodyPartGroupDef`s. For pawn stats tied to a body part, so a hat doesn't make you run faster. The built-in traits use `Legs`+`Feet`, `Arms`+`Hands`, `UpperHead`+`FullHead`+`Eyes` and `Torso`+`Waist`. Weapons ignore it. Needs `Apparel` in categories (or no categories). Make sure vanilla apparel covers the groups you pick: nothing in Core or the DLCs covers only `Feet` or `Hands`. |
| `partialStats` | false | Lets the trait roll where only some of its stats mean anything (see Stats). |
| `bindOnFirstUse` | false | The item binds to the first pawn who equips or uses it; from then on its traits only work for that pawn and their descendants. |
| `neverRandom` | false | Random generation never picks it. It can still be added by name (API, dev tools). |
| `visible` | false | The trait gives itself away at a glance (a burning blade, a lit room): it starts revealed and stays revealed, whatever the hide-traits setting, Secretive or a trader's partial identification do to the item's other traits. Can't be combined with `secretive`. |
| `visuals` | | How the trait shows on the item (see Visuals). |

### Points and rarity

Generation gives each item a point target and adds traits until the item reaches it. A trait's `points` is what it costs against that target; set it when the trait is clearly stronger or weaker than others of its rarity (Swiftness is Uncommon with `points` 3 because move speed helps in every fight and every job). Points are never shown to the player.

With `scalesWithRarity`, `rarity` is the lowest rarity the trait appears at. Rolled higher, its values are scaled by the rarity multipliers in the settings (by default 1, 1.25, 1.5, 2, 3 from Common to Legendary), relative to its own rarity, and its cost follows the points table the same way. Offsets are multiplied; factors move further from 1 in the direction they already point (x0.8 becomes x0.6 at double scale). Trigger amounts (healing, stun, damage and the rest) scale like offsets; a trigger's chance and cooldown only as its `rarityScales` says (see Triggers). An aura's radius grows with the square root of the multiplier. A rarity is skipped once a factor would fall below 0.05, an offset would pass its stat's `maxValue`, a need restore would pass 100% or a scaled trigger chance would, so a strong trait may top out before Legendary. Without `scalesWithRarity`, the trait only rolls at its own rarity.

## Stats

```xml
<statOffsets>
  <li><stat>AccuracyMedium</stat><min>0.03</min><max>0.08</max></li>
</statOffsets>
<statFactors>
  <li><stat>MeleeWeapon_CooldownMultiplier</stat><min>0.75</min><max>0.9</max></li>
</statFactors>
```

Each value is rolled once per item between `min` and `max`. A trait can list several stats in either list.

Where a stat ends up depends on the stat:

- Item stats (accuracy, `RangedWeapon_*`, `MeleeWeapon_*`, mass, hit points, deterioration, apparel armor and insulation, a bench's work speed, a bed's comfort) change the item itself.
- Pawn stats (any stat in the pawn categories: move speed, work speed, shooting accuracy, healing rate, psychic sensitivity and so on) go on weapons and apparel as equipped offsets, so they follow whoever wields or wears the item and show in that pawn's stat breakdown. Use `statOffsets` for these. A `statFactors` entry on a pawn stat only rolls on items that list the stat in their own `statBases`, which in practice means weapon multipliers.
- Apparel made from stuff counts as listing the armor and insulation stats it gets from its stuff (it lists `StuffEffectMultiplierArmor` and the like instead), so an armor or insulation trait rolls on a parka or a duster.

A trait only rolls on an item where every one of its stats means something. The framework checks, in order: melee-only stats stay off guns and ranged-only stats off melee weapons; the stat is in the item's `statBases`; it is a pawn stat and the item is a weapon or apparel; it is a `RangedWeapon_`/`MeleeWeapon_` stat on that kind of weapon; vanilla would show the stat for that item. A bed's Comfort or a bench's `WorkTableWorkSpeedFactor` passes because those defs list them. If a stat fails, the trait doesn't roll there. With `partialStats`, the trait rolls when at least one stat applies, and the card hides the ones that don't (Piercing uses this to carry one armor penetration stat for each kind of weapon).

Two traits that touch the same stat never share an item. Set `allowOverlap` to true on either to allow it.

The framework's own stats in `Defs/EntadStats.xml` work in XML like any other, and each already has its rule for which items it rolls on: `Entad_GlowRadius` (light from furniture without a lamp, or from apparel, where the light follows the wearer; the trait's `glowColor` sets the light's colour, a lamp's by default), `Entad_DeflectChance` (apparel: the chance a shot hitting the wearer glances off, some back at the shooter; capped at 60%), `Entad_RoomHeating` and `Entad_RoomCooling` (pair with `targetTemperature`), `Entad_StoredRotFactor`, `Entad_FertilityOffset`, `Entad_PlantGrowthFactor`, `Entad_FilthPerDay` (pair with `filth`), `Entad_ProductQualityChance`, `Entad_ProductDoubleChance`, `Entad_IngredientRefundChance`, `Entad_ColonyWealthFactor`, `Entad_MercilessDamageFactor`, `Entad_MeleeArmorPenetrationMultiplier`, `Entad_FilthPickupFactor` and the three power network stats. `Defs/EntadTraits_Furniture.xml`, `EntadTraits_Drawbacks.xml` and `EntadTraits_Apparel.xml` show them in use.

## Building properties

For furniture values that live in comps rather than stats:

```xml
<buildingFactors>
  <li><property>StorageCapacity</property><min>1.5</min><max>2</max></li>
</buildingFactors>
```

| Property | Rolls on |
|---|---|
| `LightRadius` | buildings with a glower |
| `HeatOutput` | buildings with a heat pusher |
| `FuelConsumptionRate`, `FuelCapacity` | refuelable buildings |
| `PowerGeneration` | power plants |
| `PowerConsumption` | powered consumers |
| `TemperatureControlPower` | heaters and coolers |
| `StorageCapacity` | shelf-like storage holding 2+ stacks per cell |

All are factors, scaled like stat factors.

Fuel: `fuelTypes` (ThingDefs) and `fuelCategories` (ThingCategoryDefs) let a refuelable building burn more things; `replaceFuel` makes them its only fuel.

## Weapon effects

```xml
<changeDamageType>Burn</changeDamageType>
<extraDamage>
  <li><damageType>Stun</damageType><min>2</min><max>6</max></li>
</extraDamage>
<weaponProperties>
  <li><property>BurstShotCount</property><min>1.25</min><max>1.5</max></li>
</weaponProperties>
<ignoreAccuracyMaluses>true</ignoreAccuracyMaluses>
```

- `changeDamageType` replaces the damage type of the weapon's attacks, melee and ranged.
- `extraDamage` adds damage of another type to each hit, scaled by rarity.
- `weaponProperties`: `BurstShotCount` and `BurstShotSpeed` are factors and only roll on weapons that already fire bursts; `StoppingPower` is an offset and rolls on anything with a projectile. Each caps at 3.
- `ignoreAccuracyMaluses` (ranged only) ignores weather and smoke penalties.

Damage effects only roll on weapons whose damage the framework can reach: melee, bullets, explosive projectiles and beams with a damage def. Smoke and EMP launchers are left out.

`killThought` gives the wielder a memory on each kill (with `killVictims`: `Any`, `Hostile`, `Humanlike`, `Fight`, `HostileMechanoid`). It is shorthand for a Kill trigger, below.

## Equipped hediffs

```xml
<categories><li>Weapon</li><li>Apparel</li></categories>
<equippedHediffs><li>MyMod_Hediff_Clotting</li></equippedHediffs>
```

The hediff is added while the item is equipped and removed when it comes off, unless other active gear gives it too. The card describes it from the hediff's first stage (stat offsets, factors, pain, bleeding) and from any `ThoughtWorker_Hediff` mood tied to it, so put the effect in `stages`. The framework's `EntadHediffBase` (`HediffWithComps`, not bad, not curable) is a convenient parent.

## Triggers

"When X happens, do Y."

```xml
<triggers>
  <li>
    <on>Downed</on>
    <heal>30~40</heal>
    <cooldownHours>24</cooldownHours>
  </li>
</triggers>
```

Events (`on`):

| Event | Fires when | Filters |
|---|---|---|
| `Kill` | the holder kills something, hunting included | `victims`, `minDistance` |
| `Hit` | the holder's damage gets through to a pawn (a weapon counts its own hits, apparel any) | `victims`, `attack`, `finishing`, `minDistance` |
| `TakeDamage` | the holder takes damage that gets through | `attack`, `fromEnemy`, `healthBelow`, `minDistance`, `includeBlocked` |
| `Downed` | the holder is downed | |
| `WakeUp` | a sleep of an hour or more ends | |
| `Eat` | the holder finishes eating something nourishing | |
| `BillDone` | one iteration of a production bill finishes | |
| `JobDone` | a job in `jobs` finishes | `jobs` (required) |
| `Use` | furniture: someone used it | `use` |

Filters: `victims` is `Any`, `Hostile`, `Humanlike`, `Fight` (hostile plus wild animals), `HostileMechanoid`. `attack` is `Any`, `Melee`, `Ranged` (melee means dealt in a melee attack, so a rifle butt counts). `fromEnemy` true needs a hostile attacker. `healthBelow` 0.5 means under 50% health after the hit. `finishing` true means the hit downed or killed a standing pawn. `includeBlocked` true also counts a blow armor stopped entirely (not one a shield belt absorbed), for retaliation that shouldn't depend on the armor failing. `use` is one or more of `Sleep`, `Eat`, `Work`, `Recreation`, `Other`, comma separated.

Effects (ranges are written `min~max`, rolled per item and scaled by rarity):

| Effect | |
|---|---|
| `heal` | hit points healed, worst injuries first |
| `rest`, `food`, `joy` | fraction of the need restored (0.3 = 30%, at most 1) |
| `hediff`, `hediffHours` | adds the hediff; `hediffHours` sets its duration and needs a hediff with `HediffComp_Disappears` |
| `thought` | a plain memory thought |
| `stunSeconds` | stuns the target |
| `inspiration` or `randomInspiration` | starts that inspiration, or a random one the pawn can have |
| `damageDef`, `damage`, `armorPenetration` | damage dealt by the holder, after the other effects; needs `toOther` |
| `mechanoidDamageDef`, `mechanoidDamage` | more damage, to mechanoids only (an EMP that stuns them); needs `toOther` |
| `igniteChance` | chance to set the target alight; needs `toOther` |

`toOther` (TakeDamage and Hit) sends the effects to the other pawn instead of the holder: the attacker on `TakeDamage`, the pawn hit on `Hit`. Damage also reaches an attacker that isn't a pawn, such as a turret. It can't go on a trigger with a `radius`. A trigger's own damage sets off no triggers, so two thorned pawns don't answer each other for ever.

```xml
<li>
  <on>TakeDamage</on>
  <attack>Melee</attack>
  <fromEnemy>true</fromEnemy>
  <includeBlocked>true</includeBlocked>
  <toOther>true</toOther>
  <damageDef>Cut</damageDef>
  <damage>3~6</damage>
  <armorPenetration>0.15</armorPenetration>
</li>
```

Area: with `radius` above 0, every effect goes to each pawn `targets` picks (`Self`, `Allies`, `Enemies`, comma separated) within that many tiles of the holder (the furniture for `Use`; the victim's cell with `atVictim`, Kill only). `sameRoom` limits it to the centre's room. `fleck` and `sound` play at the centre.

```xml
<li>
  <on>Kill</on>
  <victims>Hostile</victims>
  <chance>0.2</chance>
  <stunSeconds>2~3</stunSeconds>
  <radius>4</radius>
  <atVictim>true</atVictim>
  <targets>Enemies</targets>
  <fleck>PsycastAreaEffect</fleck>
  <sound>Thunder_OnMap</sound>
</li>
```

`Use` triggers are for furniture and every other event is for weapons and apparel, so one trait can't mix the two. A `Use` trigger only rolls on furniture that can be used that way (a `Sleep` trigger needs a bed).

### Chance, cooldown and strength

`chance` (default 1) and the cooldown, `cooldownHours` or `cooldownSeconds` (one of the two; per item, shared by everyone who uses it), are ranges like the effects (`0.06~0.08`, `30~45`), rolled per item. A better roll means a higher chance or a shorter cooldown, and the item's price follows.

With `scalesWithRarity`, `rarityScales` decides what rarity does to them: `None` (the default) leaves both alone, `Chance` multiplies the chance by the rarity multiplier (for offensive procs, which their chance keeps in check; needs a chance below 1), `Cooldown` divides the cooldown by it (for defensive procs and lifesavers, kept in check by their cooldown; needs a cooldown). The effects scale either way. A `rarityScales` on a trait without `scalesWithRarity` is an error.

The player's settings have a multiplier on the chance and one on the cooldown of every combat trigger (`Hit`, `TakeDamage`, `Kill`, `Downed`), both 100% by default and read as the trigger rolls, so they change existing items too. The card shows the chance and cooldown with them applied.

Three fields keep a combat proc from being better on one kind of weapon than another:

- `rateNormalized` (Hit only, 0 to 1): the chance is written for a weapon that attacks once a second (ranged) or every two seconds (melee), and multiplied by this weapon's time per attack raised to this power. At 1 a minigun and a sniper rifle set it off about as often per minute of fighting: use it for all-or-nothing effects (a stun, a disarm) that a fast weapon would otherwise land over and over.
- `scaleWithDamage` (Hit and Kill, 0 to 1): the effect's strength (its stun and damage, and the power handed to its casts) is multiplied by the weapon's damage per attack against a reference (11 for ranged, an assault rifle's bullet; 18 for melee, a longsword), clamped to 0.5 to 2.5, raised to this power. Damage procs use 0.5 of both, so the proc's damage per second follows the square root of the weapon's. Both need a weapon in `categories`, and both read the item, not the wielder's skill or aim.
- `adaptSeconds` (Hit and TakeDamage): after the trigger goes off at a pawn, that pawn can't set it off again for this long. With `adaptFactor` (above 0, below 1), each repeat within the window is that much as strong as the one before instead (stun, damage and cast durations), until it would fall under 25%; each repeat restarts the window. A stun on every blow would otherwise hold a melee attacker in place for good. Adaptation isn't saved.

The card shows the chance and strength for the weapon the trait is on.

## Abilities cast by triggers

```xml
<triggers>
  <li>
    <on>Hit</on>
    <attack>Ranged</attack>
    <victims>Hostile</victims>
    <chance>0.22~0.3</chance>
    <rarityScales>Chance</rarityScales>
    <rateNormalized>0.5</rateNormalized>
    <scaleWithDamage>0.5</scaleWithDamage>
    <casts>
      <li><ability>Entad_Crackle</ability><target>Victim</target></li>
    </casts>
  </li>
</triggers>
```

Each cast is an AbilityDef (`ability`) or one a compat module registered by name (`externalAbility`, such as a Vanilla Psycasts Expanded psycast). It goes off at once, with no warmup, job, psyfocus or heat, ignoring the ability's range and line of sight; everything else the ability checks still counts. When a cast can't go off (a psychically deaf target, nowhere to skip to), the whole trigger is skipped and keeps its cooldown, unless the cast is `optional`.

`target`: `Self`, `Attacker` (TakeDamage), `Victim` and `VictimCell` (Hit, Kill), `Between` (halfway to the other pawn), `Origin` (where the holder stood), `NearestEnemy` (the nearest standing enemy the holder can see; on Hit and Kill, not the victim). `unroofed` true needs open sky over the target, for a strike from above. `fromTarget` true starts a projectile ability at the target instead of the holder (an `externalAbility` aimed at a pawn; only modules that launch projectiles honour it).

A `Hit` or `TakeDamage` trigger with casts needs a chance below 1 or a cooldown, or every bullet of a minigun casts. Casts can't go on a trigger with a `radius`: the ability has its own area. A trigger's `scaleWithDamage` and adaptation reach the cast as its power, which the framework's damage comps multiply their damage by, and vanilla's duration comps (stun, burden) their duration.

The framework's own damage comps, for an AbilityDef made to be cast by a trigger (`Defs/EntadProcAbilities.xml`):

- `EntadFramework.CompProperties_AbilityEntadStrike`: damage on the target alone, or with `radius` above 0 a blast around it. `spareNonHostile` keeps the blast off everything not hostile to the caster (fire it starts still spares nobody); `explosionSound` replaces the damage def's own.
- `EntadFramework.CompProperties_AbilityEntadChain`: strikes the target, then jumps up to `jumps` times to the nearest enemy within `jumpRange` of the last one, in sight of it, skipping downed pawns. `jumpVisuals` and `jumpSound` play on each jump (Holder is where the last target stood, Other the next).

Both take `damageDef`, `damageAmount` (the def's default when unset), `armorPenetration`, and for a strike without a radius `igniteChance` and `mechanoidDamageDef`/`mechanoidDamage`. Their `visuals` list takes event visuals (see Visuals) with `at` Other for the target or blast centre and Holder for the caster; they are the ability's own look and play whatever the visuals settings say.

## Abilities

```xml
<abilities><li>Painblock</li></abilities>
<abilityCharges>2</abilityCharges>
<abilityCooldownTicks>60000</abilityCooldownTicks>
<abilityCastTicks>60</abilityCastTicks>
```

Weapons and apparel grant the abilities to whoever holds them. Furniture offers them as right-click actions that a pawn walks over and performs. `abilityCooldownTicks` replaces the ability's own cooldown (60000 ticks is a day). With `abilityCharges`, the ability can be used that many times in a row and gets one charge back per cooldown. `abilityCastTicks` is the warmup on gear, or how long the pawn works at the furniture.

For ammo instead of a cooldown, set `abilityAmmo` (a ThingDef), `abilityAmmoPerCharge` (default 1) and `abilityReloadTicks` (default 60) together with `abilityCharges`; the pawn reloads from the ammo like vanilla reloadable gear. Weapons and apparel only, and no cooldown in this mode.

Any AbilityDef works: vanilla, a DLC's or your own. `Defs/EntadAbilities.xml` has no-DLC examples with the framework's pulse and blink comps.

## Auras

```xml
<aura>
  <radius>6.9</radius>
  <hediff>MyMod_Hediff_Rallied</hediff>
  <affectsAllies>true</affectsAllies>
  <onlyInCombat>true</onlyInCombat>
  <ringColor>(1.0, 0.8, 0.35, 1)</ringColor>
</aura>
```

Every second (`intervalTicks` 60) the holder of the gear, or the furniture, gives the hediff to pawns in range and in sight, and refreshes it on those who have it. Make the hediff a child of `EntadAuraHediffBase`, which fades it 400 ticks after the last refresh. A hediff whose Disappears time is no longer than `intervalTicks` + 15 is an error.

Who: `affectsSelf` (gear only), `affectsAllies`, `affectsNeutrals`, `affectsEnemies`; `affectsHumanlikes` and `affectsAnimals` (both on by default), `affectsMechanoids`, `affectsEntities`. When, for gear: `whileDowned` (off), `whileAsleep` (on), `onlyInCombat`. `blockedByWalls` is on by default. With `severityPerDay` above 0 the hediff builds up instead, like toxic fallout, reduced by each of `resistStats`. A scaling aura's radius grows with the square root of the rarity multiplier. Furniture auras need power when the building uses it, and can't use `onlyInCombat` or `affectsSelf`. An item carries one aura at most.

## Visuals

```xml
<visuals>
  <li Class="EntadFramework.EntadVisual_WeaponGlow">
    <color>(1, 0.45, 0.12, 0.25)</color>
    <flicker>0.25</flicker>
  </li>
  <li Class="EntadFramework.EntadVisual_WeaponFlecks"><fleck>Entad_Fleck_Ember</fleck><perSecond>3</perSecond></li>
  <li Class="EntadFramework.EntadVisual_EventFlecks"><on>Hit</on><at>Other</at><fleck>Entad_Fleck_BurnFlash</fleck></li>
</visuals>
```

A trait's `visuals` say how it shows on the item. Each entry is one class, set with `Class`, and every field has a default, so most entries need only a fleck or a colour. The framework's flecks are in `Defs/EntadVisuals.xml`; any FleckDef works. The look needs no art of its own: glows, sheens and flows are made from the item's own texture.

By default a visual plays before its trait is identified too, as a hint that something is there (the player can turn that off). `beforeIdentified` on an entry overrides the setting: true plays while the trait is hidden whatever the setting says, false only once it is fully revealed. A visual never reveals anything by itself. For a trait whose look gives it away, set `visible` on the trait instead.

Item visuals, drawn on the item's own shape wherever vanilla draws it: in a pawn's hands while drafted or aiming, worn on a pawn (apparel), lying on the map. The `Weapon` in their names is historical; they work on apparel too.

| Class | |
|---|---|
| `EntadVisual_WeaponGlow` | a halo around the silhouette, pulsing and flickering; `tint` also washes the colour over the item itself, for pale cloth |
| `EntadVisual_WeaponFlow` | soft patches of colour crawling up the item: flames, static, frost. `additive` false for colours that must show on white |
| `EntadVisual_WeaponSheen` | a bright band sweeping across it every few seconds |
| `EntadVisual_WeaponGlints` | glints twinkling on spots of it |
| `EntadVisual_WeaponFlames` | small fire sprites on it (vanilla's fire frames, or `texFolder`) |
| `EntadVisual_WeaponFlecks` | flecks thrown off it: embers, sparks, mist |
| `EntadVisual_WeaponArcs` | static arcs jumping between spots on it |

Worn visuals (apparel only), drawn over the wearer's body and following it through walking, facing and lying down: `EntadVisual_WornFlecks`, `EntadVisual_WornFlames`, `EntadVisual_WornArcs`. `x` and `z` set where on the body they play (the defaults cover a humanlike's torso and legs); `onlyDrafted` limits them to a wearer who is drafted or otherwise showing its weapon. A hat that is hidden takes its visuals with it.

Shots: `EntadVisual_ProjectileTrail` leaves flecks behind every shot the weapon fires (`perCell` per tile of flight), and `EntadVisual_ProjectileArcs` throws little zaps off the shot as it flies. Beams and other instant shots get none.

Furniture, on an installed building: `EntadVisual_BuildingFlecks`, `EntadVisual_BuildingArcs`, `EntadVisual_BuildingSwirl` (a slow turning sprite per cell, a portal in each shelf slot). `when` limits them to `Always`, `Powered`, `NetLive`, `NetHasSource`, `FlareHeld`, `HoldsRottable`, `Growing`, `HoldsItems`, or `Custom` with a `custom` key a compat module registered. Each is worked out at most every 250 ticks, and only for buildings in view.

Event visuals play once when something happens. `on`: `Hit` (the item's weapon damage lands), `Struck` (the holder takes damage that gets through), `Heal` (one of the holder's injuries heals), `Dodge` (the holder dodges a melee blow), `Deflect` (worn apparel turned a shot aside), `Trigger` (one of the trait's triggers goes off; `trigger` picks one by its index in the list). `at`: `Holder`, `Other` (the thing hit, the attacker, the pawn killed), and on `Trigger` only `Affected` (each pawn the trigger's effects reached) and `Source` (the furniture of a `Use` trigger). `chance`, `minIntervalTicks` (keep it above 0 for a minigun or a heal) and `whileBleeding` limit them.

| Class | |
|---|---|
| `EntadVisual_EventFlecks` | a burst of flecks at the spot |
| `EntadVisual_EventEffecter` | a vanilla effecter, aimed at `toward`; instant effecters only |
| `EntadVisual_EventArcs` | zaps around the spot, or with `to` a jagged bolt to each `to` spot |
| `EntadVisual_EventStream` | flecks flowing `from` one spot `to` another (life drunk from a corpse) |
| `EntadVisual_EventRibbons` | glowing strands writhing along the line between two spots |
| `EntadVisual_EventAfterimage` | faded copies of the pawn sliding off it |

The player can turn visuals off as a whole or by kind (gear, shots, events, furniture). Load errors name the trait for a visual that can't play: a worn visual on a trait without `Apparel`, a building visual without `Furniture`, a trigger index past the end of the list, a fleck missing. Everything is drawn only for things in view and spawns flecks on game ticks, so nothing piles up while paused; a game with no trait that uses a kind pays one field read for it.

## Moods and meals

- `thought` (a memory) is given while the item is equipped, or after a pawn uses the furniture. `thoughtHours` is its lifetime in hours, rolled per item. `thoughtMoodRange` instead picks a random standalone memory whose mood lies in the range when the trait is applied.
- On eating surfaces: `mealNutritionFactor` (a range), `mealThought` (added to the meal's thoughts), `mealQualityOffset` (+1 moves a simple meal's thought to fine, and fine to lavish).

## Drawbacks

A trait with negative `points` is a drawback. Generation only gives one to some items (30% by default, at most one), and spends the points it gives back on more traits, so a drawback buys a stronger item.

```xml
<EntadFramework.EntadTraitDef>
  <defName>MyMod_Sweltering</defName>
  <label>Sweltering</label>
  <description>It gives off heat like a forge, and never cools down.</description>
  <categories><li>Furniture</li></categories>
  <points>-2</points>
  <exclusiveWith><li>Entad_Cool</li></exclusiveWith>
  <targetTemperature>40</targetTemperature>
  <statOffsets>
    <li><stat>Entad_RoomHeating</stat><min>10</min><max>15</max></li>
  </statOffsets>
</EntadFramework.EntadTraitDef>
```

The built-in drawbacks are all things the player can work around (a cooler, storing something that doesn't rot, keeping the item outside) rather than flat penalties, which tend to make an item one nobody uses. A drawback that shares a stat with a good trait already can't meet it; list the rest in `exclusiveWith`.

## Exclusivity

- `exclusiveWith`: trait defNames that can never share an item with this one. Listing it on either def is enough.
- `exclusivityTags`: strings; two traits sharing a tag never share an item.
- Implicit: two traits on the same stat, building property, ability, thought, meal effect, fuel or damage type change conflict unless one has `allowOverlap`. Two auras always conflict.

## Market value

By default each point adds a share of the item's base value plus flat silver (both in the settings), moved up to 25% either way by how well the values rolled. `marketValuePercent` (0.1 = 10% of base value) and `marketValueOffset` (silver) replace that price, for a trait that is cheap to roll but valuable to own, or the reverse.

## DLC and other mods

Put `MayRequire="Ludeon.RimWorld.Royalty"` (or the mod's packageId) on any trait that names a def from that DLC or mod, and on the defs that go with it. Without the DLC the trait isn't loaded and nothing else changes.

## Translation

`label`, `description`, `descriptionMelee` and `descriptionRanged` are injected like any def's, under `Languages/<Language>/DefInjected/EntadTraitDef/`.

## Testing

Load errors name the trait: a bad range, a filter on the wrong event, a field that needs another. Fix every one; most mean the trait would never roll or would do nothing.

In dev mode, the Entad Framework category of the debug actions has "Add specific Entad trait" (click an item) and "Add Entad trait to pawn's gear" (both list the traits that can roll there first, and a scaling trait asks for a rarity), "Generate Random Entad Item" (logs the point target and each trait's rarity and cost), and "Reveal all Entad traits" / "Hide all Entad traits". "Log Entad triggers (toggle)" logs every trigger that passes its filters and whether it fired (with its strength when `scaleWithDamage` or adaptation changed it), missed its roll, was on cooldown, met an adapted target or had a cast refused, and why a trigger visual that fired showed nothing.

## Stats that need code

The applies-to checks above decide where a stat can roll, and they only know vanilla's stats, the framework's own and stats an item lists in `statBases`. A stat another mod defines that its buildings or items don't list (or that its own code reads from somewhere other than the item) usually fails the last check, so a trait made of it never rolls, and there is nothing in XML to change that. The fix is a small compat assembly that calls `EntadCompat.RegisterStat(stat, def => ...)` at startup to say which ThingDefs the stat applies to; that answer is used before the `statBases` fallback, and returning false keeps a stat off items it would otherwise pass (the Combat Extended module does this for vanilla accuracy stats CE removes). The same goes for a stat that nothing reads: the framework puts the value on the item, but only code that asks for it makes it do anything. The Dubs Bad Hygiene module under `Mods/DubsBadHygiene/` is a worked example of both.

`EntadCompat` has a few more hooks for a module: `RegisterVisualCondition(key, thing => ...)` for building visuals with `when` Custom; the `Tick10` event, raised every 10 ticks in play, for checks that must catch something brief (keep the handler to a count check when it has nothing to watch); and the `SelectionOverlays` event, raised every frame while an entad with traits is selected, for drawing a module's radius (draw nothing for an effect the player hasn't identified). The Vanilla Temperature Expanded module under `Mods/VanillaTemperatureExpanded/` gives Warm, Cool, Sweltering and Frigid VTE's Proxy Heat around the building, reading the heat from `EntadFurnitureEffects.HeatOf` so the two agree.

## Not covered here

| Shape | Fields | Example file |
|---|---|---|
| Persona weapons | `persona`, `neverBond`, `secretive`, `bondedHediffs`, `bondedThought`, `otherWeaponThought`, `killThirstThought`, `killThirstDays` | `Defs/EntadTraits_Persona.xml` |
| Portable space | `portableSpace` | `Defs/EntadTraits_Space.xml` |
| Power network effects | `powerNet` | `Defs/EntadTraits_PowerNet.xml` |
| Effects and abilities from a compat assembly | `customEffects`, `customCardLines`, `externalAbilities`, trigger casts with `externalAbility` | `Mods/DubsBadHygiene/`, `Mods/VanillaPsycastsExpanded/` |
