# Packrat

A Vintage Story mod that opens every storage container around you in a single dialog,
so you can search, sort and shift-click across all of them at once instead of one
chest at a time.

## For Players

### Opening the browser

Press **R** (rebindable under Controls → "[PackRat] Open All Containers") and every
container you can reach opens in one Storage Browser window. Press it again to close.

What counts as "reachable" depends on where you are standing:

- **Inside an enclosed room**, every container in the room opens, even if a pillar or
  a wall corner is between you and it.
- **Out in the open**, containers within about 5 blocks open, as long as you can
  actually see them.

Containers locked against you by block reinforcement are skipped.

The browser has a search box and a sort dropdown (alphabetical, by category, by
material), and shows which slots belong to which container.

### Insert priority

Shift-clicking an item into the browser sends it to whichever container suits it best.
By default that means:

1. A container that **already holds that item** wins - your stack gets topped up rather
   than scattered into a new container.
2. Failing that, a container that **already has free space** wins.
3. A **wholly-empty crate is the last resort**, because putting anything into an empty
   crate locks that whole crate to a single item type.
4. **Perishable food** overrides all of the above and goes to whichever container
   preserves it best - a cellar, a storage vessel, an ice box. This accounts both for how
   cool the spot is and for containers that slow spoilage on their own, so a storage
   vessel beats a plain chest standing right next to it.

If you want a different order between container types, set one:

```
.packrat priority set trunk,chest,crate
```

Highest priority first. Types you do not list rank below every type you do list.

Priority only decides between **equally good** targets. It never overrides rule 1 or
rule 4 above: if a crate already holds your nails, the nails go in that crate even when
trunks are top of your list, and cheese still goes in the cellar. Priority decides where
things land when nothing already holds them - which container gets claimed for a new
item type, and which empty container gets used first.

### Finding the right type names

The names are block codes, and they are not always what you would guess - a Basket is
`stationarybasket`, and a Trunk is `trunk` rather than a kind of `chest`. To see the
correct token for everything around you:

```
.packrat priority types
```

```
Container types in reach:
  chest - "Chest" x4
  crate - "Crate" x7 [priority 3]
  stationarybasket - "Basket" x1
  trunk - "Trunk" x2 [priority 1]
Use the token on the left with '.packrat priority set'.
```

### Commands

| Command | What it does |
| --- | --- |
| `.packrat priority` | Show the current priority list |
| `.packrat priority set <types>` | Set the order, highest first, comma or space separated |
| `.packrat priority reset` | Clear the list and go back to defaults |
| `.packrat priority types` | List the container types in reach and their tokens |
| `.packratdebug` | Toggle debug logging |

Your priority list is per-player and saved in `ModConfig/packrat-client.json` in your
Vintage Story data folder.

### On servers

Packrat must be installed on the server as well as the client. This is not optional
for the priority feature: when you shift-click, the game does not tell the server where
the items went - it tells the server only which slot you clicked, and the server works
out the destination itself. Your priority list is sent to the server so that both sides
reach the same answer.

### Compatible storage mods

Packrat picks up containers from Sortable Storage, Containers Bundle, Better Crates,
QP's Storage Controller (including its linked containers) and Primitive Survival's tree
hollows, alongside all the vanilla ones.

## Upgrading from 1.1.x

**Empty crates are no longer the preferred shift-click target.** Previously an empty
crate outranked any container that merely had free space, and ranked level with a
container that already held the item - so shift-clicking could claim a fresh crate for a
single stack while a trunk sat there with room, and which one you got could depend on
the order the containers happened to open in. Now an empty crate ranks last, and items
go to containers with existing stacks or existing space first.

If you preferred the old behavior - crates first, always - you can ask for it explicitly:

```
.packrat priority set crate
```

Shift-clicking also now prefers open containers over your own backpack more consistently
than before, where the two used to score equally and the winner depended on the order
things were opened in.

## Building

```bash
./build.sh
```

The built mod lands in the `Releases` directory.

## Installation

Copy the `.zip` from `Releases` into your Vintage Story `Mods` folder.
