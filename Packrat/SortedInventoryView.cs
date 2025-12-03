using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace Packrat;

/// <summary>
/// A wrapper around CompositeInventoryView that provides sorted/filtered views.
/// When SortMode is None, passes through directly to the underlying inventory.
/// When sorting is active, only non-empty slots are shown, in sorted order.
/// </summary>
public class SortedInventoryView : InventoryBase
{
    private readonly CompositeInventoryView _underlying;
    private SortMode _sortMode = SortMode.None;

    // Maps display position -> underlying slot index (only used when sorting)
    private int[] _displayOrder;

    // Track if display order needs rebuilding
    private bool _isDirty;

    public SortedInventoryView(CompositeInventoryView underlying)
        : base("sorted", "packrat-sorted-browser", underlying.Api)
    {
        _underlying = underlying;

        // Subscribe to slot changes in all source inventories
        foreach (var inv in _underlying.SourceInventories)
        {
            inv.SlotModified += OnSlotModified;
        }

        RebuildDisplayOrder();
    }

    private void OnSlotModified(int slotId)
    {
        // Mark dirty so we rebuild on next access
        _isDirty = true;
    }

    /// <summary>
    /// Get or set the current sort mode. Setting triggers a rebuild of the display order.
    /// </summary>
    public SortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (_sortMode != value)
            {
                _sortMode = value;
                RebuildDisplayOrder();
            }
        }
    }

    /// <summary>
    /// Whether sorting is currently active (any mode except None)
    /// </summary>
    public bool IsSorting => _sortMode != SortMode.None;

    /// <summary>
    /// Access to the underlying composite inventory (for container boundaries when not sorting)
    /// </summary>
    public CompositeInventoryView Underlying => _underlying;

    /// <summary>
    /// Rebuild the display order based on current sort mode
    /// </summary>
    public void RebuildDisplayOrder()
    {
        _isDirty = false;

        if (_sortMode == SortMode.None)
        {
            _displayOrder = null;
            return;
        }

        // Collect non-empty slots with their sort keys and stack sizes
        var sortableSlots = new List<(int index, string sortKey, int stackSize)>();

        for (int i = 0; i < _underlying.Count; i++)
        {
            var slot = _underlying[i];
            if (slot?.Itemstack == null) continue;

            string sortKey = GetSortKey(slot.Itemstack, _sortMode);
            int stackSize = slot.Itemstack.StackSize;
            sortableSlots.Add((i, sortKey, stackSize));
        }

        // Sort by key, then by stack size (largest first), then by original index for stability
        sortableSlots.Sort((a, b) =>
        {
            int cmp = string.Compare(a.sortKey, b.sortKey, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;

            // Larger stacks first
            cmp = b.stackSize.CompareTo(a.stackSize);
            if (cmp != 0) return cmp;

            return a.index.CompareTo(b.index);
        });

        _displayOrder = sortableSlots.Select(s => s.index).ToArray();
    }

    /// <summary>
    /// Get the sort key for an item based on the sort mode
    /// </summary>
    private string GetSortKey(ItemStack stack, SortMode mode)
    {
        var collectible = stack.Collectible;
        if (collectible == null) return "zzz"; // Sort unknowns to end

        return mode switch
        {
            SortMode.Alphabetical => stack.GetName() ?? collectible.Code?.ToString() ?? "zzz",
            SortMode.ByCategory => $"{(int)GetItemCategory(collectible):D2}_{stack.GetName()}",
            SortMode.ByMaterial => $"{GetMaterialKey(collectible)}_{stack.GetName()}",
            _ => "zzz"
        };
    }

    /// <summary>
    /// Categorize an item for ByCategory sorting
    /// </summary>
    private ItemCategory GetItemCategory(CollectibleObject collectible)
    {
        // Check for tools
        if (collectible.Tool != null)
            return ItemCategory.Tools;

        // Check code-based categories
        var code = collectible.Code?.Path?.ToLowerInvariant() ?? "";

        // Weapons
        if (code.Contains("sword") || code.Contains("spear") || code.Contains("bow") ||
            code.Contains("arrow") || code.Contains("shield") || code.Contains("blade") ||
            code.Contains("falx") || code.Contains("club") || code.Contains("mace"))
            return ItemCategory.Weapons;

        // Food (has nutrition properties)
        if (collectible.NutritionProps != null)
            return ItemCategory.Food;

        // Clothing/Armor
        if (code.Contains("armor") || code.Contains("clothes") || code.Contains("shirt") ||
            code.Contains("trousers") || code.Contains("boots") || code.Contains("hat") ||
            code.Contains("helmet") || code.Contains("gambeson") || code.Contains("brigandine") ||
            code.Contains("chainmail") || code.Contains("scalemail") || code.Contains("platemail") ||
            collectible.FirstCodePart() == "clothing")
            return ItemCategory.Clothing;

        // Plants
        if (code.Contains("seed") || code.Contains("sapling") || code.Contains("flower") ||
            code.Contains("mushroom") || code.Contains("treeseed") || code.Contains("crop") ||
            collectible.FirstCodePart() == "seeds")
            return ItemCategory.Plants;

        // Blocks (placeable)
        if (collectible is Block)
            return ItemCategory.Blocks;

        // Resources (ores, ingots, raw materials)
        if (code.Contains("ore") || code.Contains("ingot") || code.Contains("nugget") ||
            code.Contains("gem") || code.Contains("crystal") || code.Contains("metal") ||
            code.Contains("coal") || code.Contains("charcoal") || code.Contains("clay") ||
            code.Contains("sand") || code.Contains("gravel") || code.Contains("stone") ||
            code.Contains("flint") || code.Contains("leather") || code.Contains("fat") ||
            code.Contains("bone") || code.Contains("stick") || code.Contains("log") ||
            code.Contains("plank") || code.Contains("firewood"))
            return ItemCategory.Resources;

        return ItemCategory.Other;
    }

    /// <summary>
    /// Get a material sort key for ByMaterial sorting
    /// </summary>
    private string GetMaterialKey(CollectibleObject collectible)
    {
        // For blocks, use BlockMaterial
        if (collectible is Block block)
        {
            return block.BlockMaterial.ToString();
        }

        // For items, try to infer from code
        var code = collectible.Code?.Path?.ToLowerInvariant() ?? "";

        if (code.Contains("copper")) return "Copper";
        if (code.Contains("bronze")) return "Bronze";
        if (code.Contains("iron")) return "Iron";
        if (code.Contains("steel")) return "Steel";
        if (code.Contains("gold")) return "Gold";
        if (code.Contains("silver")) return "Silver";
        if (code.Contains("tin")) return "Tin";
        if (code.Contains("lead")) return "Lead";
        if (code.Contains("zinc")) return "Zinc";
        if (code.Contains("bismuth")) return "Bismuth";
        if (code.Contains("titanium")) return "Titanium";
        if (code.Contains("stone") || code.Contains("rock")) return "Stone";
        if (code.Contains("wood") || code.Contains("log") || code.Contains("plank")) return "Wood";
        if (code.Contains("clay")) return "Clay";
        if (code.Contains("ceramic")) return "Ceramic";
        if (code.Contains("glass")) return "Glass";
        if (code.Contains("cloth") || code.Contains("linen") || code.Contains("wool")) return "Cloth";
        if (code.Contains("leather")) return "Leather";
        if (code.Contains("bone")) return "Bone";

        return "ZOther"; // Sort unknowns to end
    }

    #region InventoryBase Implementation

    public override int Count
    {
        get
        {
            if (_sortMode == SortMode.None)
                return _underlying.Count;

            // Rebuild if dirty before returning count
            if (_isDirty)
            {
                RebuildDisplayOrder();
            }

            return _displayOrder?.Length ?? 0;
        }
    }

    public override ItemSlot this[int slotId]
    {
        get
        {
            if (_sortMode == SortMode.None)
                return _underlying[slotId];

            // Rebuild if dirty before accessing slots
            if (_isDirty)
            {
                RebuildDisplayOrder();
            }

            if (_displayOrder == null || slotId < 0 || slotId >= _displayOrder.Length)
                return null;

            return _underlying[_displayOrder[slotId]];
        }
        set
        {
            // Read-only view
        }
    }

    /// <summary>
    /// Delegate to the underlying inventory with proper slot translation
    /// </summary>
    public override object ActivateSlot(int slotId, ItemSlot sourceSlot, ref ItemStackMoveOperation op)
    {
        int realSlotId = TranslateSlotId(slotId);
        if (realSlotId < 0) return null;

        return _underlying.ActivateSlot(realSlotId, sourceSlot, ref op);
    }

    /// <summary>
    /// Translate a display slot ID to the underlying slot ID
    /// </summary>
    private int TranslateSlotId(int displaySlotId)
    {
        if (_sortMode == SortMode.None)
            return displaySlotId;

        if (_displayOrder == null || displaySlotId < 0 || displaySlotId >= _displayOrder.Length)
            return -1;

        return _displayOrder[displaySlotId];
    }

    // Persistence not needed - this is a transient view
    public override void FromTreeAttributes(ITreeAttribute tree) { }
    public override void ToTreeAttributes(ITreeAttribute tree) { }

    #endregion
}
