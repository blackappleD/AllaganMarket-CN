using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AllaganMarket.GameInterop;
using AllaganMarket.Services.Interfaces;

using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Microsoft.Extensions.Hosting;

namespace AllaganMarket.Services;

/// <summary>
/// Lists inventory stacks on the retainer market in bulk through addon
/// callbacks, mirroring the manual "right click -> Put Up for Sale -> set
/// price -> confirm" flow. No OS mouse or keyboard input is generated.
/// </summary>
public sealed class AutoListingService : IHostedService, IDisposable
{
    private const int MaxMarketSlots = 20;

    private static readonly InventoryType[] PlayerInventoryTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    // The player inventory addon variants; whichever is ready owns the
    // programmatic context menu so the game shows the retainer sell entries.
    private static readonly string[] InventoryAddonNames =
    [
        "InventoryExpansion",
        "InventoryLarge",
        "Inventory",
    ];

    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog pluginLog;
    private readonly ICharacterMonitorService characterMonitorService;
    private readonly IInventoryService inventoryService;
    private readonly MarketPriceUpdaterService marketPriceUpdaterService;
    private readonly UndercutService undercutService;
    private readonly AutoUndercutService autoUndercutService;
    private readonly MannequinRestockService mannequinRestockService;

    private CancellationTokenSource? cancellationTokenSource;
    private int marketBoardRetryRequested;
    private long lastProcessedMarketItemId = -1;

    public AutoListingService(
        IFramework framework,
        IGameGui gameGui,
        IPluginLog pluginLog,
        ICharacterMonitorService characterMonitorService,
        IInventoryService inventoryService,
        MarketPriceUpdaterService marketPriceUpdaterService,
        UndercutService undercutService,
        AutoUndercutService autoUndercutService,
        MannequinRestockService mannequinRestockService)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.pluginLog = pluginLog;
        this.characterMonitorService = characterMonitorService;
        this.inventoryService = inventoryService;
        this.marketPriceUpdaterService = marketPriceUpdaterService;
        this.undercutService = undercutService;
        this.autoUndercutService = autoUndercutService;
        this.mannequinRestockService = mannequinRestockService;
    }

    public bool IsRunning { get; private set; }

    public string StatusMessage { get; private set; } = string.Empty;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.marketPriceUpdaterService.MarketBoardItemRequestReceived += this.MarketBoardItemRequestReceived;
        this.undercutService.MarketOfferingsProcessed += this.OnMarketOfferingsProcessed;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.cancellationTokenSource?.Cancel();
        this.marketPriceUpdaterService.MarketBoardItemRequestReceived -= this.MarketBoardItemRequestReceived;
        this.undercutService.MarketOfferingsProcessed -= this.OnMarketOfferingsProcessed;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        this.cancellationTokenSource?.Cancel();
        this.cancellationTokenSource?.Dispose();
    }

    public void Start(uint itemId, bool isHq, int stackCount, bool refreshMarketPrice)
    {
        if (this.IsRunning || !this.CheckNoOtherAutomation())
        {
            return;
        }

        var activeRetainer = this.characterMonitorService.ActiveRetainer;
        if (activeRetainer == null)
        {
            this.StatusMessage = "请先打开雇员的出售品列表。";
            return;
        }

        stackCount = Math.Clamp(stackCount, 1, MaxMarketSlots);
        this.BeginRun($"开始批量上架：{stackCount} 组。");
        this.pluginLog.Information(
            $"Batch listing: item {itemId} ({(isHq ? "HQ" : "NQ")}), {stackCount} stack(s), refreshMarketPrice={refreshMarketPrice}.");
        _ = this.RunAsync(activeRetainer.WorldId, itemId, isHq, stackCount, refreshMarketPrice, this.cancellationTokenSource!.Token);
    }

    public void StartRetractAll(bool returnToRetainer, int count)
    {
        if (this.IsRunning || !this.CheckNoOtherAutomation())
        {
            return;
        }

        if (this.characterMonitorService.ActiveRetainer == null)
        {
            this.StatusMessage = "请先打开雇员的出售品列表。";
            return;
        }

        count = Math.Clamp(count, 1, MaxMarketSlots);
        this.BeginRun(returnToRetainer ? $"开始批量收回给雇员：{count} 件。" : $"开始批量收回给自己：{count} 件。");
        this.pluginLog.Information($"Batch retract: returnToRetainer={returnToRetainer}, count={count}.");
        _ = this.RunRetractAsync(returnToRetainer, count, this.cancellationTokenSource!.Token);
    }

    public void Cancel()
    {
        this.cancellationTokenSource?.Cancel();
    }

    // The auto-undercut and mannequin flows drive the same RetainerSell and
    // ContextMenu addons; two loops firing callbacks at once would race.
    private bool CheckNoOtherAutomation()
    {
        if (this.autoUndercutService.IsRunning)
        {
            this.StatusMessage = "自动压价正在运行，请等待其完成。";
            return false;
        }

        if (this.mannequinRestockService.IsRestocking)
        {
            this.StatusMessage = "人偶补货正在运行，请等待其完成。";
            return false;
        }

        return true;
    }

    private void BeginRun(string statusMessage)
    {
        this.cancellationTokenSource?.Cancel();
        this.cancellationTokenSource?.Dispose();
        this.cancellationTokenSource = new CancellationTokenSource();
        this.IsRunning = true;
        this.StatusMessage = statusMessage;
    }

    private async Task RunAsync(
        uint worldId,
        uint itemId,
        bool isHq,
        int stackCount,
        bool refreshMarketPrice,
        CancellationToken cancellationToken)
    {
        var listed = 0;
        try
        {
            uint? unitPrice = null;
            for (var index = 0; index < stackCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!await this.WaitForAddon("RetainerSellList", cancellationToken, 10))
                {
                    this.StatusMessage = $"出售品列表已关闭，批量上架中止（已上架 {listed}/{stackCount} 组）。";
                    return;
                }

                if (!await this.HasFreeMarketSlot())
                {
                    this.StatusMessage = $"批量上架结束：已上架 {listed}/{stackCount} 组，出售槽位已满。";
                    return;
                }

                var inventorySlot = await this.FindInventoryStack(itemId, isHq);
                if (inventorySlot == null)
                {
                    this.StatusMessage = $"批量上架结束：已上架 {listed}/{stackCount} 组，背包已无该物品。";
                    return;
                }

                if (!await this.OpenInventoryContextMenu(inventorySlot.Value.Type, inventorySlot.Value.Slot) ||
                    !await this.WaitForAddon("ContextMenu", cancellationToken, 20))
                {
                    this.StatusMessage = $"无法打开物品右键菜单（已上架 {listed}/{stackCount} 组）。";
                    return;
                }

                if (!await this.SelectPutUpForSale())
                {
                    this.StatusMessage = $"右键菜单中没有“出售”选项（已上架 {listed}/{stackCount} 组）。";
                    return;
                }

                if (!await this.WaitForAddon("RetainerSell", cancellationToken))
                {
                    this.StatusMessage = $"出售窗口未打开（已上架 {listed}/{stackCount} 组）。";
                    return;
                }

                if (unitPrice == null)
                {
                    if (refreshMarketPrice)
                    {
                        this.StatusMessage = "正在查询市场时价……";
                        unitPrice = await this.QueryRecommendedPrice(worldId, itemId, isHq, cancellationToken);
                    }

                    unitPrice ??= this.undercutService.GetRecommendedUnitPrice(worldId, itemId, isHq, 1, false)?.Amount;
                    if (unitPrice == null)
                    {
                        await this.CloseAddon("RetainerSell", cancellationToken);
                        this.StatusMessage = "无法获取推荐价格；请先在市场查询一次该物品的时价。";
                        return;
                    }
                }

                // Setting the price only updates the numeric input; abort if it
                // fails so the stack is never confirmed at a stale or zero price.
                if (!await this.SetRetainerSellPrice(unitPrice.Value))
                {
                    await this.CloseAddon("RetainerSell", cancellationToken);
                    this.StatusMessage = $"设置价格失败，批量上架中止（已上架 {listed}/{stackCount} 组）。";
                    return;
                }

                // Callback 0 is the confirm button; quantity already defaults
                // to the full stack when listing from the inventory.
                if (!await this.ClickRetainerSellCallback(0))
                {
                    await this.CloseAddon("RetainerSell", cancellationToken);
                    this.StatusMessage = $"确认上架失败，批量上架中止（已上架 {listed}/{stackCount} 组）。";
                    return;
                }

                await this.WaitUntilAddonGone("RetainerSell", cancellationToken);

                listed++;
                this.StatusMessage = $"已上架 {listed}/{stackCount} 组，单价 {unitPrice.Value}。";
                this.pluginLog.Information($"Batch listing: listed stack {listed}/{stackCount} of item {itemId} at {unitPrice.Value}.");
                await Task.Delay(400, cancellationToken);
            }

            this.StatusMessage = $"批量上架完成：{listed}/{stackCount} 组。";
        }
        catch (OperationCanceledException)
        {
            this.StatusMessage = $"批量上架已取消（已上架 {listed} 组）。";
            this.pluginLog.Information("Batch listing cancelled.");
        }
        catch (Exception ex)
        {
            this.StatusMessage = $"批量上架失败（已上架 {listed} 组），请查看日志。";
            this.pluginLog.Error($"Batch listing failed: {ex}");
        }
        finally
        {
            this.IsRunning = false;
        }
    }

    private async Task RunRetractAsync(bool returnToRetainer, int count, CancellationToken cancellationToken)
    {
        var retracted = 0;
        var target = returnToRetainer ? "雇员" : "自己";
        try
        {
            while (retracted < count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!await this.WaitForAddon("RetainerSellList", cancellationToken, 10))
                {
                    this.StatusMessage = $"出售品列表已关闭，批量收回中止（已收回 {retracted}/{count} 件）。";
                    return;
                }

                var remaining = await this.CountMarketItems();
                if (remaining == 0)
                {
                    this.StatusMessage = $"批量收回结束：已收回 {retracted}/{count} 件，出售列表已空。";
                    return;
                }

                // Rows shift up after each removal, so always operate on row 0.
                if (!await this.SelectListedItem(0) ||
                    !await this.WaitForAddon("ContextMenu", cancellationToken, 20))
                {
                    this.StatusMessage = $"无法打开在售物品右键菜单（已收回 {retracted}/{count} 件）。";
                    return;
                }

                var entrySelected = returnToRetainer
                    ? await this.SelectContextMenuEntry(["收回给雇员"], ["Return to Retainer"])
                    : await this.SelectContextMenuEntry(["收回给自己"], ["Return to Inventory", "Take Back"]);
                if (!entrySelected)
                {
                    this.StatusMessage = $"右键菜单中没有“收回给{target}”选项（已收回 {retracted}/{count} 件）。";
                    return;
                }

                // Wait until the listing actually disappears; if it does not
                // (for example the inventory is full), abort instead of firing
                // the same callback forever.
                var removed = false;
                for (var wait = 0; wait < 30; wait++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(100, cancellationToken);
                    if (await this.CountMarketItems() < remaining)
                    {
                        removed = true;
                        break;
                    }
                }

                if (!removed)
                {
                    this.StatusMessage = $"收回未生效，批量收回中止（已收回 {retracted}/{count} 件）；请检查背包或雇员的剩余空间。";
                    return;
                }

                retracted++;
                this.StatusMessage = $"已收回给{target} {retracted}/{count} 件……";
                this.pluginLog.Information($"Batch retract: removed listing {retracted}/{count}, returnToRetainer={returnToRetainer}.");
                await Task.Delay(300, cancellationToken);
            }

            this.StatusMessage = $"批量收回给{target}完成：{retracted}/{count} 件。";
        }
        catch (OperationCanceledException)
        {
            this.StatusMessage = $"批量收回已取消（已收回 {retracted}/{count} 件）。";
            this.pluginLog.Information("Batch retract cancelled.");
        }
        catch (Exception ex)
        {
            this.StatusMessage = $"批量收回失败（已收回 {retracted}/{count} 件），请查看日志。";
            this.pluginLog.Error($"Batch retract failed: {ex}");
        }
        finally
        {
            this.IsRunning = false;
        }
    }

    private Task<int> CountMarketItems()
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                var container = this.inventoryService.GetInventoryContainer(InventoryType.RetainerMarket);
                if (container == null || !container->IsLoaded)
                {
                    return 0;
                }

                var count = 0;
                for (var index = 0; index < container->Size; index++)
                {
                    if (container->Items[index].ItemId != 0)
                    {
                        count++;
                    }
                }

                return count;
            }
        });
    }

    private Task<bool> SelectListedItem(int rowIndex)
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            var pointer = this.gameGui.GetAddonByName("RetainerSellList");
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            unsafe
            {
                var addon = (AtkUnitBase*)pointer.Address;
                if (!this.IsReady(addon))
                {
                    return false;
                }

                var values = stackalloc AtkValue[3];
                values[0] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
                values[1] = new AtkValue { Type = AtkValueType.Int, Int = rowIndex };
                values[2] = new AtkValue { Type = AtkValueType.Int, Int = 1 };
                addon->FireCallback(3, values, true);
                return true;
            }
        });
    }

    private Task<bool> HasFreeMarketSlot()
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                var container = this.inventoryService.GetInventoryContainer(InventoryType.RetainerMarket);
                if (container == null || !container->IsLoaded)
                {
                    return false;
                }

                for (var index = 0; index < container->Size; index++)
                {
                    if (container->Items[index].ItemId == 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        });
    }

    private Task<(InventoryType Type, int Slot)?> FindInventoryStack(uint itemId, bool isHq)
    {
        return this.framework.RunOnFrameworkThread<(InventoryType Type, int Slot)?>(() =>
        {
            unsafe
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
                        var item = &container->Items[index];
                        if (item->ItemId == itemId &&
                            item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == isHq)
                        {
                            return (inventoryType, item->Slot);
                        }
                    }
                }

                return null;
            }
        });
    }

    private Task<bool> OpenInventoryContextMenu(InventoryType inventoryType, int slot)
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                var agent = AgentInventoryContext.Instance();
                if (agent == null)
                {
                    return false;
                }

                var ownerAddonId = 0u;
                foreach (var addonName in InventoryAddonNames)
                {
                    var pointer = this.gameGui.GetAddonByName(addonName);
                    if (pointer == IntPtr.Zero)
                    {
                        continue;
                    }

                    var addon = (AtkUnitBase*)pointer.Address;
                    if (addon->IsReady && addon->IsVisible)
                    {
                        ownerAddonId = addon->Id;
                        break;
                    }
                }

                agent->OpenForItemSlot(inventoryType, slot, 0, ownerAddonId);
                return true;
            }
        });
    }

    private Task<bool> SelectPutUpForSale()
    {
        return this.SelectContextMenuEntry(["到市场出售", "出售"], ["Put Up for Sale"]);
    }

    /// <summary>
    /// Selects a ContextMenu entry, trying exact label matches first and then
    /// case-insensitive substring matches, so short CN labels cannot
    /// accidentally match longer unrelated entries.
    /// </summary>
    private Task<bool> SelectContextMenuEntry(string[] exactLabels, string[] fuzzyLabels)
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                var pointer = this.gameGui.GetAddonByName("ContextMenu");
                if (pointer == IntPtr.Zero)
                {
                    return false;
                }

                var addon = (AddonContextMenu*)pointer.Address;
                if (!this.IsReady(&addon->AtkUnitBase))
                {
                    return false;
                }

                var list = addon->GetComponentListById(2);
                if (list == null)
                {
                    return false;
                }

                var labels = new List<string>();
                for (var index = 0; index < list->ListLength; index++)
                {
                    try
                    {
                        labels.Add(list->GetItemLabel(index).ToString().Trim());
                    }
                    catch
                    {
                        // Context menu text can be invalid while the menu is
                        // being rebuilt; skip that entry safely.
                        labels.Add(string.Empty);
                    }
                }

                var selectedIndex = labels.FindIndex(
                    label => exactLabels.Any(exact => label.Equals(exact, StringComparison.Ordinal)));
                if (selectedIndex < 0)
                {
                    selectedIndex = labels.FindIndex(
                        label => label.Length > 0 &&
                                 fuzzyLabels.Any(fuzzy => label.Contains(fuzzy, StringComparison.OrdinalIgnoreCase)));
                }

                if (selectedIndex < 0)
                {
                    this.pluginLog.Warning(
                        $"Batch listing: no entry matching [{string.Join(", ", exactLabels)}] in context menu; entries: {string.Join(" | ", labels)}.");
                    addon->AtkUnitBase.Close(true);
                    return false;
                }

                var values = stackalloc AtkValue[3];
                values[0] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
                values[1] = new AtkValue { Type = AtkValueType.Int, Int = selectedIndex };
                values[2] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
                addon->FireCallback(3, values, true);
                return true;
            }
        });
    }

    private async Task<uint?> QueryRecommendedPrice(uint worldId, uint itemId, bool isHq, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref this.marketBoardRetryRequested, 0);
            Interlocked.Exchange(ref this.lastProcessedMarketItemId, -1);

            // ComparePrices is the game's "view current market price" action.
            await this.ClickRetainerSellCallback(4);
            await Task.Delay(650, cancellationToken);

            uint? result = null;
            var comparePricesRetried = false;
            var offeringsProcessed = false;

            // Busy items stream their listings across several packets and the
            // market price cache is only written after the full batch arrives,
            // so this has to wait for the processed signal instead of sampling
            // the cache for a second or two.
            for (var wait = 0; wait < 100; wait++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref this.marketBoardRetryRequested) != 0)
                {
                    break;
                }

                offeringsProcessed = Interlocked.Read(ref this.lastProcessedMarketItemId) == itemId;
                if (offeringsProcessed)
                {
                    result = this.undercutService.GetRecommendedUnitPrice(worldId, itemId, isHq, 1, false)?.Amount;
                    break;
                }

                // The compare-prices callback can be dropped while RetainerSell
                // is still refreshing; issue it once more if the results window
                // never opened.
                if (!comparePricesRetried && wait == 20 && !await this.IsAddonReady("ItemSearchResult"))
                {
                    comparePricesRetried = true;
                    this.pluginLog.Warning(
                        $"Batch listing: the compare-prices window did not open for item {itemId}; clicking compare prices again.");
                    await this.ClickRetainerSellCallback(4);
                }

                await Task.Delay(100, cancellationToken);
            }

            if (Volatile.Read(ref this.marketBoardRetryRequested) != 0 && attempt < 2)
            {
                this.pluginLog.Warning(
                    "Batch listing: market board asked to retry later; closing result window and retrying in 2 seconds.");
                await this.CloseAddon("ItemSearchResult", cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }

            if (!offeringsProcessed)
            {
                this.pluginLog.Warning(
                    $"Batch listing: no market board data arrived for item {itemId} within 10 seconds; falling back to the cached price.");
            }

            await this.CloseAddon("ItemSearchResult", cancellationToken);
            return result ?? this.undercutService.GetRecommendedUnitPrice(worldId, itemId, isHq, 1, false)?.Amount;
        }

        return null;
    }

    private void OnMarketOfferingsProcessed(uint worldId, uint itemId)
    {
        if (this.IsRunning)
        {
            Interlocked.Exchange(ref this.lastProcessedMarketItemId, itemId);
        }
    }

    private void MarketBoardItemRequestReceived(MarketBoardItemRequest request)
    {
        if (this.IsRunning && request.Status == MarketPriceUpdaterService.RateLimitedStatus)
        {
            Interlocked.Exchange(ref this.marketBoardRetryRequested, 1);
        }
    }

    private Task<bool> ClickRetainerSellCallback(int callbackIndex)
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            var pointer = this.gameGui.GetAddonByName("RetainerSell");
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            unsafe
            {
                var addon = (AddonRetainerSell*)pointer.Address;
                if (!this.IsReady(&addon->AtkUnitBase))
                {
                    return false;
                }

                var value = new AtkValue { Type = AtkValueType.Int, Int = callbackIndex };
                addon->AtkUnitBase.FireCallback(1, &value, true);
                return true;
            }
        });
    }

    private Task<bool> SetRetainerSellPrice(uint price)
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            var pointer = this.gameGui.GetAddonByName("RetainerSell");
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            unsafe
            {
                var addon = (AddonRetainerSell*)pointer.Address;
                if (!this.IsReady(&addon->AtkUnitBase) || addon->AskingPrice == null)
                {
                    return false;
                }

                addon->AskingPrice->SetValue((int)price);
                return true;
            }
        });
    }

    private Task<bool> IsAddonReady(string addonName)
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            var pointer = this.gameGui.GetAddonByName(addonName);
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            unsafe
            {
                return this.IsReady((AtkUnitBase*)pointer.Address);
            }
        });
    }

    private async Task<bool> WaitForAddon(string addonName, CancellationToken cancellationToken, int attempts = 30)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await this.IsAddonReady(addonName))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return false;
    }

    private async Task WaitUntilAddonGone(string addonName, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await this.IsAddonReady(addonName))
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    private Task<bool> CloseAddon(string addonName, CancellationToken cancellationToken)
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            var pointer = this.gameGui.GetAddonByName(addonName);
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            unsafe
            {
                var addon = (AtkUnitBase*)pointer.Address;
                return this.IsReady(addon) && addon->Close(true);
            }
        });
    }

    private unsafe bool IsReady(AtkUnitBase* addon)
    {
        return addon != null && addon->IsReady && addon->IsVisible;
    }
}
