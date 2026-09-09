using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AllaganMarket.Models;
using AllaganMarket.Services.Interfaces;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Microsoft.Extensions.Hosting;

namespace AllaganMarket.Services;

public sealed class MannequinRestockService : IHostedService
{
    private static readonly InventoryType[] PlayerInventoryTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IInventoryService inventoryService;
    private readonly IRetainerService retainerService;
    private readonly IPluginLog pluginLog;
    private readonly Configuration configuration;

    public MannequinRestockService(
        IAddonLifecycle addonLifecycle,
        IInventoryService inventoryService,
        IRetainerService retainerService,
        IPluginLog pluginLog,
        Configuration configuration)
    {
        this.addonLifecycle = addonLifecycle;
        this.inventoryService = inventoryService;
        this.retainerService = retainerService;
        this.pluginLog = pluginLog;
        this.configuration = configuration;
    }

    public bool IsMannequinWindowVisible { get; private set; }

    public string? MannequinAddonName { get; private set; }

    public nint MannequinAddonAddress { get; private set; }

    public MannequinConfiguration? CurrentConfiguration { get; private set; }

    public string StatusMessage { get; private set; } = "等待打开服装模特商店设定。";

    public event Action? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, this.OnAnyAddonChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh, this.OnAnyAddonChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PostDraw, this.OnAnyAddonChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize, this.OnAnyAddonFinalized);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, this.OnAnyAddonChanged);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, this.OnAnyAddonChanged);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostDraw, this.OnAnyAddonChanged);
        this.addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, this.OnAnyAddonFinalized);

        return Task.CompletedTask;
    }

    public void BeginRestock()
    {
        if (!this.IsMannequinWindowVisible || this.CurrentConfiguration == null)
        {
            this.StatusMessage = "请先完成一次正常上架，采集当前模特的装备和价格。";
            this.pluginLog.Warning("Cannot start mannequin restock because no supported mannequin window is active.");
            this.StateChanged?.Invoke();
            return;
        }

        var plan = this.BuildRestockPlan(this.CurrentConfiguration);
        this.pluginLog.Information(
            "Mannequin restock requested for {Count} items; {Missing} items are unavailable.",
            plan.Count,
            plan.Count(item => item.Source == RestockItemSource.Missing));
        this.StatusMessage = plan.Count == 0
            ? "当前没有检测到售罄装备。"
            : "补货流程尚未启用：等待当前客户端的模特回调定义。";
        this.StateChanged?.Invoke();
    }

    public void SaveConfiguration(MannequinConfiguration mannequinConfiguration)
    {
        if (mannequinConfiguration.MannequinId == 0)
        {
            return;
        }

        this.configuration.MannequinConfigurations[mannequinConfiguration.MannequinId] = mannequinConfiguration;
        this.configuration.IsDirty = true;
        this.CurrentConfiguration = mannequinConfiguration;
        this.StateChanged?.Invoke();
    }

    public unsafe IReadOnlyList<RestockItemPlan> BuildRestockPlan(MannequinConfiguration mannequinConfiguration)
    {
        return mannequinConfiguration.Items
            .Where(item => item.ItemId != 0 && item.IsSoldOut)
            .Select(item => new RestockItemPlan(
                item,
                this.FindPlayerInventoryItem(item) != null
                    ? RestockItemSource.PlayerInventory
                    : this.FindRetainerInventoryItem(item) != null
                        ? RestockItemSource.RetainerInventory
                        : RestockItemSource.Missing))
            .ToArray();
    }

    private unsafe InventoryItem* FindPlayerInventoryItem(MannequinItem item)
    {
        foreach (var inventoryType in PlayerInventoryTypes)
        {
            var container = this.inventoryService.GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var index = 0; index < container->Size; index++)
            {
                var inventoryItem = &container->Items[index];
                if (inventoryItem->ItemId == item.ItemId &&
                    inventoryItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == item.IsHighQuality)
                {
                    return inventoryItem;
                }
            }
        }

        return null;
    }

    private unsafe InventoryItem* FindRetainerInventoryItem(MannequinItem item)
    {
        if (this.retainerService.RetainerId == 0)
        {
            return null;
        }

        for (var inventoryType = InventoryType.RetainerPage1; inventoryType <= InventoryType.RetainerPage7; inventoryType++)
        {
            var container = this.inventoryService.GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var index = 0; index < container->Size; index++)
            {
                var inventoryItem = &container->Items[index];
                if (inventoryItem->ItemId == item.ItemId &&
                    inventoryItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == item.IsHighQuality)
                {
                    return inventoryItem;
                }
            }
        }

        return null;
    }

    private unsafe void OnAnyAddonChanged(AddonEvent type, AddonArgs args)
    {
        if (args.Addon == IntPtr.Zero)
        {
            return;
        }

        if (type == AddonEvent.PostDraw)
        {
            if (args.Addon.Address != this.MannequinAddonAddress)
            {
                return;
            }

            var addon = (AtkUnitBase*)args.Addon.Address;
            var isVisible = addon != null && addon->IsReady && addon->IsVisible;
            if (this.IsMannequinWindowVisible != isVisible)
            {
                this.IsMannequinWindowVisible = isVisible;
                this.StateChanged?.Invoke();
            }

            return;
        }

        if (!this.IsMannequinAddon(args.Addon.Address))
        {
            return;
        }

        this.IsMannequinWindowVisible = true;
        this.MannequinAddonName = args.AddonName;
        this.MannequinAddonAddress = args.Addon.Address;
        this.CurrentConfiguration = null;
        this.StatusMessage = $"已识别模特窗口：{args.AddonName}。等待配置采集。";
        this.LogAddonSummary((AtkUnitBase*)args.Addon.Address);
        this.StateChanged?.Invoke();
    }

    private unsafe void OnAnyAddonFinalized(AddonEvent type, AddonArgs args)
    {
        if (args.Addon == IntPtr.Zero || args.Addon != this.MannequinAddonAddress)
        {
            return;
        }

        this.IsMannequinWindowVisible = false;
        this.MannequinAddonName = null;
        this.MannequinAddonAddress = IntPtr.Zero;
        this.CurrentConfiguration = null;
        this.StatusMessage = "等待打开服装模特商店设定。";
        this.StateChanged?.Invoke();
    }

    private unsafe bool IsMannequinAddon(nint addonAddress)
    {
        var addon = (AtkUnitBase*)addonAddress;
        if (addon == null || !addon->IsReady || !addon->IsVisible)
        {
            return false;
        }

        if (addon->NameString.Contains("Mannequin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsMannequinText(addon->RootNode, 0);
    }

    private unsafe void LogAddonSummary(AtkUnitBase* addon)
    {
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount == 0)
        {
            return;
        }

        var values = new List<string>();
        for (var index = 0; index < addon->AtkValuesCount; index++)
        {
            var value = addon->AtkValues[index];
            values.Add(value.Type switch
            {
                AtkValueType.Int => $"I:{value.Int}",
                AtkValueType.UInt => $"U:{value.UInt}",
                AtkValueType.Int64 => $"L:{value.Int64}",
                AtkValueType.UInt64 => $"UL:{value.UInt64}",
                AtkValueType.Float => $"F:{value.Float}",
                AtkValueType.Bool => $"B:{value.Bool}",
                AtkValueType.String => "S",
                _ => value.Type.ToString(),
            });
        }

        this.pluginLog.Information(
            "Detected mannequin addon {AddonName}; AtkValuesCount={Count}; values={Values}",
            addon->NameString,
            addon->AtkValuesCount,
            string.Join(",", values));
    }

    private static unsafe bool ContainsMannequinText(AtkResNode* node, int depth)
    {
        if (node == null || depth > 16)
        {
            return false;
        }

        if (node->Type == NodeType.Text)
        {
            var text = node->GetAsAtkTextNode()->NodeText.ToString();
            if (text.Contains("服装模特商店设定", StringComparison.Ordinal) ||
                text.Contains("Mannequin Shop Settings", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
        {
            if (ContainsMannequinText(child, depth + 1))
            {
                return true;
            }
        }

        return false;
    }
}

public enum RestockItemSource
{
    Missing,
    PlayerInventory,
    RetainerInventory,
}

public sealed record RestockItemPlan(MannequinItem Item, RestockItemSource Source);
