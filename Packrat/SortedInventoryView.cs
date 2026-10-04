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
public class SortedInventoryView : InventoryBase, IDisposable
{
    private readonly CompositeInventoryView _underlying;
    private SortMode _sortMode = SortMode.None;

    // Maps display position -> underlying slot index (only used when sorting)
    private int[] _displayOrder;

    // Dummy slot to return for out-of-bounds access (VS GUI expects non-null slots)
    private readonly DummySlot _emptySlot = new(null);

    // Track if display order needs rebuilding
    private bool _isDirty;

    // Filter predicate for search filtering
    private System.Func<int, ItemSlot, bool> _filterPredicate;

    /// <summary>
    /// Set a filter predicate for search filtering.
    /// The predicate receives (underlyingSlotIndex, slot) and returns true if slot should be shown.
    /// Setting to null clears the filter.
    /// </summary>
    public System.Func<int, ItemSlot, bool> FilterPredicate
    {
        get => _filterPredicate;
        set
        {
            _filterPredicate = value;
            _isDirty = true;
        }
    }

    /// <summary>
    /// Whether filtering is currently active
    /// </summary>
    public bool IsFiltering => _filterPredicate != null;

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

    /// <summary>
    /// Unsubscribes from the source inventories, which belong to block entities and so
    /// outlive the browser - left subscribed, each one keeps every past view alive.
    /// </summary>
    public void Dispose()
    {
        foreach (var inv in _underlying.SourceInventories)
            inv.SlotModified -= OnSlotModified;
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
    /// Rebuild the display order based on current sort mode and filter
    /// </summary>
    public void RebuildDisplayOrder()
    {
        _isDirty = false;

        // When no sorting AND no filtering, pass through directly
        if (_sortMode == SortMode.None && _filterPredicate == null)
        {
            _displayOrder = null;
            return;
        }

        // Collect slots that pass the filter (and are non-empty when sorting)
        var filteredSlots = new List<(int index, SortKey sortKey, int stackSize)>();

        for (int i = 0; i < _underlying.Count; i++)
        {
            var slot = _underlying[i];

            // When sorting is active, skip empty slots
            if (_sortMode != SortMode.None && slot?.Itemstack == null)
                continue;

            // Apply filter predicate if set
            if (_filterPredicate != null && !_filterPredicate(i, slot))
                continue;

            SortKey sortKey = _sortMode != SortMode.None
                ? GetSortKey(slot, _sortMode)
                : new SortKey(0, i, ""); // Preserve original order when not sorting
            int stackSize = slot?.Itemstack?.StackSize ?? 0;
            filteredSlots.Add((i, sortKey, stackSize));
        }

        // Sort if sorting mode is active
        if (_sortMode != SortMode.None)
        {
            filteredSlots.Sort((a, b) =>
            {
                int cmp = a.sortKey.Group.CompareTo(b.sortKey.Group);
                if (cmp != 0) return cmp;

                cmp = a.sortKey.Number.CompareTo(b.sortKey.Number);
                if (cmp != 0) return cmp;

                cmp = string.Compare(a.sortKey.Text, b.sortKey.Text, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;

                // Larger stacks first
                cmp = b.stackSize.CompareTo(a.stackSize);
                if (cmp != 0) return cmp;

                return a.index.CompareTo(b.index);
            });
        }

        _displayOrder = filteredSlots.Select(s => s.index).ToArray();
    }

    /// <summary>
    /// What slots are ordered by: Group, then Number, then Text. Numbers are kept as
    /// numbers rather than formatted into the text, so close values never round together.
    /// </summary>
    private readonly record struct SortKey(int Group, float Number, string Text);

    /// <summary>
    /// Get the sort key for an item based on the sort mode
    /// </summary>
    private SortKey GetSortKey(ItemSlot slot, SortMode mode)
    {
        var stack = slot.Itemstack;
        var collectible = stack.Collectible;
        if (collectible == null) return new SortKey(int.MaxValue, 0, ""); // Sort unknowns to end

        return mode switch
        {
            SortMode.Alphabetical => new SortKey(0, 0, GetAlphabeticalKey(stack)),
            SortMode.ByCategory => new SortKey(0, 0, $"{(int)GetItemCategory(collectible):D2}_{stack.GetName()}"),
            SortMode.ByMaterial => new SortKey(0, 0, $"{GetMaterialKey(collectible)}_{stack.GetName()}"),
            SortMode.ByPerishable => GetPerishableKey(slot),
            _ => new SortKey(int.MaxValue, 0, "")
        };
    }

    /// <summary>
    /// Get a sort key for Alphabetical sorting
    /// </summary>
    private static string GetAlphabeticalKey(ItemStack stack)
    {
        return stack.GetName() ?? stack.Collectible.Code?.ToString() ?? "zzz";
    }

    /// <summary>
    /// Get a sort key for ByPerishable sorting. Groups, in order:
    /// 0 - already spoiling, most spoiled first
    /// 1 - fresh and perishing, fewest hours until spoiling first
    /// 2 - perishable but paused where it is (rate 0, e.g. too hot), alphabetical
    /// 3 - not perishable, alphabetical
    /// </summary>
    private SortKey GetPerishableKey(ItemSlot slot)
    {
        var stack = slot.Itemstack;
        var world = _underlying.Api.World;
        var state = stack.Collectible.UpdateAndGetTransitionState(world, slot, EnumTransitionType.Perish);
        if (state == null)
            return new SortKey(3, 0, GetAlphabeticalKey(stack));

        // Spoil progress doesn't depend on the container's rate, so this group needs no division
        if (state.TransitionLevel > 0)
            return new SortKey(0, -state.TransitionLevel, "");

        // The same rate the vanilla tooltip divides by, so the order matches the "fresh for" it shows
        float rate = stack.Collectible.GetTransitionRateMul(world, slot, EnumTransitionType.Perish);
        if (rate <= 0)
            return new SortKey(2, 0, GetAlphabeticalKey(stack));

        return new SortKey(1, state.FreshHoursLeft / rate, "");
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
            // Pass through only if no sorting AND no filtering
            if (_sortMode == SortMode.None && _filterPredicate == null)
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
            // Pass through only if no sorting AND no filtering
            if (_sortMode == SortMode.None && _filterPredicate == null)
                return _underlying[slotId];

            // Rebuild if dirty before accessing slots
            if (_isDirty)
            {
                RebuildDisplayOrder();
            }

            if (_displayOrder == null || slotId < 0 || slotId >= _displayOrder.Length)
                return _emptySlot; // Return empty slot instead of null (VS GUI expects non-null)

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
        // Pass through only if no sorting AND no filtering
        if (_sortMode == SortMode.None && _filterPredicate == null)
            return displaySlotId;

        // Rebuild if dirty before translating, so we resolve against current contents
        if (_isDirty)
        {
            RebuildDisplayOrder();
        }

        if (_displayOrder == null || displaySlotId < 0 || displaySlotId >= _displayOrder.Length)
            return -1;

        return _displayOrder[displaySlotId];
    }

    // Persistence not needed - this is a transient view
    public override void FromTreeAttributes(ITreeAttribute tree) { }
    public override void ToTreeAttributes(ITreeAttribute tree) { }

    #endregion
}
