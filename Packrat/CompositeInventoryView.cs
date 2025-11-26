using System.Collections.Generic;
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
    public void AddInventory(InventoryBase inv)
    {
        if (_sourceInventories.Contains(inv)) return;

        int startIndex = _slotMap.Count;
        _sourceInventories.Add(inv);
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
    }

    /// <summary>
    /// Get the list of source inventories
    /// </summary>
    public IReadOnlyList<InventoryBase> SourceInventories => _sourceInventories;

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
    /// KEY METHOD: Delegate to the real inventory so the packet has the correct InventoryID
    /// </summary>
    public override object ActivateSlot(int slotId, ItemSlot sourceSlot, ref ItemStackMoveOperation op)
    {
        if (slotId < 0 || slotId >= _slotMap.Count) return null;

        var (realInv, realSlotId) = _slotMap[slotId];
        return realInv.ActivateSlot(realSlotId, sourceSlot, ref op);
    }

    // We don't persist anything - this is a transient view
    public override void FromTreeAttributes(ITreeAttribute tree) { }
    public override void ToTreeAttributes(ITreeAttribute tree) { }
}
