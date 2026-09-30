using System.Collections.Generic;

namespace AllaganMarket.Models;

public sealed class MannequinConfiguration
{
    /// <summary>
    /// Gets or sets the preset's key in <see cref="Configuration.MannequinConfigurations"/>.
    /// Older presets were keyed by the mannequin's game object id, which changes on
    /// every zone load, so the value is only an identifier; mannequins are matched
    /// to presets by the gear they carry.
    /// </summary>
    public ulong MannequinId { get; set; }

    /// <summary>
    /// Gets or sets the user-given preset name; empty falls back to the first item's name.
    /// </summary>
    public string? Name { get; set; }

    public ulong RetainerId { get; set; }

    public bool SellAsSet { get; set; }

    public List<MannequinItem> Items { get; set; } = [];
}

public sealed class MannequinItem
{
    public int EquipmentSlot { get; set; }

    public uint ItemId { get; set; }

    public bool IsHighQuality { get; set; }

    public uint UnitPrice { get; set; }

    public bool IsSoldOut { get; set; }
}
