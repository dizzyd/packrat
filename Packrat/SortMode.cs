namespace Packrat;

/// <summary>
/// Sort modes for the storage browser view
/// </summary>
public enum SortMode
{
    /// <summary>
    /// No sorting - show items in original container order with all slots visible
    /// </summary>
    None,

    /// <summary>
    /// Sort alphabetically by item name
    /// </summary>
    Alphabetical,

    /// <summary>
    /// Sort by item category (Tools, Weapons, Food, etc.)
    /// </summary>
    ByCategory,

    /// <summary>
    /// Sort by material type (Stone, Metal, Wood, etc.)
    /// </summary>
    ByMaterial
}

/// <summary>
/// Item categories for sorting
/// </summary>
public enum ItemCategory
{
    Tools,
    Weapons,
    Food,
    Blocks,
    Resources,
    Plants,
    Clothing,
    Other
}
