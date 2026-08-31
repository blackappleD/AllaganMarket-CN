using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AllaganMarket.GameInterop;
using AllaganMarket.Models;
using AllaganMarket.Services.Interfaces;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Microsoft.Extensions.Hosting;

namespace AllaganMarket.Services;

/// <summary>
/// Runs the retainer repricing flow through addon callbacks. No OS mouse or keyboard input is generated.
/// </summary>
public sealed class AutoUndercutService : IHostedService, IDisposable
{
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog pluginLog;
    private readonly ICharacterMonitorService characterMonitorService;
    private readonly MarketPriceUpdaterService marketPriceUpdaterService;
    private readonly SaleTrackerService saleTrackerService;
    private readonly UndercutService undercutService;

    private CancellationTokenSource? cancellationTokenSource;
    private int marketBoardRetryRequested;

    public AutoUndercutService(
        IFramework framework,
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        IPluginLog pluginLog,
        ICharacterMonitorService characterMonitorService,
        MarketPriceUpdaterService marketPriceUpdaterService,
        SaleTrackerService saleTrackerService,
        UndercutService undercutService)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.addonLifecycle = addonLifecycle;
        this.pluginLog = pluginLog;
        this.characterMonitorService = characterMonitorService;
        this.marketPriceUpdaterService = marketPriceUpdaterService;
        this.saleTrackerService = saleTrackerService;
        this.undercutService = undercutService;
    }

    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Talk can be created and refreshed several times while the retainer's
        // greeting/dialogue is being displayed.  Advancing it from both events
        // mirrors TextAdvance and keeps the automation from getting stuck on a
        // transient Talk addon.
        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", this.OnTalkUpdated);
        this.addonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", this.OnTalkUpdated);
        this.marketPriceUpdaterService.MarketBoardItemRequestReceived += this.MarketBoardItemRequestReceived;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.cancellationTokenSource?.Cancel();
        this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Talk", this.OnTalkUpdated);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "Talk", this.OnTalkUpdated);
        this.marketPriceUpdaterService.MarketBoardItemRequestReceived -= this.MarketBoardItemRequestReceived;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        this.cancellationTokenSource?.Cancel();
        this.cancellationTokenSource?.Dispose();
    }

    public void Start()
    {
        if (this.IsRunning || !this.characterMonitorService.IsLoggedIn)
        {
            return;
        }

        var activeCharacter = this.characterMonitorService.ActiveCharacter;
        if (activeCharacter == null)
        {
            return;
        }

        var retainers = this.characterMonitorService.GetRetainers(activeCharacter.CharacterId)
            .Where(retainer => this.IsAutoUndercutEnabled(retainer))
            .OrderBy(retainer => retainer.DisplayOrder)
            .ToList();
        if (retainers.Count == 0)
        {
            return;
        }

        this.cancellationTokenSource?.Cancel();
        this.cancellationTokenSource?.Dispose();
        this.cancellationTokenSource = new CancellationTokenSource();
        this.IsRunning = true;
        this.pluginLog.Information(
            $"Automatic undercut: selected {retainers.Count} retainer(s): {string.Join(", ", retainers.Select(retainer => $"{retainer.Name} [{retainer.CharacterId}]"))}.");
        _ = this.RunAsync(retainers, this.cancellationTokenSource.Token);
    }

    public void Cancel()
    {
        this.cancellationTokenSource?.Cancel();
    }

    private bool IsAutoUndercutEnabled(Character retainer)
    {
        // Read the persisted configuration as the source of truth. This avoids
        // using a stale Character instance if the retainer list was refreshed
        // between checking the box and pressing Execute.
        return this.characterMonitorService.Characters.TryGetValue(retainer.CharacterId, out var current) &&
               current.AutoUndercut;
    }

    private async Task RunAsync(IReadOnlyList<Character> retainers, CancellationToken cancellationToken)
    {
        try
        {
            if (!await this.WaitForAddon("RetainerList", cancellationToken))
            {
                return;
            }

            foreach (var retainer in retainers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.pluginLog.Information($"Automatic undercut: opening retainer {retainer.Name}.");

                if (!await this.OpenRetainer(retainer, cancellationToken))
                {
                    this.pluginLog.Error($"Automatic undercut: unable to open retainer {retainer.Name}.");
                    await this.CloseRetainerWindows(cancellationToken);
                    continue;
                }

                await Task.Delay(400, cancellationToken);
                await this.ProcessRetainer(retainer, cancellationToken);
                await this.CloseRetainerWindows(cancellationToken);
                await Task.Delay(400, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            this.pluginLog.Information("Automatic undercut cancelled.");
        }
        catch (Exception ex)
        {
            this.pluginLog.Error($"Automatic undercut failed: {ex}");
        }
        finally
        {
            this.IsRunning = false;
        }
    }

    private async Task<bool> OpenRetainer(Character retainer, CancellationToken cancellationToken)
    {
        // The RetainerList addon is briefly refreshing after the previous
        // retainer is closed.  Give the game time to finish that transition and
        // retry the internal callback instead of skipping the next retainer.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await this.WaitForAddon("RetainerList", cancellationToken))
            {
                await Task.Delay(250, cancellationToken);
                continue;
            }

            if (await this.SelectRetainer(retainer.CharacterId, retainer.DisplayOrder, cancellationToken) &&
                await this.WaitForSelectString(cancellationToken) &&
                await this.SelectSellInventory(cancellationToken) &&
                await this.WaitForAddon("RetainerSellList", cancellationToken))
            {
                return true;
            }

            this.pluginLog.Warning(
                $"Automatic undercut: retainer {retainer.Name} did not finish opening (attempt {attempt + 1}/3).");
            await this.CloseRetainerWindows(cancellationToken);
            await Task.Delay(350, cancellationToken);
        }

        return false;
    }

    private async Task ProcessRetainer(Character retainer, CancellationToken cancellationToken)
    {
        List<SaleItem>? saleItems = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            saleItems = this.saleTrackerService.GetRetainerSales(retainer.CharacterId)?
                .Where(item => !item.IsEmpty())
                .OrderBy(item => item.MenuIndex)
                .ToList();
            if (saleItems is { Count: > 0 })
            {
                break;
            }

            await Task.Delay(100, cancellationToken);
        }

        if (saleItems == null || saleItems.Count == 0)
        {
            this.pluginLog.Information($"Automatic undercut: retainer {retainer.Name} has no listed items.");
            return;
        }

        var marketPrices = new Dictionary<(uint ItemId, bool IsHq), uint?>();
        for (var rowIndex = 0; rowIndex < saleItems.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var saleItem = saleItems[rowIndex];
            if (!await this.SelectListedItem(rowIndex, cancellationToken) ||
                !await this.WaitForAddon("ContextMenu", cancellationToken) ||
                !await this.SelectContextMenuItem(cancellationToken) ||
                !await this.WaitForAddon("RetainerSell", cancellationToken))
            {
                this.pluginLog.Error($"Automatic undercut: unable to open listed item row {rowIndex}.");
                continue;
            }

            var marketKey = (saleItem.ItemId, saleItem.IsHq);
            if (!marketPrices.TryGetValue(marketKey, out var recommendedPrice))
            {
                recommendedPrice = await this.QueryRecommendedPrice(saleItem, cancellationToken);
                marketPrices[marketKey] = recommendedPrice;
            }
            else
            {
                this.pluginLog.Verbose(
                    $"Automatic undercut: reusing market price for item {saleItem.ItemId} ({(saleItem.IsHq ? "HQ" : "NQ")}).");
            }
            if (recommendedPrice is { } price && price < saleItem.UnitPrice)
            {
                await this.SetRetainerSellPrice(price, cancellationToken);
                this.pluginLog.Information($"Automatic undercut: {saleItem.ItemId} {saleItem.UnitPrice} -> {price}.");
            }

            // Confirm even when no lower price exists, preserving the current listing price.
            await this.ClickRetainerSellCallback(0, cancellationToken);
            await this.WaitForAddon("RetainerSellList", cancellationToken);
            await Task.Delay(250, cancellationToken);
        }
    }

    private async Task<uint?> QueryRecommendedPrice(SaleItem saleItem, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref this.marketBoardRetryRequested, 0);

            // ComparePrices is the game's "view current market price" action.
            await this.ClickRetainerSellCallback(4, cancellationToken);
            await Task.Delay(650, cancellationToken);

            uint? result = null;
            for (var wait = 0; wait < 16; wait++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref this.marketBoardRetryRequested) != 0)
                {
                    break;
                }

                result = this.undercutService.GetRecommendedUnitPrice(saleItem)?.Amount;
                if (result != null)
                {
                    break;
                }

                await Task.Delay(100, cancellationToken);
            }

            if (Volatile.Read(ref this.marketBoardRetryRequested) != 0 && attempt < 2)
            {
                this.pluginLog.Warning(
                    "Automatic undercut: market board asked to retry later; closing result window and retrying in 2 seconds.");
                await this.CloseAddon("ItemSearchResult", cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }

            await this.CloseAddon("ItemSearchResult", cancellationToken);
            return result ?? this.undercutService.GetRecommendedUnitPrice(saleItem)?.Amount;
        }

        return null;
    }

    private void MarketBoardItemRequestReceived(MarketBoardItemRequest request)
    {
        if (this.IsRunning && request.Status == MarketPriceUpdaterService.RateLimitedStatus)
        {
            Interlocked.Exchange(ref this.marketBoardRetryRequested, 1);
        }
    }

    private Task<bool> SelectRetainer(ulong retainerId, byte fallbackDisplayOrder, CancellationToken cancellationToken)
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            var pointer = this.gameGui.GetAddonByName("RetainerList");
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

                var displayOrder = -1;
                var retainerManager = RetainerManager.Instance();
                if (retainerManager != null && retainerManager->IsReady)
                {
                    // FireCallback expects the visible/sorted row index. Do
                    // not invert RetainerManager.DisplayOrder here: the
                    // manager's GetRetainerBySortedIndex already performs the
                    // required mapping from visible row to backing slot.
                    for (uint index = 0; index < 10; index++)
                    {
                        var current = retainerManager->GetRetainerBySortedIndex(index);
                        if (current == null || current->RetainerId != retainerId)
                        {
                            continue;
                        }

                        displayOrder = (int)index;
                        break;
                    }
                }

                // RetainerList can become visible a few frames before the
                // manager's Retainers span is repopulated. In that window the
                // persisted display order is still the valid callback index.
                if (displayOrder < 0)
                {
                    displayOrder = fallbackDisplayOrder;
                    this.pluginLog.Verbose(
                        $"Automatic undercut: retainer {retainerId} is not populated yet; using cached display order {displayOrder}.");
                }

                var values = stackalloc AtkValue[4];
                values[0] = new AtkValue { Type = AtkValueType.Int, Int = 2 };
                values[1] = new AtkValue { Type = AtkValueType.UInt, UInt = (uint)displayOrder };
                values[2] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
                values[3] = new AtkValue { Type = AtkValueType.Int, Int = 0 };

                // FireCallback's boolean result is not a reliable success indicator for this addon;
                // the game often returns false even though the retainer selection was accepted.
                addon->FireCallback(4, values, false);
                return true;
            }
        });
    }

    private Task<bool> SelectSellInventory(CancellationToken cancellationToken)
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            var pointer = this.gameGui.GetAddonByName("SelectString");
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            unsafe
            {
                var addon = (AddonSelectString*)pointer.Address;
                if (!this.IsReady(&addon->AtkUnitBase) || addon->PopupMenu.List == null)
                {
                    return false;
                }

                var selectedIndex = -1;
                for (var index = 0; index < addon->PopupMenu.List->ListLength; index++)
                {
                    try
                    {
                        var text = addon->PopupMenu.List->GetItemLabel(index).ToString();
                        if (text.Contains("出售（玩家所持物品）", StringComparison.Ordinal) ||
                            text.Contains("出售（玩家所持物品", StringComparison.Ordinal) ||
                            text.Contains("Sell items in your inventory", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("Sell (Items in your inventory", StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = index;
                            break;
                        }
                    }
                    catch
                    {
                        // UI text nodes can be invalid while SelectString is refreshing.
                    }
                }

                // This is the current fallback row for the standard retainer menu.
                selectedIndex = selectedIndex < 0 ? 2 : selectedIndex;
                var value = new AtkValue { Type = AtkValueType.Int, Int = selectedIndex };
                addon->AtkUnitBase.FireCallback(1, &value, false);
                return true;
            }
        });
    }

    private async Task<bool> WaitForSelectString(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await this.IsAddonReady("SelectString"))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return false;
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

    private unsafe void OnTalkUpdated(AddonEvent type, AddonArgs args)
    {
        if (!this.IsRunning)
        {
            return;
        }

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (this.IsReady(addon))
        {
            ClickTalk(addon);
        }
    }

    /// <summary>
    /// Sends the same internal event sequence used by TextAdvance's
    /// AddonMaster.Talk.Click().  This never moves the user's OS cursor or
    /// generates keyboard input.
    /// </summary>
    private static unsafe void ClickTalk(AtkUnitBase* addon)
    {
        var stage = AtkStage.Instance();
        if (stage == null)
        {
            return;
        }

        var atkEvent = new AtkEvent
        {
            Listener = (AtkEventListener*)addon,
            Target = &stage->AtkEventTarget,
            State = new AtkEventState { StateFlags = (AtkEventStateFlags)132 },
        };
        var eventData = default(AtkEventData);
        addon->ReceiveEvent((AtkEventType)3, 0, &atkEvent, &eventData);
        addon->ReceiveEvent((AtkEventType)9, 0, &atkEvent, &eventData);
        addon->ReceiveEvent((AtkEventType)4, 0, &atkEvent, &eventData);
    }

    private Task<bool> SelectListedItem(int rowIndex, CancellationToken cancellationToken)
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

    private Task<bool> SelectContextMenuItem(CancellationToken cancellationToken)
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            var pointer = this.gameGui.GetAddonByName("ContextMenu");
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

                // For a listed market item, the first context-menu entry is always "Adjust Price".
                var values = stackalloc AtkValue[3];
                values[0] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
                values[1] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
                values[2] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
                addon->FireCallback(3, values, true);
                return true;
            }
        });
    }

    private Task<bool> ClickRetainerSellCallback(int callbackIndex, CancellationToken cancellationToken)
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

    private Task<bool> SetRetainerSellPrice(uint price, CancellationToken cancellationToken)
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

    private async Task<bool> WaitForAddon(string addonName, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await this.framework.RunOnFrameworkThread(() =>
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
                }))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return false;
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

    private async Task CloseRetainerWindows(CancellationToken cancellationToken)
    {
        await this.CloseAddon("RetainerSell", cancellationToken);
        await this.CloseAddon("ItemSearchResult", cancellationToken);
        await this.CloseAddon("RetainerSellList", cancellationToken);
        await Task.Delay(250, cancellationToken);

        // Do not simply close SelectString here.  The retainer interaction
        // menu must receive the internal "return retainer" callback first;
        // otherwise the game remains in the current retainer's menu and the
        // next RetainerList selection can target the wrong state.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await this.ReturnRetainer(cancellationToken))
            {
                if (await this.WaitForAddon("RetainerList", cancellationToken))
                {
                    return;
                }
            }

            await Task.Delay(100, cancellationToken);
        }

        // Fallback for an already-closed/refreshing menu or an unexpected UI
        // state.  This keeps cancellation and recovery paths from leaving a
        // stale SelectString addon visible.
        await this.CloseAddon("SelectString", cancellationToken);
    }

    private Task<bool> ReturnRetainer(CancellationToken cancellationToken)
    {
        return this.framework.RunOnFrameworkThread(() =>
        {
            var pointer = this.gameGui.GetAddonByName("SelectString");
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            unsafe
            {
                var addon = (AddonSelectString*)pointer.Address;
                if (!this.IsReady(&addon->AtkUnitBase) || addon->PopupMenu.List == null)
                {
                    return false;
                }

                var selectedIndex = -1;
                for (var index = 0; index < addon->PopupMenu.List->ListLength; index++)
                {
                    try
                    {
                        var text = addon->PopupMenu.List->GetItemLabel(index).ToString();
                        if (text.Contains("让雇员返回", StringComparison.Ordinal) ||
                            text.Contains("Send retainer home", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("Have retainer return", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("Return retainer", StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = index;
                            break;
                        }
                    }
                    catch
                    {
                        // SelectString text nodes can be invalid while the
                        // menu is being rebuilt; skip that entry safely.
                    }
                }

                if (selectedIndex < 0)
                {
                    this.pluginLog.Verbose("Automatic undercut: return-retainer entry was not found in SelectString.");
                    return false;
                }

                var value = new AtkValue { Type = AtkValueType.Int, Int = selectedIndex };
                // SelectString's option callback is callback 1 (the same
                // callback used for selecting "Sell items in your inventory").
                addon->AtkUnitBase.FireCallback(1, &value, false);
                this.pluginLog.Verbose($"Automatic undercut: selected return-retainer entry at index {selectedIndex}.");
                return true;
            }
        });
    }

    private unsafe bool IsReady(AtkUnitBase* addon)
    {
        return addon != null && addon->IsReady && addon->IsVisible;
    }
}
