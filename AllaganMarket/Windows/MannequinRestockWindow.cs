using System;
using System.Linq;
using System.Numerics;

using AllaganMarket.Services;

using DalaMock.Host.Mediator;

using Dalamud.Bindings.ImGui;
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

    private readonly MannequinRestockService restockService;
    private readonly Configuration configuration;
    private readonly ITextureProvider textureProvider;
    private readonly ExcelSheet<Item> itemSheet;
    private readonly IPluginLog pluginLog;
    private Vector2 lastWindowSize;

    public MannequinRestockWindow(
        MediatorService mediator,
        ImGuiService imGuiService,
        MannequinRestockService restockService,
        Configuration configuration,
        ITextureProvider textureProvider,
        ExcelSheet<Item> itemSheet,
        IPluginLog pluginLog)
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

        // ImGui overlays always render above the native UI, so hide the panel
        // while the user is interacting with the price/equipment dialogs to
        // avoid covering them. Keep it visible during automated restocking so
        // the progress status stays readable.
        if (!this.restockService.IsRestocking && this.restockService.IsOverlayObstructed())
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
        var restockConfiguration = this.restockService.CurrentConfiguration;
        var plan = restockConfiguration == null || restockConfiguration.Items.Count == 0
            ? Array.Empty<RestockItemPlan>()
            : this.restockService.BuildRestockPlan(restockConfiguration);
        var actionable = plan.Count(item => item.Source != RestockItemSource.Missing && item.Item.UnitPrice > 0);

        this.DrawHeader(plan.Count, actionable);
        if (this.IsCollapsed)
        {
            return;
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 340);
        ImGui.TextWrapped(this.restockService.StatusMessage);
        ImGui.PopTextWrapPos();

        if (restockConfiguration == null || restockConfiguration.Items.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "尚未读取到模特装备数据。");
            return;
        }

        ImGui.Separator();
        this.DrawOptions();
        this.DrawSlotTable(restockConfiguration);
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

        ImGui.SameLine();
        if (ImGui.Button(this.IsCollapsed ? "展开预设 ▼" : "收起 ▲"))
        {
            this.IsCollapsed = !this.IsCollapsed;
        }
    }

    private void DrawSlotTable(Models.MannequinConfiguration restockConfiguration)
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
        ImGui.TableSetupColumn("装备", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("价格", ImGuiTableColumnFlags.WidthFixed, PriceInputWidth + 70);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableHeadersRow();

        foreach (var item in restockConfiguration.Items.OrderBy(item => item.EquipmentSlot))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            this.DrawItemIcon(item);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(this.restockService.GetItemName(item.ItemId));

            ImGui.TableNextColumn();
            var price = (int)item.UnitPrice;
            ImGui.SetNextItemWidth(PriceInputWidth);
            ImGui.InputInt($"##price{item.EquipmentSlot}_{item.ItemId}", ref price, 0, 0);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                this.restockService.UpdatePresetItem(
                    item.EquipmentSlot,
                    (uint)Math.Max(0, price),
                    item.IsHighQuality);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("补货时的上架单价；装备在售时会自动记录。");
            }

            ImGui.SameLine();
            var isHighQuality = item.IsHighQuality;
            if (ImGui.Checkbox($"HQ##{item.EquipmentSlot}_{item.ItemId}", ref isHighQuality))
            {
                this.restockService.UpdatePresetItem(item.EquipmentSlot, item.UnitPrice, isHighQuality);
            }

            ImGui.TableNextColumn();
            this.DrawSlotStatus(item);
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

    private void DrawSlotStatus(Models.MannequinItem item)
    {
        if (!item.IsSoldOut)
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
                    ImGui.SetTooltip("装备在雇员背包中，请先取出到自己背包。");
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

            // Attach the panel to the addon's right edge; fall back to the left
            // edge when there is no room on screen, and clamp vertically so the
            // table never extends past the bottom of the screen.
            var viewport = ImGui.GetMainViewport();
            var rightEdge = addon->X + addon->GetScaledWidth(true);
            var top = (float)(addon->Y + 2);
            var viewportBottom = viewport.Pos.Y + viewport.Size.Y;
            if (top + this.lastWindowSize.Y > viewportBottom)
            {
                top = Math.Max(viewport.Pos.Y, viewportBottom - this.lastWindowSize.Y);
            }

            if (rightEdge + this.lastWindowSize.X > viewport.Pos.X + viewport.Size.X)
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
