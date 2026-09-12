using System;
using System.Linq;
using System.Numerics;

using AllaganMarket.Services;

using DalaMock.Host.Mediator;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace AllaganMarket.Windows;

public sealed class MannequinRestockWindow : ExtendedWindow
{
    private const float MaxWindowWidth = 320;

    private readonly MannequinRestockService restockService;
    private readonly IPluginLog pluginLog;

    public MannequinRestockWindow(
        MediatorService mediator,
        ImGuiService imGuiService,
        MannequinRestockService restockService,
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
        this.pluginLog = pluginLog;
        this.IsOpen = true;
        this.RespectCloseHotkey = false;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(150, 0),
            MaximumSize = new Vector2(MaxWindowWidth, 600),
        };
    }

    public override bool DrawConditions()
    {
        return this.restockService.IsMannequinWindowVisible && base.DrawConditions();
    }

    public override void PreDraw()
    {
        base.PreDraw();
        this.UpdatePosition();
    }

    public override void Draw()
    {
        var configuration = this.restockService.CurrentConfiguration;
        var plan = configuration == null || configuration.Items.Count == 0
            ? Array.Empty<RestockItemPlan>()
            : this.restockService.BuildRestockPlan(configuration);

        var buttonLabel = this.restockService.IsRestocking
            ? "补货执行中..."
            : plan.Count > 0
                ? $"一键补货 ({plan.Count})"
                : "一键补货";
        if (ImGui.Button(buttonLabel, new Vector2(150, 0)) && !this.restockService.IsRestocking)
        {
            this.restockService.BeginRestock();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                plan.Any(item => item.Source == RestockItemSource.Missing)
                    ? "重新上架检测到的售罄装备；标记为“缺失”的装备不在背包或当前雇员中，将被跳过。"
                    : "重新上架检测到的售罄装备。");
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MaxWindowWidth - 20);
        ImGui.TextWrapped(this.restockService.StatusMessage);

        foreach (var item in plan)
        {
            var source = item.Source switch
            {
                RestockItemSource.PlayerInventory => "背包",
                RestockItemSource.RetainerInventory => "雇员",
                _ => "缺失",
            };
            var price = item.Item.UnitPrice > 0 ? $"{item.Item.UnitPrice:N0}" : "未知";
            var quality = item.Item.IsHighQuality ? " HQ" : string.Empty;
            ImGui.TextWrapped($"{this.restockService.GetItemName(item.Item.ItemId)}{quality}：{source}，价格 {price}");
        }

        ImGui.PopTextWrapPos();
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
            if (addon != null && addon->IsVisible)
            {
                // Anchor the overlay's bottom-right corner near the addon's
                // bottom-right corner so the list grows upward instead of
                // extending past the bottom of the native window.
                ImGui.SetNextWindowPos(
                    new Vector2(
                        addon->X + addon->GetScaledWidth(true) - 12,
                        addon->Y + addon->GetScaledHeight(true) - 12),
                    ImGuiCond.Always,
                    new Vector2(1, 1));
            }
        }
        catch (Exception exception)
        {
            this.pluginLog.Verbose(exception, "Unable to position mannequin restock window.");
        }
    }
}
