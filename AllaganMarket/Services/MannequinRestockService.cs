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

    // Poll cadence for the state-driven waits and the post-callback settle
    // delay. Dialogs open within a frame or two, so 50ms keeps the flow snappy
    // while the attempt counts still leave over a second of headroom.
    private const int PollIntervalMilliseconds = 50;

    // The native window briefly reports itself as not-ready while it refreshes
    // after a slot changes. Tearing the run down on the first such frame is what
    // used to abort restocking immediately, so allow a short grace period.
    private const long AddonGraceMilliseconds = 2000;

    private const string SellAsSetSettingKey = "MannequinRestockSellAsSet";
    private const string ConfirmOnFinishSettingKey = "MannequinRestockConfirmOnFinish";

    // MerchantSetting callbacks (verified against the native flow):
    // 11 = 确定 (commit and close), 12 = list equipment in slot,
    // 13 = slot context menu (remove listed/sold-out item).
    private const int MerchantSettingConfirmCallback = 11;
    private const int MerchantSettingListSlotCallback = 12;
    private const int MerchantSettingSlotContextCallback = 13;
    private const int MerchantEquipSelectChooseCallback = 19;
    private const int RetainerSellConfirmCallback = 0;

    // Private-use glyph the game appends to HQ item names in list rows.
    private const char HighQualityGlyph = '';

    private const int MannequinSlotCount = 12;

    // Native node trees are shallow; the cap only exists so a malformed or cyclic
    // tree can never overflow the stack (an uncatchable crash for the whole game).
    private const int MaxNodeDepth = 24;

    // AgentMerchantSettingInfo availability values: 1 = listed. 2 = sold out
    // (item still occupies the native slot). Other values with an item id set
    // (e.g. after collecting the earnings of a sold set) leave the native slot
    // empty, so only the listed state counts as "does not need restocking".
    private const byte AvailabilityListed = 1;
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

    // Guards restockCancellationTokenSource: the run's finally block disposes it
    // from a thread pool thread while the framework thread can still cancel it.
    private readonly object restockLock = new();
    private long lastDiagnosticMilliseconds;
    private long lastCaptureAttemptMilliseconds;
    private long lastAddonSeenMilliseconds;
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

    /// <summary>
    /// Gets or sets a value indicating whether "只按整套出售" is re-enabled once every
    /// slot has been relisted. The game clears it while items are sold out.
    /// </summary>
    public bool SellAsSetOnFinish
    {
        get => !this.configuration.BooleanSettings.TryGetValue(SellAsSetSettingKey, out var value) || value;
        set => this.configuration.Set(SellAsSetSettingKey, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the native 确定 button is pressed at the
    /// end of a run, which commits the shop settings and closes the window.
    /// </summary>
    public bool ConfirmOnFinish
    {
        get => !this.configuration.BooleanSettings.TryGetValue(ConfirmOnFinishSettingKey, out var value) || value;
        set => this.configuration.Set(ConfirmOnFinishSettingKey, value);
    }

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
        this.CancelRestock();
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

        CancellationToken cancellationToken;
        lock (this.restockLock)
        {
            this.restockCancellationTokenSource = new CancellationTokenSource();
            cancellationToken = this.restockCancellationTokenSource.Token;
        }

        this.restockExecution = plan.ToList();
        this.StatusMessage = $"开始补货：{plan.Count} 个售罄装备。";
        this.StateChanged?.Invoke();
        _ = this.ExecuteRestockAsync(this.restockExecution, cancellationToken);
    }

    /// <summary>
    /// Cancels an in-flight restock run. The token source is only ever touched under
    /// <see cref="restockLock"/> because the run disposes it from a thread pool thread
    /// while the framework thread can still be cancelling it.
    /// </summary>
    private void CancelRestock()
    {
        lock (this.restockLock)
        {
            try
            {
                this.restockCancellationTokenSource?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run already finished and disposed its own token source.
            }
        }
    }

    private async Task ExecuteRestockAsync(List<RestockItemPlan> execution, CancellationToken cancellationToken)
    {
        var restockedCount = 0;
        var failedCount = 0;
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
                try
                {
                    await this.RestockPlayerInventoryItemAsync(plan.Item, cancellationToken);
                    restockedCount++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (!this.IsMannequinWindowVisible)
                    {
                        // The shop window went away mid-item; report that instead of
                        // blaming the item for a step that could never have worked.
                        throw new OperationCanceledException("服装模特商店设定窗口已关闭。");
                    }

                    // One slot failing (missing from the picker, a dialog that
                    // never appeared) should not strand the remaining slots.
                    failedCount++;
                    this.StatusMessage = $"{itemName} 补货失败，继续处理下一件。";
                    this.pluginLog.Error(
                        exception,
                        "[MannequinRestock] slot={Slot}; item={ItemId}; restock failed.",
                        plan.Item.EquipmentSlot,
                        plan.Item.ItemId);
                    this.StateChanged?.Invoke();
                    await this.RecoverFromFailedItemAsync(cancellationToken);
                }
            }

            this.StatusMessage = restockedCount > 0
                ? $"补货完成：已重新上架 {restockedCount}/{execution.Count} 个装备。"
                : "补货结束：没有可以重新上架的装备（缺失、价格未知或在雇员中）。";
            if (failedCount > 0)
            {
                this.StatusMessage += $" {failedCount} 个装备失败，请查看日志。";
            }

            this.pluginLog.Information(
                "[MannequinRestock] execution completed; restocked={Restocked}; failed={Failed}; planned={Count}.",
                restockedCount,
                failedCount,
                execution.Count);
            this.StateChanged?.Invoke();

            await Task.Delay(250, cancellationToken);
            if (this.IsMannequinWindowVisible)
            {
                // Agent memory and the configuration dictionary must only be
                // touched on the framework thread.
                await this.framework.RunOnFrameworkThread(() => this.TryCaptureCurrentConfiguration());
            }

            // Last, because pressing 确定 closes the window, which cancels the
            // run; with nothing awaited afterwards that cancel is a no-op.
            if (restockedCount > 0)
            {
                await this.FinishRestockAsync(cancellationToken);
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
            lock (this.restockLock)
            {
                this.restockCancellationTokenSource?.Dispose();
                this.restockCancellationTokenSource = null;
            }

            this.StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Runs the two closing steps of the manual flow: tick "只按整套出售" (confirming the
    /// prompt the game raises) and press 确定 so the shop settings are committed.
    /// </summary>
    private async Task FinishRestockAsync(CancellationToken cancellationToken)
    {
        if (!await this.WaitForAddonAsync(MannequinAddonNameValue, cancellationToken, 20, false))
        {
            return;
        }

        if (this.SellAsSetOnFinish && !await this.TryEnableSellAsSetAsync(cancellationToken))
        {
            // Committing without the set-sale flag is exactly the loss this option
            // exists to prevent, so leave the window open for the user instead.
            this.StatusMessage += " 未能自动勾选“只按整套出售”，请手动勾选后点击确定。";
            this.StateChanged?.Invoke();
            return;
        }

        if (this.ConfirmOnFinish)
        {
            // Only commit when nothing is covering the shop window, otherwise the
            // callback would be delivered while a dialog still owns the flow.
            if (await this.IsAnyObstructingAddonReadyAsync())
            {
                this.StatusMessage += " 仍有对话框未关闭，未自动点击确定。";
                this.pluginLog.Warning("[MannequinRestock] skipping confirm; a dialog is still open.");
                this.StateChanged?.Invoke();
                return;
            }

            this.pluginLog.Information("[MannequinRestock] state=confirm.");
            await this.FireCallbackAsync(MannequinAddonNameValue, cancellationToken, MerchantSettingConfirmCallback, 0);
        }
    }

    private async Task<bool> IsAnyObstructingAddonReadyAsync()
    {
        foreach (var addonName in OverlayObstructingAddons)
        {
            if (await this.IsAddonReadyAsync(addonName))
            {
                return true;
            }
        }

        return false;
    }

    private async Task RestockPlayerInventoryItemAsync(MannequinItem item, CancellationToken cancellationToken)
    {
        var slot = (uint)item.EquipmentSlot;
        await this.WaitForAddonAsync(MannequinAddonNameValue, cancellationToken);

        // 1. Take the sold-out entry off the slot. An occupied slot opens a
        //    context menu; "收回" asks for confirmation, "移除已售罄商品" does not.
        //    Slots whose agent entry lingers after the earnings were collected
        //    are already empty in the native window, so those skip the removal.
        var (slotItemId, slotAvailability) = await this.ReadSlotStateAsync(item.EquipmentSlot);
        var agentSlotIsLive = slotItemId == 0 || slotAvailability == AvailabilitySoldOut;
        if (slotItemId != 0 && slotAvailability == AvailabilitySoldOut)
        {
            this.pluginLog.Information("[MannequinRestock] state=remove-sold-out; slot={Slot}.", item.EquipmentSlot);
            await this.FireCallbackAsync(MannequinAddonNameValue, cancellationToken, MerchantSettingSlotContextCallback, slot);
            if (await this.WaitForAddonAsync("ContextMenu", cancellationToken, 15, false))
            {
                await this.FireCallbackAsync("ContextMenu", cancellationToken, 0, 0, 0);
                if (await this.WaitForAddonAsync("SelectYesno", cancellationToken, 8, false))
                {
                    await this.FireCallbackAsync("SelectYesno", cancellationToken, 0);
                    await this.WaitUntilAddonGoneAsync("SelectYesno", cancellationToken);
                }
            }

            await this.WaitUntilAddonGoneAsync("ContextMenu", cancellationToken);
            if (!await this.WaitForSlotItemAsync(item.EquipmentSlot, 0, cancellationToken))
            {
                throw new InvalidOperationException($"槽位 {slot} 的售罄商品未能下架。");
            }
        }
        else if (slotItemId != 0)
        {
            this.pluginLog.Information(
                "[MannequinRestock] slot={Slot}; availability={Availability}; native slot is already clear, skipping removal.",
                item.EquipmentSlot,
                slotAvailability);
        }

        // 2. Open the equipment picker for the now empty slot.
        await this.FireCallbackAsync(MannequinAddonNameValue, cancellationToken, MerchantSettingListSlotCallback, slot);
        await this.WaitForAddonAsync("MerchantEquipSelect", cancellationToken);

        // 3. Pick the item; when it is not in the bag list, try the retainer tab.
        var callback = await this.FindEquipmentCallbackAsync(item, cancellationToken, 15);
        if (callback < 0 && await this.TrySwitchEquipSelectSourceAsync(cancellationToken))
        {
            callback = await this.FindEquipmentCallbackAsync(item, cancellationToken, 15);
        }

        if (callback < 0)
        {
            await this.TryCloseAddonAsync("MerchantEquipSelect");
            throw new InvalidOperationException($"未在装备选择窗口找到 {this.GetItemName(item.ItemId)}。");
        }

        this.pluginLog.Information(
            "[MannequinRestock] state=select-equipment; slot={Slot}; item={ItemId}; callback={Callback}.",
            item.EquipmentSlot,
            item.ItemId,
            callback);
        await this.FireCallbackAsync("MerchantEquipSelect", cancellationToken, MerchantEquipSelectChooseCallback, callback);

        // 4. Apply the preset price and confirm. The price is written into the
        //    numeric input directly (the pattern the listing/undercut services
        //    already use) so the stack is never confirmed at a stale price.
        await this.WaitForAddonAsync("RetainerSell", cancellationToken);
        if (!await this.SetRetainerSellPriceAsync(item.UnitPrice))
        {
            await this.TryCloseAddonAsync("RetainerSell");
            throw new InvalidOperationException($"未能设置 {this.GetItemName(item.ItemId)} 的上架单价。");
        }

        await this.FireCallbackAsync("RetainerSell", cancellationToken, RetainerSellConfirmCallback);

        // The game closes the price dialog when the listing is accepted, so that
        // is the success signal; a dialog that stays open means it was rejected.
        if (!await this.WaitUntilAddonGoneAsync("RetainerSell", cancellationToken))
        {
            await this.TryCloseAddonAsync("RetainerSell");
            throw new InvalidOperationException($"{this.GetItemName(item.ItemId)} 的上架确认没有被接受。");
        }

        // 5. Give the agent data a moment to reflect the new listing — but only
        //    when it was tracking the slot to begin with. A stale entry (left
        //    behind after a sold set's earnings were collected) never updates,
        //    so waiting on it just added a guaranteed timeout per item.
        if (agentSlotIsLive &&
            !await this.WaitForSlotItemAsync(item.EquipmentSlot, item.ItemId, cancellationToken, 10))
        {
            this.pluginLog.Warning(
                "[MannequinRestock] slot={Slot}; item={ItemId}; the agent data did not confirm the listing; continuing on the closed price dialog.",
                item.EquipmentSlot,
                item.ItemId);
        }
    }

    /// <summary>
    /// Writes the unit price into the RetainerSell numeric input. Setting the value
    /// only updates the input, so the caller still has to fire the confirm callback.
    /// </summary>
    private Task<bool> SetRetainerSellPriceAsync(uint price)
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

    /// <summary>
    /// Switches the equipment picker between the bag and the retainer inventory by
    /// clicking its source tabs, so gear stored on the active retainer can be listed.
    /// </summary>
    private async Task<bool> TrySwitchEquipSelectSourceAsync(CancellationToken cancellationToken)
    {
        var switched = await this.framework.RunOnFrameworkThread(
            () => this.TryClickComponent("MerchantEquipSelect", ComponentType.RadioButton, 1));
        if (!switched)
        {
            return false;
        }

        this.pluginLog.Information("[MannequinRestock] state=switch-equip-source; target=retainer.");
        await Task.Delay(PollIntervalMilliseconds * 3, cancellationToken);
        return true;
    }

    /// <summary>
    /// Closes any dialog the failed item left open so the next slot starts from
    /// the shop settings window instead of inheriting a half-finished flow.
    /// </summary>
    private async Task RecoverFromFailedItemAsync(CancellationToken cancellationToken)
    {
        foreach (var addonName in new[] { "SelectYesno", "ContextMenu", "RetainerSell", "MerchantEquipSelect" })
        {
            if (!await this.IsAddonReadyAsync(addonName))
            {
                continue;
            }

            await this.TryCloseAddonAsync(addonName);
            await this.WaitUntilAddonGoneAsync(addonName, cancellationToken, 10);
        }
    }

    private async Task TryCloseAddonAsync(string addonName)
    {
        try
        {
            await this.framework.RunOnFrameworkThread(() =>
            {
                var pointer = this.gameGui.GetAddonByName(addonName);
                if (pointer == IntPtr.Zero)
                {
                    return;
                }

                unsafe
                {
                    var addon = (AtkUnitBase*)pointer.Address;
                    if (this.IsReady(addon))
                    {
                        // Some windows (the equipment picker) ignore the generic -1
                        // cancel value, so close the window directly.
                        addon->Close(true);
                    }
                }
            });
        }
        catch (Exception exception)
        {
            this.pluginLog.Warning(exception, "[MannequinRestock] failed to close {AddonName}.", addonName);
        }
    }

    /// <summary>
    /// Re-enables "只按整套出售" and confirms the prompt the game shows for it.
    /// The checkbox is clicked through its own node because no addon callback for
    /// it is known; a missing checkbox is reported instead of guessing.
    /// </summary>
    private async Task<bool> TryEnableSellAsSetAsync(CancellationToken cancellationToken)
    {
        var state = await this.framework.RunOnFrameworkThread(() => this.ReadSellAsSetCheckBox());
        if (state == null)
        {
            var components = await this.framework.RunOnFrameworkThread(this.DescribeMannequinComponents);
            this.pluginLog.Warning(
                "[MannequinRestock] could not find the sell-as-set checkbox; components: {Components}",
                components);
            return false;
        }

        if (state == true)
        {
            return true;
        }

        // Set the checked state directly, then notify the window so it reads the
        // new state and raises its confirmation prompt. (Replaying the click
        // event alone never toggles the component, verified in-game.)
        if (await this.framework.RunOnFrameworkThread(() => this.ClickSellAsSetCheckBox(true)) &&
            await this.ConfirmSellAsSetPromptAsync(cancellationToken))
        {
            return true;
        }

        this.pluginLog.Warning("[MannequinRestock] the sell-as-set checkbox did not react to the click.");
        return false;
    }

    /// <summary>
    /// The confirmation prompt appearing is the real signal the window registered
    /// the change, so success requires it to show up, be answered, and close, and
    /// the checkbox to read as ticked afterwards.
    /// </summary>
    private async Task<bool> ConfirmSellAsSetPromptAsync(CancellationToken cancellationToken)
    {
        if (!await this.WaitForAddonAsync("SelectYesno", cancellationToken, 15, false))
        {
            this.pluginLog.Warning("[MannequinRestock] no confirmation prompt appeared after clicking the sell-as-set checkbox.");
            return false;
        }

        await this.FireCallbackAsync("SelectYesno", cancellationToken, 0);
        if (!await this.WaitUntilAddonGoneAsync("SelectYesno", cancellationToken))
        {
            this.pluginLog.Warning("[MannequinRestock] the sell-as-set confirmation stayed open.");
            return false;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await this.framework.RunOnFrameworkThread(() => this.ReadSellAsSetCheckBox() == true))
            {
                this.pluginLog.Information("[MannequinRestock] state=sell-as-set-enabled.");
                return true;
            }

            await Task.Delay(PollIntervalMilliseconds, cancellationToken);
        }

        this.pluginLog.Warning("[MannequinRestock] the sell-as-set checkbox is still unticked after confirming the prompt.");
        return false;
    }

    private unsafe bool ClickSellAsSetCheckBox(bool setCheckedFirst)
    {
        var node = this.FindSellAsSetCheckBoxNode(out var addon);
        if (node == null)
        {
            return false;
        }

        this.pluginLog.Information(
            "[MannequinRestock] sell-as-set checkbox node={NodeId}; setChecked={SetChecked}; events: {Events}",
            node->NodeId,
            setCheckedFirst,
            DescribeNodeEvents(addon, node));

        if (setCheckedFirst)
        {
            var checkBox = (AtkComponentCheckBox*)node->GetAsAtkComponentNode()->Component;
            if (checkBox != null)
            {
                checkBox->IsChecked = true;
            }
        }

        return ClickNode(addon, node);
    }

    private static unsafe string DescribeNodeEvents(AtkUnitBase* addon, AtkResNode* node)
    {
        var parts = new List<string>();
        AppendNodeEvents(addon, node, "self", parts);
        if ((ushort)node->Type >= 1000)
        {
            var component = node->GetAsAtkComponentNode()->Component;
            if (component != null)
            {
                var uldManager = component->UldManager;
                for (var index = 0; index < uldManager.NodeListCount; index++)
                {
                    var child = uldManager.NodeList[index];
                    if (child != null)
                    {
                        AppendNodeEvents(addon, child, $"child{child->NodeId}", parts);
                    }
                }
            }
        }

        return parts.Count == 0 ? "<none>" : string.Join(" | ", parts);
    }

    private static unsafe void AppendNodeEvents(AtkUnitBase* addon, AtkResNode* node, string label, List<string> parts)
    {
        var events = new List<string>();
        for (var registered = node->AtkEventManager.Event; registered != null; registered = registered->NextEvent)
        {
            var listener = registered->Listener == null
                ? "null"
                : registered->Listener == (AtkEventListener*)addon
                    ? "addon"
                    : "component";
            events.Add($"{(int)registered->State.EventType}/p{registered->Param}/{listener}");
        }

        if (events.Count > 0)
        {
            parts.Add($"{label}[{string.Join(",", events)}]");
        }
    }

    /// <summary>
    /// Reads the sell-as-set checkbox state without clicking anything.
    /// Null when the checkbox cannot be located.
    /// </summary>
    private unsafe bool? ReadSellAsSetCheckBox()
    {
        var node = this.FindSellAsSetCheckBoxNode(out var addon);
        if (node == null)
        {
            return null;
        }

        var checkBox = (AtkComponentCheckBox*)node->GetAsAtkComponentNode()->Component;
        return checkBox != null && checkBox->IsChecked;
    }

    private unsafe AtkResNode* FindSellAsSetCheckBoxNode(out AtkUnitBase* addon)
    {
        addon = null;
        var pointer = this.gameGui.GetAddonByName(MannequinAddonNameValue);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        addon = (AtkUnitBase*)pointer.Address;
        if (!this.IsReady(addon) || addon->RootNode == null)
        {
            return null;
        }

        var remaining = 0;
        return FindComponentNode(&addon->UldManager, ComponentType.CheckBox, ref remaining);
    }

    private unsafe string DescribeMannequinComponents()
    {
        var pointer = this.gameGui.GetAddonByName(MannequinAddonNameValue);
        if (pointer == IntPtr.Zero)
        {
            return "<no addon>";
        }

        var addon = (AtkUnitBase*)pointer.Address;
        return this.IsReady(addon) ? DescribeComponentTypes(&addon->UldManager) : "<not ready>";
    }

    private unsafe bool TryClickComponent(string addonName, ComponentType componentType, int ordinal)
    {
        var pointer = this.gameGui.GetAddonByName(addonName);
        if (pointer == IntPtr.Zero)
        {
            return false;
        }

        var addon = (AtkUnitBase*)pointer.Address;
        if (!this.IsReady(addon) || addon->RootNode == null)
        {
            return false;
        }

        var remaining = ordinal;
        var node = FindComponentNode(&addon->UldManager, componentType, ref remaining);
        return node != null && ClickNode(addon, node);
    }

    private static unsafe AtkResNode* FindComponentNode(AtkUldManager* uldManager, ComponentType componentType, ref int remaining, int depth = 0)
    {
        if (uldManager == null || depth > MaxNodeDepth)
        {
            return null;
        }

        // The uld manager lists every node it owns flat, including component
        // nodes whose own content lives in their nested uld manager rather than
        // the child chain, so scan the list and recurse into each component.
        for (var index = 0; index < uldManager->NodeListCount; index++)
        {
            var node = uldManager->NodeList[index];
            if (node == null || (ushort)node->Type < 1000)
            {
                continue;
            }

            var component = node->GetAsAtkComponentNode()->Component;
            if (component == null)
            {
                continue;
            }

            var objectInfo = (AtkUldComponentInfo*)component->UldManager.Objects;
            if (objectInfo != null && objectInfo->ComponentType == componentType &&
                node->NodeFlags.HasFlag(NodeFlags.Visible))
            {
                if (remaining == 0)
                {
                    return node;
                }

                remaining--;
            }

            var nested = FindComponentNode(&component->UldManager, componentType, ref remaining, depth + 1);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private static unsafe string DescribeComponentTypes(AtkUldManager* uldManager, int depth = 0)
    {
        if (uldManager == null || depth > MaxNodeDepth)
        {
            return string.Empty;
        }

        var types = new List<string>();
        for (var index = 0; index < uldManager->NodeListCount; index++)
        {
            var node = uldManager->NodeList[index];
            if (node == null || (ushort)node->Type < 1000)
            {
                continue;
            }

            var component = node->GetAsAtkComponentNode()->Component;
            if (component == null)
            {
                continue;
            }

            var objectInfo = (AtkUldComponentInfo*)component->UldManager.Objects;
            if (objectInfo != null)
            {
                types.Add($"{node->NodeId}:{objectInfo->ComponentType}");
            }

            var nested = DescribeComponentTypes(&component->UldManager, depth + 1);
            if (nested.Length > 0)
            {
                types.Add($"[{nested}]");
            }
        }

        return string.Join(",", types);
    }

    /// <summary>
    /// Dispatches a synthesized click at a component node, for controls whose addon
    /// callback id is unknown (the sell-as-set checkbox, the picker tabs). Components
    /// register their handler as ButtonClick, plain nodes as MouseClick, and either
    /// may live on a collision node inside the component rather than the component
    /// node itself, so both the node and its component children are tried.
    /// </summary>
    private static unsafe bool ClickNode(AtkUnitBase* addon, AtkResNode* node)
    {
        if (TryDispatchClick(addon, node))
        {
            return true;
        }

        if ((ushort)node->Type >= 1000)
        {
            var component = node->GetAsAtkComponentNode()->Component;
            if (component != null)
            {
                var uldManager = component->UldManager;
                for (var index = 0; index < uldManager.NodeListCount; index++)
                {
                    var child = uldManager.NodeList[index];
                    if (child != null && TryDispatchClick(addon, child))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static unsafe bool TryDispatchClick(AtkUnitBase* addon, AtkResNode* node)
    {
        foreach (var eventType in new[] { AtkEventType.ButtonClick, AtkEventType.MouseClick })
        {
            for (var registered = node->AtkEventManager.Event; registered != null; registered = registered->NextEvent)
            {
                if (registered->State.EventType != eventType)
                {
                    continue;
                }

                // Hand the node's own registered event object back to whichever
                // listener the game registered it for: components register mouse
                // events to themselves and convert them into a ButtonClick for
                // the window, so delivering to the addon skips that conversion.
                var eventData = default(AtkEventData);
                var listener = registered->Listener != null ? registered->Listener : (AtkEventListener*)addon;
                listener->ReceiveEvent(eventType, (int)registered->Param, registered, &eventData);
                return true;
            }
        }

        return false;
    }

    private Task<(long ItemId, byte Availability)> ReadSlotStateAsync(int equipmentSlot)
    {
        return this.framework.RunOnFrameworkThread(() => this.ReadSlotState(equipmentSlot));
    }

    private unsafe (long ItemId, byte Availability) ReadSlotState(int equipmentSlot)
    {
        if (equipmentSlot < 0 || equipmentSlot >= MannequinSlotCount)
        {
            return (-1, 0);
        }

        var agentInfo = AgentMerchantSettingInfo.Instance();
        if (agentInfo == null)
        {
            return (-1, 0);
        }

        var slot = agentInfo->ItemsSpan[equipmentSlot];
        return (slot.ItemId, slot.Availability);
    }

    /// <summary>
    /// Waits until the mannequin slot holds the expected item id (0 = empty; a non-zero
    /// id must also read as actively listed), so each step is driven by the real game
    /// state instead of fixed delays.
    /// </summary>
    private async Task<bool> WaitForSlotItemAsync(int equipmentSlot, uint expectedItemId, CancellationToken cancellationToken, int attempts = 40)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (itemId, availability) = await this.ReadSlotStateAsync(equipmentSlot);
            if (itemId < 0)
            {
                // Agent data is unavailable; do not block the run on it.
                return true;
            }

            if (itemId == expectedItemId && (expectedItemId == 0 || availability == AvailabilityListed))
            {
                return true;
            }

            await Task.Delay(PollIntervalMilliseconds, cancellationToken);
        }

        return false;
    }

    private async Task<int> FindEquipmentCallbackAsync(MannequinItem item, CancellationToken cancellationToken, int attempts = 30)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callback = await this.framework.RunOnFrameworkThread(() => this.FindEquipmentCallback(item));
            if (callback >= 0)
            {
                return callback;
            }

            await Task.Delay(PollIntervalMilliseconds, cancellationToken);
        }

        // Dump what the picker actually shows so a layout change is diagnosable
        // from the log instead of just reporting "not found".
        var listing = await this.framework.RunOnFrameworkThread(this.DescribeEquipSelectRows);
        this.pluginLog.Warning(
            "[MannequinRestock] item {ItemId} ({ItemName}, hq={Hq}) not found in MerchantEquipSelect; rows: {Rows}",
            item.ItemId,
            this.GetItemName(item.ItemId),
            item.IsHighQuality,
            listing);
        return -1;
    }

    private unsafe string DescribeEquipSelectRows()
    {
        var pointer = this.gameGui.GetAddonByName("MerchantEquipSelect");
        if (pointer == IntPtr.Zero)
        {
            return "<no addon>";
        }

        var addon = (AtkUnitBase*)pointer.Address;
        if (!this.IsReady(addon) || addon->RootNode == null)
        {
            return "<not ready>";
        }

        var rows = new List<string>();
        foreach (var nodeIndex in EnumerateEquipSelectRowIds())
        {
            var node = GetNodeByIdChain(addon->RootNode, 1, 8, 13, nodeIndex, 3);
            if (node == null || node->Type != NodeType.Text)
            {
                continue;
            }

            var text = node->GetAsAtkTextNode()->NodeText.ToString().Trim();
            if (text.Length > 0)
            {
                rows.Add($"{nodeIndex}:{text}");
            }
        }

        return rows.Count == 0 ? GetNodeTextSummary(addon->RootNode) : string.Join(" | ", rows);
    }

    private static IEnumerable<int> EnumerateEquipSelectRowIds()
    {
        yield return 4;
        for (var index = 41001; index < 41051; index++)
        {
            yield return index;
        }
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

        if (!this.itemSheet.TryGetRow(item.ItemId, out var sheetItem))
        {
            return -1;
        }

        var itemName = sheetItem.Name.ToString();
        foreach (var nodeIndex in EnumerateEquipSelectRowIds())
        {
            var node = GetNodeByIdChain(addon->RootNode, 1, 8, 13, nodeIndex, 3);
            if (node == null || node->Type != NodeType.Text)
            {
                continue;
            }

            var text = node->GetAsAtkTextNode()->NodeText.ToString().Trim();
            if (!text.Contains(itemName, StringComparison.Ordinal))
            {
                continue;
            }

            // HQ rows carry the HQ glyph after the name; when the preset knows the
            // quality, require it to match so the NQ copy is never listed instead.
            var rowIsHq = text.Contains(HighQualityGlyph);
            if (rowIsHq != item.IsHighQuality)
            {
                continue;
            }

            return nodeIndex == 4 ? 0 : nodeIndex - 41000;
        }

        return -1;
    }

    private async Task FireCallbackAsync(string addonName, CancellationToken cancellationToken, params CallbackValue[] values)
    {
        await this.framework.RunOnFrameworkThread(() =>
        {
            var pointer = this.gameGui.GetAddonByName(addonName);
            if (pointer == IntPtr.Zero)
            {
                throw new InvalidOperationException($"窗口 {addonName} 不存在，无法执行 callback。");
            }

            unsafe
            {
                var addon = (AtkUnitBase*)pointer.Address;
                if (!this.IsReady(addon))
                {
                    throw new InvalidOperationException($"窗口 {addonName} 尚未就绪，无法执行 callback。");
                }

                var atkValues = stackalloc AtkValue[values.Length];
                for (var index = 0; index < values.Length; index++)
                {
                    atkValues[index] = values[index].Value;
                }

                // The first value is the callback id; the count must match the
                // number of values or the game reads uninitialised memory.
                addon->FireCallback((uint)values.Length, atkValues, true);
            }
        });
        await Task.Delay(PollIntervalMilliseconds, cancellationToken);
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

            await Task.Delay(PollIntervalMilliseconds, cancellationToken);
        }

        if (throwOnTimeout)
        {
            throw new TimeoutException($"等待窗口 {addonName} 超时。");
        }

        return false;
    }

    /// <summary>
    /// Polls until the addon is closed. Returns false when it is still open after
    /// <paramref name="attempts"/> polls so callers can treat that as a failure.
    /// </summary>
    private async Task<bool> WaitUntilAddonGoneAsync(string addonName, CancellationToken cancellationToken, int attempts = 30)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = await this.IsAddonReadyAsync(addonName);
            if (!ready)
            {
                return true;
            }

            await Task.Delay(PollIntervalMilliseconds, cancellationToken);
        }

        return false;
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

    /// <summary>
    /// Resolves a node by walking an id chain level by level: the root must carry the
    /// first id, and each following id is looked up among the previous node's direct
    /// children. Rows of list components live in the component's uld node list rather
    /// than the child chain, so component contents are searched there.
    /// </summary>
    private static unsafe AtkResNode* GetNodeByIdChain(AtkResNode* root, params int[] ids)
    {
        if (root == null || ids.Length == 0 || root->NodeId != (uint)ids[0])
        {
            return null;
        }

        var current = root;
        for (var index = 1; index < ids.Length; index++)
        {
            current = FindDirectChildById(current, (uint)ids[index]);
            if (current == null)
            {
                return null;
            }
        }

        return current;
    }

    private static unsafe AtkResNode* FindDirectChildById(AtkResNode* parent, uint id)
    {
        // ChildNode points into the sibling chain at an arbitrary end depending on
        // how the node was built, so walk both directions from it.
        for (var node = parent->ChildNode; node != null; node = node->PrevSiblingNode)
        {
            if (node->NodeId == id)
            {
                return node;
            }
        }

        for (var node = parent->ChildNode; node != null; node = node->NextSiblingNode)
        {
            if (node->NodeId == id)
            {
                return node;
            }
        }

        // Component nodes (lists, list item renderers, buttons) keep their content
        // in the uld manager instead of the child chain.
        if ((ushort)parent->Type >= 1000)
        {
            var component = parent->GetAsAtkComponentNode()->Component;
            if (component != null)
            {
                var uldManager = component->UldManager;
                for (var index = 0; index < uldManager.NodeListCount; index++)
                {
                    var node = uldManager.NodeList[index];
                    if (node != null && node->NodeId == id)
                    {
                        return node;
                    }
                }
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
            var rawStates = new List<string>();
            foreach (var item in agentInfo->ItemsSpan)
            {
                if (item.ItemId != 0)
                {
                    rawStates.Add($"{itemIndex}:{item.ItemId}@{item.Availability}");
                    var capturedItem = new MannequinItem
                    {
                        EquipmentSlot = itemIndex,
                        ItemId = item.ItemId,
                        IsHighQuality = item.IsHighQuality,
                        UnitPrice = item.Price > 0 ? (uint)Math.Min(item.Price, uint.MaxValue) : 0,
                        IsSoldOut = item.Availability != AvailabilityListed,
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
                else if (saved != null)
                {
                    // A slot the saved preset knows about but that is currently empty
                    // (e.g. the item was taken down but never relisted) is still
                    // restockable, so surface it as needing restock.
                    var savedItem = saved.Items.Find(existing => existing.EquipmentSlot == itemIndex && existing.ItemId != 0);
                    if (savedItem != null)
                    {
                        var restoredItem = CloneItem(savedItem);
                        restoredItem.IsSoldOut = true;
                        if (restoredItem.UnitPrice == 0)
                        {
                            hasUnknownPrices = true;
                        }

                        captured.Items.Add(restoredItem);
                    }
                }

                itemIndex++;
            }

            // The agent memory does not update in real time when items are taken
            // down manually through the native window (only a commit or reopen
            // refreshes it), but the window itself is always current — so slots
            // the agent claims are listed while the window no longer shows their
            // item are downgraded to needing restock.
            this.ApplyNativeWindowCrossCheck(captured, saved, ref hasUnknownPrices);

            var signature = $"{mannequinId};" + string.Join(
                ",",
                captured.Items.Select(item => $"{item.EquipmentSlot}:{item.ItemId}:{item.IsHighQuality}:{item.UnitPrice}:{item.IsSoldOut}"));
            if (signature == this.lastCaptureSignature && this.CurrentConfiguration != null)
            {
                return captured.Items.Count > 0;
            }

            this.lastCaptureSignature = signature;
            this.pluginLog.Information(
                "[MannequinDiag] raw slot availability: {RawStates}.",
                rawStates.Count == 0 ? "<all empty>" : string.Join(", ", rawStates));
            foreach (var item in captured.Items)
            {
                this.pluginLog.Information(
                    "[MannequinDiag] slot={Slot}; itemId={ItemId}; hq={HighQuality}; price={Price}; needsRestock={NeedsRestock}.",
                    item.EquipmentSlot,
                    item.ItemId,
                    item.IsHighQuality,
                    item.UnitPrice,
                    item.IsSoldOut);
            }

            this.CurrentConfiguration = captured;
            if (!this.IsRestocking)
            {
                // Remember prices and HQ flags while items are still listed so
                // sold-out slots can be restocked after the agent data loses them.
                this.PersistConfiguration(captured);
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

    public void UpdatePresetItem(int equipmentSlot, uint unitPrice, bool isHighQuality)
    {
        if (this.IsRestocking)
        {
            // CurrentConfiguration can hold transient state mid-restock;
            // persisting it would overwrite the saved snapshot for all slots.
            this.pluginLog.Debug("[MannequinRestock] ignoring preset edit while restocking; slot={Slot}.", equipmentSlot);
            return;
        }

        var current = this.CurrentConfiguration;
        var item = current?.Items.Find(existing => existing.EquipmentSlot == equipmentSlot);
        if (current == null || item == null)
        {
            this.pluginLog.Debug("[MannequinRestock] preset edit dropped; slot={Slot} not found in current configuration.", equipmentSlot);
            return;
        }

        item.UnitPrice = unitPrice;
        item.IsHighQuality = isHighQuality;
        this.PersistConfiguration(current);
        this.StateChanged?.Invoke();
    }

    private unsafe void ApplyNativeWindowCrossCheck(
        MannequinConfiguration captured,
        MannequinConfiguration? saved,
        ref bool hasUnknownPrices)
    {
        var addon = (AtkUnitBase*)this.MannequinAddonAddress;
        if (addon == null || !this.IsReady(addon) || addon->RootNode == null)
        {
            return;
        }

        var texts = new List<string>();
        CollectNodeTextRaw(addon->RootNode, texts, 0);

        // Occurrences are counted per name because identical items can occupy
        // several slots (e.g. two rings); when the window shows fewer copies
        // than the agent claims are listed, the surplus slots are empty.
        foreach (var group in captured.Items.Where(item => !item.IsSoldOut).GroupBy(item => this.GetItemName(item.ItemId)).ToList())
        {
            if (string.IsNullOrEmpty(group.Key))
            {
                continue;
            }

            var occurrences = texts.Count(text => text.Contains(group.Key, StringComparison.Ordinal));
            var missing = group.Count() - occurrences;
            if (missing <= 0)
            {
                continue;
            }

            foreach (var item in group.OrderByDescending(entry => entry.EquipmentSlot).Take(missing))
            {
                item.IsSoldOut = true;
                if (item.UnitPrice == 0)
                {
                    var savedItem = saved?.Items.Find(
                        existing => existing.EquipmentSlot == item.EquipmentSlot && existing.ItemId == item.ItemId);
                    if (savedItem != null)
                    {
                        item.IsHighQuality = savedItem.IsHighQuality;
                        item.UnitPrice = savedItem.UnitPrice;
                    }

                    if (item.UnitPrice == 0)
                    {
                        hasUnknownPrices = true;
                    }
                }
            }
        }
    }

    private static unsafe void CollectNodeTextRaw(AtkResNode* node, List<string> texts, int depth)
    {
        if (node == null || depth > MaxNodeDepth || texts.Count >= 96)
        {
            return;
        }

        if (node->Type == NodeType.Text && node->NodeFlags.HasFlag(NodeFlags.Visible))
        {
            var text = node->GetAsAtkTextNode()->NodeText.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text.Length > 80 ? text[..80] : text);
            }
        }

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
        {
            CollectNodeTextRaw(child, texts, depth + 1);
        }
    }

    private void PersistConfiguration(MannequinConfiguration current)
    {
        if (current.MannequinId == 0 || current.Items.Count == 0)
        {
            return;
        }

        // Keep previously saved slots that are currently absent (e.g. an entry
        // being replaced) so their price records survive. Items are cloned so
        // the saved snapshot never aliases CurrentConfiguration.
        this.configuration.MannequinConfigurations.TryGetValue(current.MannequinId, out var saved);
        var toSave = new MannequinConfiguration
        {
            MannequinId = current.MannequinId,
            RetainerId = current.RetainerId,
            Items = current.Items.Select(CloneItem).ToList(),
        };
        if (saved != null)
        {
            toSave.Items.AddRange(
                saved.Items
                    .Where(existing => current.Items.All(item => item.EquipmentSlot != existing.EquipmentSlot))
                    .Select(CloneItem));
        }

        this.configuration.MannequinConfigurations[current.MannequinId] = toSave;
        this.configuration.IsDirty = true;
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

    /// <summary>
    /// Resolves a display name. Lumina rows are immutable once loaded, so unlike the
    /// game memory this file touches, this is safe to call off the framework thread.
    /// </summary>
    public string GetItemName(uint itemId)
    {
        return this.itemSheet.TryGetRow(itemId, out var item) ? item.Name.ToString() : $"物品 {itemId}";
    }

    /// <summary>
    /// Whether the native equipment picker is open. It docks against the shop
    /// window's right edge, exactly where the preset panel sits, so the panel
    /// moves to the left side while it is visible.
    /// </summary>
    public bool IsEquipmentPickerVisible()
    {
        return this.IsAddonReadyOnFrameworkThread("MerchantEquipSelect");
    }

    public unsafe RestockItemSource ResolveRestockSource(MannequinItem item)
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
                // The window reports itself as not-ready for a few frames while it
                // refreshes after a slot changes, so keep an in-flight restock run
                // alive until the window has really been gone for a moment.
                if (this.IsRestocking &&
                    Environment.TickCount64 - this.lastAddonSeenMilliseconds < AddonGraceMilliseconds)
                {
                    return;
                }

                this.CancelRestock();
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

        this.lastAddonSeenMilliseconds = Environment.TickCount64;

        if (addonAddress.Address == this.MannequinAddonAddress && this.IsMannequinWindowVisible)
        {
            // Recapture periodically while the window is open: the agent data can
            // lag the addon on open, and manual take-downs only show up through
            // the native-window cross-check. The capture signature keeps an
            // unchanged state from causing any downstream work.
            if (!this.IsRestocking &&
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

        this.CancelRestock();
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


/// <summary>
/// A single AtkValue passed to <c>AtkUnitBase.FireCallback</c>. Values convert
/// implicitly so call sites read like the native callback they mirror, and the
/// count handed to the game always matches the number of values.
/// </summary>
public readonly struct CallbackValue
{
    private CallbackValue(AtkValue value)
    {
        this.Value = value;
    }

    public AtkValue Value { get; }

    public static implicit operator CallbackValue(int value)
    {
        return new CallbackValue(new AtkValue { Type = AtkValueType.Int, Int = value });
    }

    public static implicit operator CallbackValue(uint value)
    {
        return new CallbackValue(new AtkValue { Type = AtkValueType.UInt, UInt = value });
    }
}

public sealed record RestockItemPlan(MannequinItem Item, RestockItemSource Source);
