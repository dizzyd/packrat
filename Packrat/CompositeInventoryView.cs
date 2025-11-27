using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace Packrat;

/// <summary>
/// A virtual inventory that wraps multiple real inventories and presents them as one.
/// When a slot is activated, it delegates to the real inventory so packets contain
/// the correct inventory ID for server synchronization.
/// </summary>
public class CompositeInventoryView : InventoryBase
{
    // Maps virtual slot index → (real inventory, real slot index)
    private readonly List<(InventoryBase inv, int slotId)> _slotMap = new();

    // Track which inventories we've added
    private readonly List<InventoryBase> _sourceInventories = new();

    // Track container boundaries: (startSlotIndex, slotCount) for each container
    private readonly List<(int startIndex, int count)> _containerBoundaries = new();

    // Track which inventories are crates (have special item type restrictions)
    private readonly HashSet<InventoryBase> _crateInventories = new();

    public CompositeInventoryView(ICoreAPI api)
        : base("composite", "packrat-browser", api)
    {
    }

    /// <summary>
    /// Get the boundaries of each container's slots for rendering outlines
    /// </summary>
    public IReadOnlyList<(int startIndex, int count)> ContainerBoundaries => _containerBoundaries;

    /// <summary>
    /// Add an inventory's slots to this composite view
    /// </summary>
    /// <param name="inv">The inventory to add</param>
    /// <param name="isCrate">If true, this inventory has crate-style item type restrictions</param>
    public void AddInventory(InventoryBase inv, bool isCrate = false)
    {
        if (_sourceInventories.Contains(inv)) return;

        int startIndex = _slotMap.Count;
        _sourceInventories.Add(inv);
        if (isCrate)
        {
            _crateInventories.Add(inv);
        }
        for (int i = 0; i < inv.Count; i++)
        {
            _slotMap.Add((inv, i));
        }
        _containerBoundaries.Add((startIndex, inv.Count));
    }

    /// <summary>
    /// Remove an inventory from this composite view
    /// </summary>
    public void RemoveInventory(InventoryBase inv)
    {
        if (!_sourceInventories.Contains(inv)) return;

        _sourceInventories.Remove(inv);
        _crateInventories.Remove(inv);
        _slotMap.RemoveAll(entry => entry.inv == inv);
    }

    /// <summary>
    /// Clear all inventories from the view
    /// </summary>
    public new void Clear()
    {
        _sourceInventories.Clear();
        _slotMap.Clear();
        _containerBoundaries.Clear();
        _crateInventories.Clear();
    }

    /// <summary>
    /// Get the list of source inventories
    /// </summary>
    public IReadOnlyList<InventoryBase> SourceInventories => _sourceInventories;

    /// <summary>
    /// Check if a virtual slot is in a crate inventory
    /// </summary>
    public bool IsSlotInCrate(int virtualSlotId)
    {
        if (virtualSlotId < 0 || virtualSlotId >= _slotMap.Count) return false;
        var (inv, _) = _slotMap[virtualSlotId];
        return _crateInventories.Contains(inv);
    }

    /// <summary>
    /// Get the template item for a crate slot (what item type the crate holds).
    /// Returns null if the crate is empty or the slot isn't in a crate.
    /// </summary>
    public ItemStack GetCrateTemplateItem(int virtualSlotId)
    {
        if (virtualSlotId < 0 || virtualSlotId >= _slotMap.Count) return null;
        var (inv, _) = _slotMap[virtualSlotId];

        if (!_crateInventories.Contains(inv)) return null;

        // Find the first non-empty slot in this crate to determine the item type
        for (int i = 0; i < inv.Count; i++)
        {
            var stack = inv[i]?.Itemstack;
            if (stack != null)
            {
                return stack;
            }
        }

        return null;
    }

    /// <summary>
    /// Map a virtual slot ID to the real inventory and slot
    /// </summary>
    public (InventoryBase inv, int slotId) MapSlot(int virtualSlotId)
    {
        if (virtualSlotId < 0 || virtualSlotId >= _slotMap.Count)
            return (null, -1);
        return _slotMap[virtualSlotId];
    }

    public override int Count => _slotMap.Count;

    public override ItemSlot this[int slotId]
    {
        get
        {
            if (slotId < 0 || slotId >= _slotMap.Count) return null;
            var (inv, realSlot) = _slotMap[slotId];
            return inv[realSlot];
        }
        set
        {
            // Read-only view - slots belong to real inventories
        }
    }

    /// <summary>
    /// Override to implement custom placement preferences:
    /// 1. Crates with matching items (highest priority)
    /// 2. Chests with similar items
    /// 3. Any empty slot
    /// </summary>
    public override WeightedSlot GetBestSuitedSlot(ItemSlot sourceSlot, ItemStackMoveOperation op = null, List<ItemSlot> skipSlots = null)
    {
        WeightedSlot bestSlot = new WeightedSlot();

        if (sourceSlot?.Itemstack == null)
            return bestSlot;

        // If the source slot is FROM one of our inventories, return empty
        // so items go to the player inventory instead of staying in the browser
        if (_slotMap.Any(entry => entry.inv[entry.slotId] == sourceSlot))
            return bestSlot;

        var itemCode = sourceSlot.Itemstack.Collectible?.Code;
        if (itemCode == null)
            return bestSlot;

        ItemSlot bestCrateSlot = null;
        ItemSlot bestChestSlotWithMatch = null;
        ItemSlot firstEmptySlot = null;

        for (int i = 0; i < _slotMap.Count; i++)
        {
            var (inv, slotId) = _slotMap[i];
            var slot = inv[slotId];

            if (slot == null) continue;
            if (skipSlots != null && skipSlots.Contains(slot)) continue;
            if (!slot.CanHold(sourceSlot)) continue;

            bool isCrate = _crateInventories.Contains(inv);

            if (slot.Itemstack != null)
            {
                // Slot has items - check for merge possibility
                if (slot.Itemstack.Collectible?.Code?.Equals(itemCode) == true)
                {
                    // Can potentially merge - check stack size
                    if (slot.StackSize < slot.Itemstack.Collectible.MaxStackSize)
                    {
                        if (isCrate)
                        {
                            // Priority 1: Merge into crate with matching items
                            bestSlot.slot = slot;
                            bestSlot.weight = 6;
                            return bestSlot; // Highest priority, return immediately
                        }
                        else if (bestChestSlotWithMatch == null)
                        {
                            // Priority 2: Merge into chest with matching items
                            bestChestSlotWithMatch = slot;
                        }
                    }
                }
            }
            else
            {
                // Empty slot
                if (isCrate)
                {
                    // Check if crate can accept this item type
                    if (CanPlaceInCrate(inv, sourceSlot.Itemstack) && bestCrateSlot == null)
                    {
                        bestCrateSlot = slot;
                    }
                }
                else if (firstEmptySlot == null)
                {
                    firstEmptySlot = slot;
                }
            }
        }

        // Return best match based on priority
        if (bestCrateSlot != null)
        {
            // Priority 1b: Empty slot in crate that accepts this item type
            bestSlot.slot = bestCrateSlot;
            bestSlot.weight = 5;
        }
        else if (bestChestSlotWithMatch != null)
        {
            // Priority 2: Chest with matching items
            bestSlot.slot = bestChestSlotWithMatch;
            bestSlot.weight = 4;
        }
        else if (firstEmptySlot != null)
        {
            // Priority 3: Any empty slot
            bestSlot.slot = firstEmptySlot;
            bestSlot.weight = 1;
        }

        return bestSlot;
    }

    /// <summary>
    /// Check if an item can be placed into a crate inventory based on what's already in it
    /// Crates only allow one item type - all slots must contain the same item or be empty
    /// </summary>
    private bool CanPlaceInCrate(InventoryBase crateInv, ItemStack itemToPlace)
    {
        if (itemToPlace == null) return true;

        // Find what item type is already in the crate (if any)
        for (int i = 0; i < crateInv.Count; i++)
        {
            var existingStack = crateInv[i]?.Itemstack;
            if (existingStack != null)
            {
                // Crate has items - check if the new item matches
                // Compare by item/block code (same type of item)
                return existingStack.Collectible?.Code?.Equals(itemToPlace.Collectible?.Code) == true;
            }
        }

        // Crate is empty - any item is allowed
        return true;
    }

    /// <summary>
    /// KEY METHOD: Delegate to the real inventory so the packet has the correct InventoryID
    /// </summary>
    public override object ActivateSlot(int slotId, ItemSlot sourceSlot, ref ItemStackMoveOperation op)
    {
        if (slotId < 0 || slotId >= _slotMap.Count) return null;

        var (realInv, realSlotId) = _slotMap[slotId];

        // Check crate restrictions before allowing placement
        if (_crateInventories.Contains(realInv) && sourceSlot?.Itemstack != null)
        {
            if (!CanPlaceInCrate(realInv, sourceSlot.Itemstack))
            {
                return null;  // Block the operation - item type doesn't match crate contents
            }
        }

        return realInv.ActivateSlot(realSlotId, sourceSlot, ref op);
    }

    // We don't persist anything - this is a transient view
    public override void FromTreeAttributes(ITreeAttribute tree) { }
    public override void ToTreeAttributes(ITreeAttribute tree) { }
}
