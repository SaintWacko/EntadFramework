# Writing entad traits in XML

This covers the trait shapes most add-ons need: stat traits, building multipliers, weapon damage, equipped hediffs, triggers, abilities, auras, moods, drawbacks and exclusivity. Everything here is XML. The less common shapes (persona weapons, portable space, power network effects, trigger casts, effects and abilities from compat modules) are listed at the end with the files that show them.

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

### Points and rarity

Generation gives each item a point target and adds traits until the item reaches it. A trait's `points` is what it costs against that target; set it when the trait is clearly stronger or weaker than others of its rarity (Swiftness is Uncommon with `points` 3 because move speed helps in every fight and every job). Points are never shown to the player.

With `scalesWithRarity`, `rarity` is the lowest rarity the trait appears at. Rolled higher, its values are scaled by the rarity multipliers in the settings (by default 1, 1.25, 1.5, 2, 3 from Common to Legendary), relative to its own rarity, and its cost follows the points table the same way. Offsets are multiplied; factors move further from 1 in the direction they already point (x0.8 becomes x0.6 at double scale). A rarity is skipped once a factor would fall below 0.05 or an offset would pass its stat's `maxValue`, so a strong trait may top out before Legendary. Without `scalesWithRarity`, the trait only rolls at its own rarity.

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

A trait only rolls on an item where every one of its stats means something. The framework checks, in order: melee-only stats stay off guns and ranged-only stats off melee weapons; the stat is in the item's `statBases`; it is a pawn stat and the item is a weapon or apparel; it is a `RangedWeapon_`/`MeleeWeapon_` stat on that kind of weapon; vanilla would show the stat for that item. A bed's Comfort or a bench's `WorkTableWorkSpeedFactor` passes because those defs list them. If a stat fails, the trait doesn't roll there. With `partialStats`, the trait rolls when at least one stat applies, and the card hides the ones that don't (Piercing uses this to carry one armor penetration stat for each kind of weapon).

Two traits that touch the same stat never share an item. Set `allowOverlap` to true on either to allow it.

The framework's own stats in `Defs/EntadStats.xml` work in XML like any other, and each already has its rule for which items it rolls on: `Entad_GlowRadius` (light from furniture without a lamp), `Entad_RoomHeating` and `Entad_RoomCooling` (pair with `targetTemperature`), `Entad_StoredRotFactor`, `Entad_FertilityOffset`, `Entad_PlantGrowthFactor`, `Entad_FilthPerDay` (pair with `filth`), `Entad_ProductQualityChance`, `Entad_ProductDoubleChance`, `Entad_IngredientRefundChance`, `Entad_ColonyWealthFactor`, `Entad_MercilessDamageFactor`, `Entad_MeleeArmorPenetrationMultiplier`, `Entad_FilthPickupFactor` and the three power network stats. `Defs/EntadTraits_Furniture.xml` and `EntadTraits_Drawbacks.xml` show them in use.

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
| `TakeDamage` | the holder takes damage that gets through | `attack`, `fromEnemy`, `healthBelow`, `minDistance` |
| `Downed` | the holder is downed | |
| `WakeUp` | a sleep of an hour or more ends | |
| `Eat` | the holder finishes eating something nourishing | |
| `BillDone` | one iteration of a production bill finishes | |
| `JobDone` | a job in `jobs` finishes | `jobs` (required) |
| `Use` | furniture: someone used it | `use` |

Filters: `victims` is `Any`, `Hostile`, `Humanlike`, `Fight` (hostile plus wild animals), `HostileMechanoid`. `attack` is `Any`, `Melee`, `Ranged` (melee means dealt in a melee attack, so a rifle butt counts). `fromEnemy` true needs a hostile attacker. `healthBelow` 0.5 means under 50% health after the hit. `finishing` true means the hit downed or killed a standing pawn. `use` is one or more of `Sleep`, `Eat`, `Work`, `Recreation`, `Other`, comma separated.

`chance` (default 1) and `cooldownHours` (per item, shared by everyone who uses it) don't scale with rarity.

Effects (ranges are written `min~max`, rolled per item and scaled by rarity):

| Effect | |
|---|---|
| `heal` | hit points healed, worst injuries first |
| `rest`, `food`, `joy` | fraction of the need restored (0.3 = 30%, at most 1) |
| `hediff`, `hediffHours` | adds the hediff; `hediffHours` sets its duration and needs a hediff with `HediffComp_Disappears` |
| `thought` | a plain memory thought |
| `stunSeconds` | stuns the target |
| `inspiration` or `randomInspiration` | starts that inspiration, or a random one the pawn can have |

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

In dev mode, the Entad Framework category of the debug actions has "Add specific Entad trait" (click an item), "Add Entad trait to pawn's gear", "Generate Random Entad Item" (logs the point target and each trait's rarity and cost), and "Reveal all Entad traits" / "Hide all Entad traits". "Log Entad triggers (toggle)" logs every trigger that passes its filters and whether it fired, missed its roll or was on cooldown.

## Stats that need code

The applies-to checks above decide where a stat can roll, and they only know vanilla's stats, the framework's own and stats an item lists in `statBases`. A stat another mod defines that its buildings or items don't list (or that its own code reads from somewhere other than the item) usually fails the last check, so a trait made of it never rolls, and there is nothing in XML to change that. The fix is a small compat assembly that calls `EntadCompat.RegisterStat(stat, def => ...)` at startup to say which ThingDefs the stat applies to; that answer is used before the `statBases` fallback, and returning false keeps a stat off items it would otherwise pass (the Combat Extended module does this for vanilla accuracy stats CE removes). The same goes for a stat that nothing reads: the framework puts the value on the item, but only code that asks for it makes it do anything. The Dubs Bad Hygiene module under `Mods/DubsBadHygiene/` is a worked example of both.

## Not covered here

| Shape | Fields | Example file |
|---|---|---|
| Persona weapons | `persona`, `neverBond`, `secretive`, `bondedHediffs`, `bondedThought`, `otherWeaponThought`, `killThirstThought` | `Defs/EntadTraits_Persona.xml` |
| Portable space | `portableSpace` | `Defs/EntadTraits_Space.xml` |
| Power network effects | `powerNet` | `Defs/EntadTraits_PowerNet.xml` |
| Abilities cast by a trigger | trigger `casts` | `Defs/EntadTraits_Procs.xml` |
| Effects and abilities from a compat assembly | `customEffects`, `customCardLines`, `externalAbilities` | `Mods/DubsBadHygiene/`, `Mods/VanillaPsycastsExpanded/` |
