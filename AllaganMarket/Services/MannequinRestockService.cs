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
    private const string MannequinAddonNameValue = "MerchantSetting";
    private const long DiagnosticIntervalMilliseconds = 2000;

    private static readonly InventoryType[] PlayerInventoryTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IInventoryService inventoryService;
    private readonly IRetainerService retainerService;
    private readonly IPluginLog pluginLog;
    private readonly Configuration configuration;
    private readonly Dictionary<nint, string> lastAddonDiagnosticStates = [];
    private readonly Dictionary<nint, string> recentAddonDiagnostics = [];
    private long lastDiagnosticMilliseconds;

    public MannequinRestockService(
        IAddonLifecycle addonLifecycle,
        IFramework framework,
        IGameGui gameGui,
        IInventoryService inventoryService,
        IRetainerService retainerService,
        IPluginLog pluginLog,
        Configuration configuration)
    {
        this.addonLifecycle = addonLifecycle;
        this.framework = framework;
        this.gameGui = gameGui;
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
        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, this.OnAddonLifecycleEvent);
        this.addonLifecycle.RegisterListener(AddonEvent.PostShow, this.OnAddonLifecycleEvent);
        this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh, this.OnAddonLifecycleEvent);
        this.addonLifecycle.RegisterListener(AddonEvent.PostDraw, this.OnAddonLifecycleEvent);
        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, MannequinAddonNameValue, this.OnAnyAddonChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh, MannequinAddonNameValue, this.OnAnyAddonChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PostDraw, MannequinAddonNameValue, this.OnAnyAddonChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PostShow, MannequinAddonNameValue, this.OnAnyAddonChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize, MannequinAddonNameValue, this.OnAnyAddonFinalized);
        this.framework.Update += this.OnFrameworkUpdate;
        this.pluginLog.Information("[MannequinDiag] service started; target addon={AddonName}.", MannequinAddonNameValue);
        this.RefreshMannequinAddon();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.framework.Update -= this.OnFrameworkUpdate;
        this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, this.OnAddonLifecycleEvent);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostShow, this.OnAddonLifecycleEvent);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, this.OnAddonLifecycleEvent);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostDraw, this.OnAddonLifecycleEvent);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, MannequinAddonNameValue, this.OnAnyAddonChanged);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, MannequinAddonNameValue, this.OnAnyAddonChanged);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostDraw, MannequinAddonNameValue, this.OnAnyAddonChanged);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostShow, MannequinAddonNameValue, this.OnAnyAddonChanged);
        this.addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, MannequinAddonNameValue, this.OnAnyAddonFinalized);

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

    public void DumpDiagnostics()
    {
        this.pluginLog.Information(
            "[MannequinDiag] manual dump; serviceActive=true; target addon={AddonName}; trackedAddon={TrackedAddon}; trackedAddress=0x{Address:X}; visible={Visible}.",
            MannequinAddonNameValue,
            this.MannequinAddonName ?? "<none>",
            this.MannequinAddonAddress,
            this.IsMannequinWindowVisible);

        this.LogKnownMannequinState("manual");
        foreach (var diagnostic in this.recentAddonDiagnostics.Values.OrderBy(value => value, StringComparer.Ordinal))
        {
            this.pluginLog.Information("[MannequinDiag] recent addon: {Diagnostic}", diagnostic);
        }
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

        if (this.IsMannequinAddon(args.Addon.Address))
        {
            if (args.Addon.Address != this.MannequinAddonAddress || !this.IsMannequinWindowVisible)
            {
                this.IsMannequinWindowVisible = true;
                this.MannequinAddonName = args.AddonName;
                this.MannequinAddonAddress = args.Addon.Address;
                this.CurrentConfiguration = null;
                this.StatusMessage = $"已识别模特窗口：{args.AddonName}。等待配置采集。";
                this.LogAddonSummary((AtkUnitBase*)args.Addon.Address);
                this.StateChanged?.Invoke();
            }

            if (type == AddonEvent.PostDraw)
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                var isVisible = addon != null && addon->IsReady && addon->IsVisible;
                if (this.IsMannequinWindowVisible != isVisible)
                {
                    this.IsMannequinWindowVisible = isVisible;
                    this.StateChanged?.Invoke();
                }
            }

            return;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.RefreshMannequinAddon();
        if (Environment.TickCount64 - this.lastDiagnosticMilliseconds < DiagnosticIntervalMilliseconds)
        {
            return;
        }

        this.lastDiagnosticMilliseconds = Environment.TickCount64;
        this.LogKnownMannequinState("poll");
    }

    private unsafe void OnAddonLifecycleEvent(AddonEvent type, AddonArgs args)
    {
        if (args.Addon == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null)
            {
                return;
            }

            var addonName = addon->NameString;
            var shouldReadText = type is AddonEvent.PostSetup or AddonEvent.PostShow ||
                                 IsDiagnosticCandidate(addonName, string.Empty) ||
                                 this.recentAddonDiagnostics.ContainsKey((nint)addon);
            var text = shouldReadText ? GetNodeTextSummary(addon->RootNode) : "<text skipped>";
            if (type == AddonEvent.PostDraw && !IsDiagnosticCandidate(addonName, text) &&
                this.recentAddonDiagnostics.ContainsKey((nint)addon))
            {
                return;
            }

            this.LogAddonDiagnostic(type.ToString(), args.AddonName, addon, text, type == AddonEvent.PostDraw);
        }
        catch (Exception exception)
        {
            this.pluginLog.Error(exception, "[MannequinDiag] failed to inspect addon lifecycle event {Event}.", type);
        }
    }

    private unsafe void LogKnownMannequinState(string source)
    {
        try
        {
            var addonAddress = this.gameGui.GetAddonByName(MannequinAddonNameValue);
            var addon = (AtkUnitBase*)addonAddress.Address;
            if (addon == null)
            {
                this.pluginLog.Information("[MannequinDiag] source={Source}; addon={AddonName}; address=0; state=not-found.", source, MannequinAddonNameValue);
                return;
            }

            this.LogAddonDiagnostic(source, MannequinAddonNameValue, addon, GetNodeTextSummary(addon->RootNode), false);
        }
        catch (Exception exception)
        {
            this.pluginLog.Error(exception, "[MannequinDiag] failed to poll addon {AddonName}.", MannequinAddonNameValue);
        }
    }

    private unsafe void LogAddonDiagnostic(
        string source,
        string argsAddonName,
        AtkUnitBase* addon,
        string text,
        bool throttle)
    {
        var address = (nint)addon;
        var state = $"{addon->NameString};{addon->IsReady};{addon->IsVisible};{text}";
        if (throttle && this.lastAddonDiagnosticStates.TryGetValue(address, out var previousDiagnostic) &&
            previousDiagnostic == state &&
            Environment.TickCount64 - this.lastDiagnosticMilliseconds < DiagnosticIntervalMilliseconds)
        {
            return;
        }

        var diagnostic =
            $"source={source}; argsName={argsAddonName}; addonName={addon->NameString}; address=0x{address:X}; ready={addon->IsReady}; visible={addon->IsVisible}; position=({addon->X:0},{addon->Y:0}); size=({addon->GetScaledWidth(true):0},{addon->GetScaledHeight(true):0}); atkValues={addon->AtkValuesCount}; nodes={addon->UldManager.NodeListCount}; text={text}";
        this.lastAddonDiagnosticStates[address] = state;
        this.recentAddonDiagnostics[address] = diagnostic;
        while (this.recentAddonDiagnostics.Count > 100)
        {
            this.recentAddonDiagnostics.Remove(this.recentAddonDiagnostics.Keys.First());
        }
        this.lastDiagnosticMilliseconds = Environment.TickCount64;

        this.pluginLog.Information("[MannequinDiag] {Diagnostic}", diagnostic);
    }

    private unsafe void RefreshMannequinAddon()
    {
        var addonAddress = this.gameGui.GetAddonByName(MannequinAddonNameValue);
        var addon = (AtkUnitBase*)addonAddress.Address;
        if (addon == null || !addon->IsReady || !addon->IsVisible)
        {
            if (this.IsMannequinWindowVisible)
            {
                this.IsMannequinWindowVisible = false;
                this.MannequinAddonAddress = IntPtr.Zero;
                this.MannequinAddonName = null;
                this.CurrentConfiguration = null;
                this.StatusMessage = "等待打开服装模特商店设定。";
                this.StateChanged?.Invoke();
            }

            return;
        }

        if (addonAddress.Address == this.MannequinAddonAddress && this.IsMannequinWindowVisible)
        {
            return;
        }

        this.IsMannequinWindowVisible = true;
        this.MannequinAddonName = MannequinAddonNameValue;
        this.MannequinAddonAddress = addonAddress.Address;
        this.CurrentConfiguration = null;
        this.StatusMessage = $"已识别模特窗口：{MannequinAddonNameValue}。等待配置采集。";
        this.LogAddonSummary(addon);
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

        var addonName = addon->NameString;
        if (addonName.Equals(MannequinAddonNameValue, StringComparison.Ordinal) ||
            addonName.Contains("Mannequin", StringComparison.OrdinalIgnoreCase))
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
            if (text.Contains("服装模特", StringComparison.Ordinal) ||
                text.Contains("模特商店", StringComparison.Ordinal) ||
                text.Contains("商店设定", StringComparison.Ordinal) ||
                text.Contains("Mannequin", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Shop Settings", StringComparison.OrdinalIgnoreCase))
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

    private static bool IsDiagnosticCandidate(string addonName, string text)
    {
        return addonName.Contains("Mannequin", StringComparison.OrdinalIgnoreCase) ||
               addonName.Contains("Housing", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("服装", StringComparison.Ordinal) ||
               text.Contains("模特", StringComparison.Ordinal) ||
               text.Contains("商店", StringComparison.Ordinal) ||
               text.Contains("Mannequin", StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe string GetNodeTextSummary(AtkResNode* node)
    {
        var texts = new List<string>();
        CollectNodeText(node, texts, 0);
        return texts.Count == 0 ? "<none>" : string.Join(" | ", texts);
    }

    private static unsafe void CollectNodeText(AtkResNode* node, List<string> texts, int depth)
    {
        if (node == null || depth > 24 || texts.Count >= 20)
        {
            return;
        }

        if (node->Type == NodeType.Text)
        {
            var text = node->GetAsAtkTextNode()->NodeText.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text) && !texts.Contains(text, StringComparer.Ordinal))
            {
                texts.Add(text.Length > 80 ? text[..80] : text);
            }
        }

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
        {
            CollectNodeText(child, texts, depth + 1);
        }
    }
}

public enum RestockItemSource
{
    Missing,
    PlayerInventory,
    RetainerInventory,
}

public sealed record RestockItemPlan(MannequinItem Item, RestockItemSource Source);
