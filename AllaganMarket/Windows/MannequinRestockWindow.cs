using System;
using System.Linq;
using System.Numerics;

using AllaganMarket.Services;

using DalaMock.Host.Mediator;
using DalaMock.Shared.Interfaces;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AllaganMarket.Windows;

public sealed class MannequinRestockWindow : ExtendedWindow
{
    private const string CollapsedSettingKey = "MannequinRestockPanelCollapsed";
    private const float IconSize = 28;
    private const float PriceInputWidth = 90;
    private const int MaxDropdownMatches = 100;

    private static readonly string[] SlotNames =
    [
        "主手", "副手", "头部", "身体", "手部", "腿部", "脚部", "耳部", "颈部", "手腕", "右指", "左指",
    ];

    private readonly MannequinRestockService restockService;
    private readonly Configuration configuration;
    private readonly ITextureProvider textureProvider;
    private readonly ExcelSheet<Item> itemSheet;
    private readonly IPluginLog pluginLog;
    private readonly IFont font;
    private Vector2 lastWindowSize;
    private string equipmentSearchText = string.Empty;

    public MannequinRestockWindow(
        MediatorService mediator,
        ImGuiService imGuiService,
        MannequinRestockService restockService,
        Configuration configuration,
        ITextureProvider textureProvider,
        ExcelSheet<Item> itemSheet,
        IPluginLog pluginLog,
        IFont font)
        : base(
            mediator,
            imGuiService,
            "Mannequin Restock",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoSavedSettings,
            true)
    {
        this.restockService = restockService;
        this.configuration = configuration;
        this.textureProvider = textureProvider;
        this.itemSheet = itemSheet;
        this.pluginLog = pluginLog;
        this.font = font;
        this.IsOpen = true;
        this.RespectCloseHotkey = false;
    }

    private bool IsCollapsed
    {
        get => this.configuration.BooleanSettings.TryGetValue(CollapsedSettingKey, out var collapsed) && collapsed;
        set => this.configuration.Set(CollapsedSettingKey, value);
    }

    public override bool DrawConditions()
    {
        if (!this.restockService.IsMannequinWindowVisible)
        {
            return false;
        }

        return base.DrawConditions();
    }

    public override void PreDraw()
    {
        base.PreDraw();
        this.UpdatePosition();
    }

    public override void PostDraw()
    {
        base.PostDraw();
        this.lastWindowSize = ImGui.GetWindowSize();
    }

    public override void Draw()
    {
        var plan = this.restockService.BuildRestockPlan();
        var actionable = plan.Count(item => item.Source != RestockItemSource.Missing && item.Item.UnitPrice > 0);

        this.DrawHeader(plan.Count, actionable);
        if (this.IsCollapsed)
        {
            return;
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 340);
        ImGui.TextWrapped(this.restockService.StatusMessage);
        ImGui.PopTextWrapPos();

        if (this.restockService.CurrentConfiguration == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "尚未读取到模特装备数据。");
            return;
        }

        ImGui.Separator();
        this.DrawOptions();
        this.DrawSlotTable();
    }

    private void DrawOptions()
    {
        using var disabled = ImRaii.Disabled(this.restockService.IsRestocking);
        var sellAsSet = this.restockService.SellAsSetOnFinish;
        if (ImGui.Checkbox("完成后勾选只按整套出售", ref sellAsSet))
        {
            this.restockService.SellAsSetOnFinish = sellAsSet;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("装备售罄时游戏会取消该勾选；补货完成后自动重新勾选并确认提示。");
        }

        ImGui.SameLine();
        var confirmOnFinish = this.restockService.ConfirmOnFinish;
        if (ImGui.Checkbox("完成后点击确定", ref confirmOnFinish))
        {
            this.restockService.ConfirmOnFinish = confirmOnFinish;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("补货完成后自动点击原生窗口的确定按钮提交设定。");
        }
    }

    private void DrawHeader(int soldOutCount, int actionableCount)
    {
        // Same collapse control as the retainer list overlay: a lone chevron
        // icon button, pointing right while collapsed and left while expanded.
        var currentCursorPosX = ImGui.GetCursorPosX();
        if (this.IsCollapsed)
        {
            if (ImGuiService.DrawIconButton(this.font, FontAwesomeIcon.ChevronRight, ref currentCursorPosX, "展开补货预设"))
            {
                this.IsCollapsed = false;
            }

            return;
        }

        if (ImGuiService.DrawIconButton(this.font, FontAwesomeIcon.ChevronLeft, ref currentCursorPosX, "收起补货预设"))
        {
            this.IsCollapsed = true;
        }

        ImGui.SameLine();
        var buttonLabel = this.restockService.IsRestocking
            ? "补货执行中..."
            : soldOutCount > 0
                ? $"一键补货 ({actionableCount}/{soldOutCount})"
                : "一键补货";
        using (ImRaii.Disabled(this.restockService.IsRestocking))
        {
            if (ImGui.Button(buttonLabel, new Vector2(160, 0)))
            {
                this.restockService.BeginRestock();
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("重新上架已售罄的装备（需要装备在背包中且已设置价格）。");
        }
    }

    private void DrawSlotTable()
    {
        using var disabled = ImRaii.Disabled(this.restockService.IsRestocking);
        using var table = ImRaii.Table(
            "MannequinPreset",
            4,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoHostExtendX);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, IconSize);
        ImGui.TableSetupColumn("装备", ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableSetupColumn("价格", ImGuiTableColumnFlags.WidthFixed, PriceInputWidth + 70);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableHeadersRow();

        var presetItems = this.restockService.GetPresetItems();
        for (var slot = 0; slot < SlotNames.Length; slot++)
        {
            var item = presetItems.FirstOrDefault(preset => preset.EquipmentSlot == slot);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (item != null)
            {
                this.DrawItemIcon(item);
            }
            else
            {
                ImGui.Dummy(new Vector2(IconSize, IconSize));
            }

            ImGui.TableNextColumn();
            this.DrawEquipmentPicker(slot, item);

            ImGui.TableNextColumn();
            if (item != null)
            {
                var price = (int)item.UnitPrice;
                ImGui.SetNextItemWidth(PriceInputWidth);
                ImGui.InputInt($"##price{slot}", ref price, 0, 0);
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    this.restockService.UpdatePresetItem(
                        slot,
                        item.ItemId,
                        (uint)Math.Max(0, price),
                        item.IsHighQuality);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("补货时的上架单价。");
                }

                ImGui.SameLine();
                var isHighQuality = item.IsHighQuality;
                if (ImGui.Checkbox($"HQ##{slot}", ref isHighQuality))
                {
                    this.restockService.UpdatePresetItem(slot, item.ItemId, item.UnitPrice, isHighQuality);
                }
            }
            else
            {
                ImGui.TextDisabled("-");
            }

            ImGui.TableNextColumn();
            this.DrawSlotStatus(item);
        }
    }

    private void DrawEquipmentPicker(int slot, Models.MannequinItem? item)
    {
        var label = item == null
            ? $"选择{SlotNames[slot]}装备…"
            : this.restockService.GetItemName(item.ItemId);
        ImGui.SetNextItemWidth(185);
        using var combo = ImRaii.Combo($"##equip{slot}", label, ImGuiComboFlags.HeightLargest);
        if (!combo)
        {
            return;
        }

        if (ImGui.IsWindowAppearing())
        {
            this.equipmentSearchText = string.Empty;
            ImGui.SetKeyboardFocusHere();
        }

        // The search box stays pinned above its own scroll region so scrolling
        // the list never drags the focused input (and the IME indicator) away.
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint($"##equipsearch{slot}", "搜索装备……", ref this.equipmentSearchText, 64);
        ImGui.Separator();

        using var child = ImRaii.Child($"##equiplist{slot}", new Vector2(0, 240 * ImGui.GetIO().FontGlobalScale));
        if (item != null && ImGui.Selectable("〔清除该槽位〕"))
        {
            this.restockService.UpdatePresetItem(slot, 0, 0, false);
            ImGui.CloseCurrentPopup();
        }

        var options = this.restockService.GetEquippableItems(slot);
        var shown = 0;
        foreach (var option in options)
        {
            if (this.equipmentSearchText.Length > 0 &&
                !option.Name.Contains(this.equipmentSearchText, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ImGui.Selectable(option.Label, item != null && option.ItemId == item.ItemId))
            {
                this.restockService.UpdatePresetItem(
                    slot,
                    option.ItemId,
                    item?.UnitPrice ?? 0,
                    item?.IsHighQuality ?? true);
                ImGui.CloseCurrentPopup();
            }

            if (++shown >= MaxDropdownMatches)
            {
                ImGui.TextDisabled($"仅显示前 {MaxDropdownMatches} 件，请输入名称缩小范围（共 {options.Count} 件）。");
                break;
            }
        }

        if (shown == 0)
        {
            ImGui.TextDisabled("没有匹配的装备。");
        }
    }

    private void DrawItemIcon(Models.MannequinItem item)
    {
        if (!this.itemSheet.TryGetRow(item.ItemId, out var itemRow))
        {
            return;
        }

        var icon = this.textureProvider.GetFromGameIcon(new GameIconLookup(itemRow.Icon, item.IsHighQuality));
        ImGui.Image(icon.GetWrapOrEmpty().Handle, new Vector2(IconSize, IconSize));
    }

    private void DrawSlotStatus(Models.MannequinItem? item)
    {
        if (item == null)
        {
            ImGui.TextDisabled("未设置");
            return;
        }

        if (this.restockService.IsPresetItemListed(item))
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, "在售");
            return;
        }

        if (item.UnitPrice == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "缺价格");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("已售罄且售出前未记录价格；请在左侧填写上架单价。");
            }

            return;
        }

        var source = this.restockService.ResolveRestockSource(item);
        switch (source)
        {
            case RestockItemSource.PlayerInventory:
                ImGui.TextColored(ImGuiColors.DalamudYellow, "可补货");
                break;
            case RestockItemSource.RetainerInventory:
                ImGui.TextColored(ImGuiColors.DalamudOrange, "在雇员");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("装备在雇员背包中，补货时会自动切换到装备选择窗口的雇员标签上架。");
                }

                break;
            default:
                ImGui.TextColored(ImGuiColors.DalamudRed, "缺装备");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("背包和当前雇员中都没有找到该装备。");
                }

                break;
        }
    }

    private unsafe void UpdatePosition()
    {
        if (this.restockService.MannequinAddonAddress == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)this.restockService.MannequinAddonAddress;
            if (addon == null || !addon->IsVisible)
            {
                return;
            }

            // Attach the panel to the addon's right edge. The equipment picker
            // opens on that side, so dock to the left edge while it is visible
            // (or during a whole restock run so the panel does not bounce) —
            // ImGui always renders above the game UI and would otherwise cover
            // it. Centered dialogs (price input, prompts) do not move the panel.
            // Also fall back to the left when there is no room on screen, and
            // clamp vertically so the table never extends past the screen.
            var viewport = ImGui.GetMainViewport();
            var rightEdge = addon->X + addon->GetScaledWidth(true);
            var top = (float)(addon->Y + 2);
            var viewportBottom = viewport.Pos.Y + viewport.Size.Y;
            if (top + this.lastWindowSize.Y > viewportBottom)
            {
                top = Math.Max(viewport.Pos.Y, viewportBottom - this.lastWindowSize.Y);
            }

            var dockLeft = this.restockService.IsRestocking || this.restockService.IsEquipmentPickerVisible();
            if (dockLeft || rightEdge + this.lastWindowSize.X > viewport.Pos.X + viewport.Size.X)
            {
                ImGui.SetNextWindowPos(new Vector2(addon->X, top), ImGuiCond.Always, new Vector2(1, 0));
            }
            else
            {
                ImGui.SetNextWindowPos(new Vector2(rightEdge, top), ImGuiCond.Always, new Vector2(0, 0));
            }
        }
        catch (Exception exception)
        {
            this.pluginLog.Verbose(exception, "Unable to position mannequin restock window.");
        }
    }
}
