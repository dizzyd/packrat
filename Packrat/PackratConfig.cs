using System.Collections.Generic;

namespace Packrat;

/// <summary>
/// Client-side configuration for Packrat mod
/// </summary>
public class PackratConfig
{
    /// <summary>
    /// The last-used sort mode in the storage browser
    /// </summary>
    public SortMode SortMode { get; set; } = SortMode.None;

    /// <summary>
    /// Container types to prefer when shift-clicking items into storage, highest priority
    /// first. Entries are block code tokens - "trunk", "chest", "labeledchest", "crate",
    /// "storagevessel", "barrel", "stationarybasket" - as reported by .packrat priority types.
    ///
    /// Types not listed here are neutral and rank below every listed type. Priority only
    /// orders otherwise-equivalent targets: merging into a container that already holds the
    /// item always beats claiming a new slot, whatever the order says.
    ///
    /// Empty means no preference, which is the default.
    /// </summary>
    public List<string> ContainerPriority { get; set; } = new();
}
