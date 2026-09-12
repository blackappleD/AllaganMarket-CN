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
    private const long CaptureRetryIntervalMilliseconds = 500;

    // AgentMerchantSettingInfo availability value observed for sold-out slots
    // (0 = empty, 1 = listed, 2 = sold out).
    private const byte AvailabilitySoldOut = 2;

    // Native dialogs that open above MerchantSetting; the overlay hides while
    // any of them is visible so it never covers UI the user is interacting with.
    private static readonly string[] OverlayObstructingAddons =
    [
        "MerchantEquipSelect",
        "RetainerSell",
        "SelectYesno",
        "ContextMenu",
        "ItemSearchResult",
    ];

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
    private long lastCaptureAttemptMilliseconds;
    private string? lastCaptureSignature;
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
        var restockedCount = 0;
        try
        {
            foreach (var plan in execution)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemName = this.GetItemName(plan.Item.ItemId);
                if (plan.Source == RestockItemSource.Missing)
                {
                    this.pluginLog.Warning("[MannequinRestock] skipping slot={Slot}; item={ItemId}; source=missing.", plan.Item.EquipmentSlot, plan.Item.ItemId);
                    continue;
                }

                if (plan.Item.UnitPrice == 0)
                {
                    this.StatusMessage = $"跳过 {itemName}：售出前未记录价格，请手动上架一次以记录。";
                    this.pluginLog.Warning(
                        "[MannequinRestock] skipping slot={Slot}; item={ItemId}; reason=unknown-price.",
                        plan.Item.EquipmentSlot,
                        plan.Item.ItemId);
                    this.StateChanged?.Invoke();
                    continue;
                }

                if (plan.Source == RestockItemSource.RetainerInventory)
                {
                    this.StatusMessage = $"{itemName} 在雇员中；请先在传唤铃打开该雇员并取出装备。";
                    this.pluginLog.Warning(
                        "[MannequinRestock] slot={Slot}; item={ItemId}; source=retainer; automatic bell withdrawal is not available in this build.",
                        plan.Item.EquipmentSlot,
                        plan.Item.ItemId);
                    this.StateChanged?.Invoke();
                    continue;
                }

                this.StatusMessage = $"正在补货：{itemName}。";
                this.StateChanged?.Invoke();
                await this.RestockPlayerInventoryItemAsync(plan.Item, cancellationToken);
                restockedCount++;
            }

            this.StatusMessage = restockedCount > 0
                ? $"补货完成：已重新上架 {restockedCount}/{execution.Count} 个装备。"
                : "补货结束：没有可以重新上架的装备（缺失、价格未知或在雇员中）。";
            this.pluginLog.Information(
                "[MannequinRestock] execution completed; restocked={Restocked}; planned={Count}.",
                restockedCount,
                execution.Count);
            await Task.Delay(250, cancellationToken);
            if (this.IsMannequinWindowVisible)
            {
                // Agent memory and the configuration dictionary must only be
                // touched on the framework thread.
                await this.framework.RunOnFrameworkThread(() => this.TryCaptureCurrentConfiguration());
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
        this.lastCaptureAttemptMilliseconds = Environment.TickCount64;
        try
        {
            var agentInfo = AgentMerchantSettingInfo.Instance();
            if (agentInfo == null)
            {
                this.pluginLog.Warning("[MannequinDiag] MerchantSetting agent data is not available.");
                return false;
            }

            var mannequinId = this.GetCurrentMannequinId();
            this.configuration.MannequinConfigurations.TryGetValue(mannequinId, out var saved);
            var captured = new MannequinConfiguration
            {
                MannequinId = mannequinId,
                RetainerId = this.retainerService.RetainerId,
            };

            var hasUnknownPrices = false;
            var itemIndex = 0;
            foreach (var item in agentInfo->ItemsSpan)
            {
                if (item.ItemId != 0)
                {
                    var capturedItem = new MannequinItem
                    {
                        EquipmentSlot = itemIndex,
                        ItemId = item.ItemId,
                        IsHighQuality = item.IsHighQuality,
                        UnitPrice = item.Price > 0 ? (uint)Math.Min(item.Price, uint.MaxValue) : 0,
                        IsSoldOut = item.Availability == AvailabilitySoldOut,
                    };

                    // Sold-out slots lose the HQ flag and the price in the agent data,
                    // so restore them from the configuration saved while the item was listed.
                    if (capturedItem.IsSoldOut)
                    {
                        var savedItem = saved?.Items.Find(
                            existing => existing.EquipmentSlot == capturedItem.EquipmentSlot &&
                                        existing.ItemId == capturedItem.ItemId);
                        if (savedItem != null)
                        {
                            capturedItem.IsHighQuality = savedItem.IsHighQuality;
                            capturedItem.UnitPrice = savedItem.UnitPrice;
                        }

                        if (capturedItem.UnitPrice == 0)
                        {
                            hasUnknownPrices = true;
                        }
                    }

                    captured.Items.Add(capturedItem);
                }

                itemIndex++;
            }

            var signature = $"{mannequinId};" + string.Join(
                ",",
                captured.Items.Select(item => $"{item.EquipmentSlot}:{item.ItemId}:{item.IsHighQuality}:{item.UnitPrice}:{item.IsSoldOut}"));
            if (signature == this.lastCaptureSignature && this.CurrentConfiguration != null)
            {
                return captured.Items.Count > 0;
            }

            this.lastCaptureSignature = signature;
            foreach (var item in captured.Items)
            {
                this.pluginLog.Information(
                    "[MannequinDiag] slot={Slot}; itemId={ItemId}; hq={HighQuality}; price={Price}; soldOut={SoldOut}.",
                    item.EquipmentSlot,
                    item.ItemId,
                    item.IsHighQuality,
                    item.UnitPrice,
                    item.IsSoldOut);
            }

            this.CurrentConfiguration = captured;
            if (captured.Items.Count > 0 && mannequinId != 0 && !this.IsRestocking)
            {
                // Remember prices and HQ flags while items are still listed so
                // sold-out slots can be restocked after the agent data loses them.
                // Keep previously saved slots that are currently absent (e.g. an
                // entry being replaced) so their price records survive. Items are
                // cloned so the saved snapshot never aliases CurrentConfiguration.
                var toSave = new MannequinConfiguration
                {
                    MannequinId = captured.MannequinId,
                    RetainerId = captured.RetainerId,
                    Items = captured.Items.Select(CloneItem).ToList(),
                };
                if (saved != null)
                {
                    toSave.Items.AddRange(
                        saved.Items
                            .Where(existing => captured.Items.All(current => current.EquipmentSlot != existing.EquipmentSlot))
                            .Select(CloneItem));
                }

                this.configuration.MannequinConfigurations[mannequinId] = toSave;
                this.configuration.IsDirty = true;
            }

            var soldOutCount = captured.Items.Count(item => item.IsSoldOut);
            this.StatusMessage = captured.Items.Count == 0
                ? "已读取模特配置：没有检测到装备。"
                : hasUnknownPrices
                    ? $"已读取模特配置：{captured.Items.Count} 个槽位，{soldOutCount} 个售罄；部分售罄装备价格未知（售出前未记录），将跳过。"
                    : $"已读取模特配置：{captured.Items.Count} 个槽位，{soldOutCount} 个售罄。";
            this.pluginLog.Information(
                "[MannequinDiag] captured mannequin configuration; mannequinId={MannequinId}; items={ItemCount}; soldOut={SoldOutCount}.",
                captured.MannequinId,
                captured.Items.Count,
                soldOutCount);
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

    private static MannequinItem CloneItem(MannequinItem item)
    {
        return new MannequinItem
        {
            EquipmentSlot = item.EquipmentSlot,
            ItemId = item.ItemId,
            IsHighQuality = item.IsHighQuality,
            UnitPrice = item.UnitPrice,
            IsSoldOut = item.IsSoldOut,
        };
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
            .Select(item => new RestockItemPlan(item, this.ResolveRestockSource(item)))
            .ToArray();
    }

    public string GetItemName(uint itemId)
    {
        return this.itemSheet.TryGetRow(itemId, out var item) ? item.Name.ToString() : $"物品 {itemId}";
    }

    public bool IsOverlayObstructed()
    {
        foreach (var addonName in OverlayObstructingAddons)
        {
            if (this.IsAddonReadyOnFrameworkThread(addonName))
            {
                return true;
            }
        }

        return false;
    }

    private unsafe RestockItemSource ResolveRestockSource(MannequinItem item)
    {
        if (this.FindPlayerInventoryItem(item, true) != null)
        {
            return RestockItemSource.PlayerInventory;
        }

        if (this.FindRetainerInventoryItem(item, true) != null)
        {
            return RestockItemSource.RetainerInventory;
        }

        // Fall back to ignoring the HQ flag only when the slot data was fully
        // recovered (price known); otherwise the item is skipped anyway and a
        // wrong-quality match would just mislabel it as restockable.
        if (item.UnitPrice != 0)
        {
            if (this.FindPlayerInventoryItem(item, false) != null)
            {
                return RestockItemSource.PlayerInventory;
            }

            if (this.FindRetainerInventoryItem(item, false) != null)
            {
                return RestockItemSource.RetainerInventory;
            }
        }

        return RestockItemSource.Missing;
    }

    private unsafe InventoryItem* FindPlayerInventoryItem(MannequinItem item, bool matchQuality)
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
                    (!matchQuality ||
                     inventoryItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == item.IsHighQuality))
                {
                    return inventoryItem;
                }
            }
        }

        return null;
    }

    private unsafe InventoryItem* FindRetainerInventoryItem(MannequinItem item, bool matchQuality)
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
                    (!matchQuality ||
                     inventoryItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == item.IsHighQuality))
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

            if (type == AddonEvent.PostRefresh)
            {
                // The native window refreshes when its content changes (e.g. an
                // item is removed or listed), so recapture the slot data.
                this.TryCaptureCurrentConfiguration();
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
                this.lastCaptureSignature = null;
                this.StatusMessage = "等待打开服装模特商店设定。";
                this.StateChanged?.Invoke();
            }

            return;
        }

        if (addonAddress.Address == this.MannequinAddonAddress && this.IsMannequinWindowVisible)
        {
            // The agent data can lag behind the addon for a few frames, so keep
            // retrying until at least one slot is captured.
            if ((this.CurrentConfiguration == null || this.CurrentConfiguration.Items.Count == 0) &&
                Environment.TickCount64 - this.lastCaptureAttemptMilliseconds >= CaptureRetryIntervalMilliseconds)
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
        this.lastCaptureSignature = null;
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
