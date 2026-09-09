using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AllaganMarket.Agents;
using AllaganMarket.Models;
using AllaganMarket.Services.Interfaces;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

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
    private readonly ITargetManager targetManager;
    private readonly IInventoryService inventoryService;
    private readonly IRetainerService retainerService;
    private readonly IPluginLog pluginLog;
    private readonly Configuration configuration;
    private readonly ExcelSheet<Item> itemSheet;
    private readonly Dictionary<nint, string> lastAddonDiagnosticStates = [];
    private readonly Dictionary<nint, string> recentAddonDiagnostics = [];
    private long lastDiagnosticMilliseconds;
    private CancellationTokenSource? restockCancellationTokenSource;
    private List<RestockItemPlan>? restockExecution;

    public MannequinRestockService(
        IAddonLifecycle addonLifecycle,
        IFramework framework,
        IGameGui gameGui,
        ITargetManager targetManager,
        IInventoryService inventoryService,
        IRetainerService retainerService,
        IPluginLog pluginLog,
        Configuration configuration,
        ExcelSheet<Item> itemSheet)
    {
        this.addonLifecycle = addonLifecycle;
        this.framework = framework;
        this.gameGui = gameGui;
        this.targetManager = targetManager;
        this.inventoryService = inventoryService;
        this.retainerService = retainerService;
        this.pluginLog = pluginLog;
        this.configuration = configuration;
        this.itemSheet = itemSheet;
    }

    public bool IsMannequinWindowVisible { get; private set; }

    public string? MannequinAddonName { get; private set; }

    public nint MannequinAddonAddress { get; private set; }

    public MannequinConfiguration? CurrentConfiguration { get; private set; }

    public string StatusMessage { get; private set; } = "等待打开服装模特商店设定。";

    public bool IsRestocking => this.restockExecution != null;

    public event System.Action? StateChanged;

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
        this.restockCancellationTokenSource?.Cancel();
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
        if (this.restockExecution != null)
        {
            this.pluginLog.Information("Mannequin restock is already running; ignoring duplicate click.");
            return;
        }

        if (!this.IsMannequinWindowVisible)
        {
            this.StatusMessage = "请先打开服装模特商店设定。";
            this.pluginLog.Warning("Cannot start mannequin restock because no supported mannequin window is active.");
            this.StateChanged?.Invoke();
            return;
        }

        if (this.CurrentConfiguration == null || this.CurrentConfiguration.Items.Count == 0)
        {
            this.TryCaptureCurrentConfiguration();
        }

        if (this.CurrentConfiguration == null || this.CurrentConfiguration.Items.Count == 0)
        {
            this.StatusMessage = "已点击补货，但未读取到模特装备数据；请查看 MerchantSetting 槽位日志。";
            this.pluginLog.Information(
                "Mannequin restock clicked, but no mannequin item data is available; addon={AddonName}; address=0x{Address:X}.",
                this.MannequinAddonName ?? MannequinAddonNameValue,
                this.MannequinAddonAddress);
            this.LogKnownMannequinState("restock-click");
            this.StateChanged?.Invoke();
            return;
        }

        var plan = this.BuildRestockPlan(this.CurrentConfiguration);
        this.pluginLog.Information(
            "Mannequin restock requested for {Count} items; {Missing} items are unavailable.",
            plan.Count,
            plan.Count(item => item.Source == RestockItemSource.Missing));
        if (plan.Count == 0)
        {
            this.StatusMessage = "当前没有检测到售罄装备。";
            this.StateChanged?.Invoke();
            return;
        }

        this.restockCancellationTokenSource = new CancellationTokenSource();
        this.restockExecution = plan.ToList();
        this.StatusMessage = $"开始补货：{plan.Count} 个售罄装备。";
        this.StateChanged?.Invoke();
        _ = this.ExecuteRestockAsync(this.restockExecution, this.restockCancellationTokenSource.Token);
    }

    private async Task ExecuteRestockAsync(List<RestockItemPlan> execution, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var plan in execution)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (plan.Source == RestockItemSource.Missing)
                {
                    this.pluginLog.Warning("[MannequinRestock] skipping slot={Slot}; item={ItemId}; source=missing.", plan.Item.EquipmentSlot, plan.Item.ItemId);
                    continue;
                }

                if (plan.Source == RestockItemSource.RetainerInventory)
                {
                    this.StatusMessage = $"部位 {plan.Item.EquipmentSlot} 的装备在雇员中；请先在传唤铃打开该雇员并取出装备。";
                    this.pluginLog.Warning(
                        "[MannequinRestock] slot={Slot}; item={ItemId}; source=retainer; automatic bell withdrawal is not available in this build.",
                        plan.Item.EquipmentSlot,
                        plan.Item.ItemId);
                    this.StateChanged?.Invoke();
                    continue;
                }

                this.StatusMessage = $"正在补货：部位 {plan.Item.EquipmentSlot}。";
                this.StateChanged?.Invoke();
                await this.RestockPlayerInventoryItemAsync(plan.Item, cancellationToken);
            }

            this.StatusMessage = "补货流程已完成，请确认模特槽位状态。";
            this.pluginLog.Information("[MannequinRestock] execution completed; items={Count}.", execution.Count);
            await Task.Delay(250, cancellationToken);
            if (this.IsMannequinWindowVisible)
            {
                this.TryCaptureCurrentConfiguration();
            }
        }
        catch (OperationCanceledException)
        {
            this.StatusMessage = "补货流程已取消。";
            this.pluginLog.Information("[MannequinRestock] execution cancelled.");
        }
        catch (Exception exception)
        {
            this.StatusMessage = "补货流程中断，请查看日志。";
            this.pluginLog.Error(exception, "[MannequinRestock] execution failed.");
        }
        finally
        {
            this.restockExecution = null;
            this.restockCancellationTokenSource?.Dispose();
            this.restockCancellationTokenSource = null;
            this.StateChanged?.Invoke();
        }
    }

    private async Task RestockPlayerInventoryItemAsync(MannequinItem item, CancellationToken cancellationToken)
    {
        await this.WaitForAddonAsync(MannequinAddonNameValue, cancellationToken);
        this.pluginLog.Information("[MannequinRestock] state=remove-sold-out; slot={Slot}.", item.EquipmentSlot);
        await this.FireCallbackAsync(MannequinAddonNameValue, 13, cancellationToken, item.EquipmentSlot);

        if (await this.WaitForAddonAsync("ContextMenu", cancellationToken, 10, false))
        {
            this.pluginLog.Information("[MannequinRestock] state=remove-context-menu; slot={Slot}.", item.EquipmentSlot);
            await this.FireCallbackAsync("ContextMenu", 0, cancellationToken, 0, 0);
            if (await this.WaitForAddonAsync("SelectYesno", cancellationToken, 10, false))
            {
                await this.FireCallbackAsync("SelectYesno", 0, cancellationToken);
            }
        }

        await this.WaitUntilAddonGoneAsync("ContextMenu", cancellationToken);
        await this.FireCallbackAsync(MannequinAddonNameValue, 12, cancellationToken, item.EquipmentSlot);
        await this.WaitForAddonAsync("MerchantEquipSelect", cancellationToken);

        var callback = await this.FindEquipmentCallbackAsync(item, cancellationToken);
        if (callback < 0)
        {
            throw new InvalidOperationException($"未在 MerchantEquipSelect 找到物品 {item.ItemId} (HQ={item.IsHighQuality})。");
        }

        this.pluginLog.Information(
            "[MannequinRestock] state=select-equipment; slot={Slot}; item={ItemId}; callback={Callback}.",
            item.EquipmentSlot,
            item.ItemId,
            callback);
        await this.FireCallbackAsync("MerchantEquipSelect", 19, cancellationToken, callback);
        await this.WaitForAddonAsync("RetainerSell", cancellationToken);
        await this.FireCallbackAsync("RetainerSell", 2, cancellationToken, (int)item.UnitPrice);
        await this.FireCallbackAsync("RetainerSell", 0, cancellationToken);
        await this.WaitUntilAddonGoneAsync("RetainerSell", cancellationToken);
        await Task.Delay(250, cancellationToken);
    }

    private async Task<int> FindEquipmentCallbackAsync(MannequinItem item, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callback = await this.framework.RunOnFrameworkThread(() => this.FindEquipmentCallback(item));
            if (callback >= 0)
            {
                return callback;
            }

            await Task.Delay(100, cancellationToken);
        }

        return -1;
    }

    private unsafe int FindEquipmentCallback(MannequinItem item)
    {
        var pointer = this.gameGui.GetAddonByName("MerchantEquipSelect");
        if (pointer == IntPtr.Zero)
        {
            return -1;
        }

        var addon = (AtkUnitBase*)pointer.Address;
        if (!this.IsReady(addon) || addon->RootNode == null)
        {
            return -1;
        }

        var nodes = new List<int> { 4 };
        for (var index = 41001; index < 41051; index++)
        {
            nodes.Add(index);
        }

        foreach (var nodeIndex in nodes)
        {
            var node = GetNodeByIdChain(addon->RootNode, 1, 8, 13, nodeIndex, 3);
            if (node == null || node->Type != NodeType.Text)
            {
                continue;
            }

            var text = node->GetAsAtkTextNode()->NodeText.ToString();
            if (!this.itemSheet.TryGetRow(item.ItemId, out var sheetItem) || !text.Contains(sheetItem.Name.ToString(), StringComparison.Ordinal))
            {
                continue;
            }

            return nodeIndex == 4 ? 0 : nodeIndex - 41000;
        }

        return -1;
    }

    private async Task FireCallbackAsync(string addonName, int callbackIndex, CancellationToken cancellationToken, params int[] arguments)
    {
        await this.framework.RunOnFrameworkThread(() =>
        {
            var pointer = this.gameGui.GetAddonByName(addonName);
            if (pointer == IntPtr.Zero)
            {
                throw new InvalidOperationException($"窗口 {addonName} 不存在，无法执行 callback {callbackIndex}。");
            }

            unsafe
            {
                var addon = (AtkUnitBase*)pointer.Address;
                if (!this.IsReady(addon))
                {
                    throw new InvalidOperationException($"窗口 {addonName} 尚未就绪，无法执行 callback {callbackIndex}。");
                }

                var values = stackalloc AtkValue[arguments.Length];
                for (var index = 0; index < arguments.Length; index++)
                {
                    values[index] = new AtkValue { Type = AtkValueType.Int, Int = arguments[index] };
                }

                addon->FireCallback((uint)callbackIndex, values, true);
            }
        });
        await Task.Delay(100, cancellationToken);
    }

    private async Task<bool> WaitForAddonAsync(string addonName, CancellationToken cancellationToken, int attempts = 40, bool throwOnTimeout = true)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = await this.IsAddonReadyAsync(addonName);
            if (ready)
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        if (throwOnTimeout)
        {
            throw new TimeoutException($"等待窗口 {addonName} 超时。");
        }

        return false;
    }

    private async Task WaitUntilAddonGoneAsync(string addonName, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = await this.IsAddonReadyAsync(addonName);
            if (!ready)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    private Task<bool> IsAddonReadyAsync(string addonName)
    {
        return this.framework.RunOnFrameworkThread(() => this.IsAddonReadyOnFrameworkThread(addonName));
    }

    private unsafe bool IsAddonReadyOnFrameworkThread(string addonName)
    {
        var pointer = this.gameGui.GetAddonByName(addonName);
        return pointer != IntPtr.Zero && this.IsReady((AtkUnitBase*)pointer.Address);
    }

    private static unsafe AtkResNode* GetNodeByIdChain(AtkResNode* root, params int[] ids)
    {
        var current = root;
        foreach (var id in ids)
        {
            current = FindChildById(current, (uint)id);
            if (current == null)
            {
                return null;
            }
        }

        return current;
    }

    private static unsafe AtkResNode* FindChildById(AtkResNode* root, uint id)
    {
        if (root == null)
        {
            return null;
        }

        for (var node = root->ChildNode; node != null; node = node->NextSiblingNode)
        {
            if (node->NodeId == id)
            {
                return node;
            }

            var descendant = FindChildById(node, id);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }

    private unsafe bool IsReady(AtkUnitBase* addon)
    {
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private unsafe bool TryCaptureCurrentConfiguration()
    {
        try
        {
            var agentInfo = AgentMerchantSettingInfo.Instance();
            if (agentInfo == null)
            {
                this.pluginLog.Warning("[MannequinDiag] MerchantSetting agent data is not available.");
                return false;
            }

            var mannequinId = this.GetCurrentMannequinId();
            var captured = new MannequinConfiguration
            {
                MannequinId = mannequinId,
                RetainerId = this.retainerService.RetainerId,
            };

            var itemIndex = 0;
            foreach (var item in agentInfo->ItemsSpan)
            {
                this.pluginLog.Information(
                    "[MannequinDiag] slot={Slot}; itemId={ItemId}; itemIdWithQuality={ItemIdWithQuality}; hq={HighQuality}; price={Price}; availability={Availability}; color1={Color1}; color2={Color2}.",
                    itemIndex,
                    item.ItemId,
                    item.ItemIdWithQuality,
                    item.IsHighQuality,
                    item.Price,
                    item.Availability,
                    item.Color1,
                    item.Color2);

                if (item.ItemId != 0)
                {
                    captured.Items.Add(new MannequinItem
                    {
                        EquipmentSlot = itemIndex,
                        ItemId = item.ItemId,
                        IsHighQuality = item.IsHighQuality,
                        UnitPrice = item.Price > 0 ? (uint)Math.Min(item.Price, uint.MaxValue) : 0,
                        IsSoldOut = item.Availability == 2,
                    });
                }

                itemIndex++;
            }

            this.CurrentConfiguration = captured;
            this.StatusMessage = $"已读取模特配置：{captured.Items.Count}/12 个槽位。";
            this.pluginLog.Information(
                "[MannequinDiag] captured mannequin configuration; mannequinId={MannequinId}; selectedItems=0x{SelectedItems:X8}; items={ItemCount}.",
                captured.MannequinId,
                agentInfo->SelectedItems,
                captured.Items.Count);
            this.StateChanged?.Invoke();
            return captured.Items.Count > 0;
        }
        catch (Exception exception)
        {
            this.pluginLog.Error(exception, "[MannequinDiag] failed to capture MerchantSetting agent data.");
            return false;
        }
    }

    private ulong GetCurrentMannequinId()
    {
        return this.targetManager.Target?.GameObjectId ?? (ulong)this.MannequinAddonAddress;
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
                this.restockCancellationTokenSource?.Cancel();
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
            if (this.CurrentConfiguration == null)
            {
                this.TryCaptureCurrentConfiguration();
            }

            return;
        }

        this.IsMannequinWindowVisible = true;
        this.MannequinAddonName = MannequinAddonNameValue;
        this.MannequinAddonAddress = addonAddress.Address;
        this.CurrentConfiguration = null;
        this.StatusMessage = $"已识别模特窗口：{MannequinAddonNameValue}。等待配置采集。";
        this.LogAddonSummary(addon);
        this.TryCaptureCurrentConfiguration();
        this.StateChanged?.Invoke();
    }

    private unsafe void OnAnyAddonFinalized(AddonEvent type, AddonArgs args)
    {
        if (args.Addon == IntPtr.Zero || args.Addon != this.MannequinAddonAddress)
        {
            return;
        }

        this.restockCancellationTokenSource?.Cancel();
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
