using System.Collections.Generic;

namespace AllaganMarket.Models;

public sealed class MannequinConfiguration
{
    public ulong MannequinId { get; set; }

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
