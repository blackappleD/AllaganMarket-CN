using System;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace AllaganMarket.Agents;

[StructLayout(LayoutKind.Explicit)]
public unsafe struct AgentMerchantSettingInfo
{
    public static AgentMerchantSettingInfo* Instance()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
        {
            return null;
        }

        var agent = agentModule->GetAgentByInternalId(AgentId.MerchantSetting);
        if (agent == null)
        {
            return null;
        }

        return (AgentMerchantSettingInfo*)*(nint*)((nint)agent + 40);
    }

    [FieldOffset(192)]
    public MannequinItem Items;

    [FieldOffset(1912)]
    public uint SelectedItems;

    public readonly Span<MannequinItem> ItemsSpan
    {
        get
        {
            fixed (MannequinItem* pointer = &this.Items)
            {
                return new Span<MannequinItem>(pointer, 12);
            }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 152)]
    public struct MannequinItem
    {
        [FieldOffset(0)]
        public uint ItemIdWithQuality;

        [FieldOffset(24)]
        public nint Price;

        [FieldOffset(42)]
        public byte Color1;

        [FieldOffset(43)]
        public byte Color2;

        [FieldOffset(45)]
        public byte Availability;

        public readonly uint ItemId => this.ItemIdWithQuality % 1_000_000;

        public readonly bool IsHighQuality => this.ItemIdWithQuality > 1_000_000;
    }
}
