using System;
using System.Linq;
using System.Numerics;

using AllaganMarket.Mediator;
using AllaganMarket.Services;

using DalaMock.Host.Mediator;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace AllaganMarket.Windows;

public sealed class MannequinRestockWindow : ExtendedWindow
{
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
        this.Size = new Vector2(130, 32);
    }

    public override bool DrawConditions()
    {
        return this.restockService.IsMannequinWindowVisible && base.DrawConditions();
    }

    public override void Draw()
    {
        this.UpdatePosition();
        this.Flags |= ImGuiWindowFlags.NoBackground;

        var configuration = this.restockService.CurrentConfiguration;
        if (configuration == null || configuration.Items.Count == 0)
        {
            if (ImGui.Button("一键补货", new Vector2(120, 0)) && !this.restockService.IsRestocking)
            {
                this.restockService.BeginRestock();
            }

            ImGui.TextWrapped(this.restockService.StatusMessage);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("点击后采集当前模特状态；如果已保存配置，将按配置执行补货。");
            }
            return;
        }

        var plan = this.restockService.BuildRestockPlan(configuration);
        ImGui.TextUnformatted($"售罄装备：{plan.Count}");

        foreach (var item in plan)
        {
            var source = item.Source switch
            {
                RestockItemSource.PlayerInventory => "背包",
                RestockItemSource.RetainerInventory => "雇员",
                _ => "缺失",
            };
            ImGui.TextWrapped($"部位 {item.Item.EquipmentSlot}：{source}，价格 {item.Item.UnitPrice}");
        }

        ImGui.Spacing();
        var buttonLabel = this.restockService.IsRestocking ? "补货执行中..." : "一键补货";
        if (ImGui.Button(buttonLabel, new Vector2(-1, 0)) && !this.restockService.IsRestocking)
        {
            this.restockService.BeginRestock();
        }

        if (plan.Any(item => item.Source == RestockItemSource.Missing))
        {
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("部分装备不在背包或当前雇员中，无法补货。");
            }
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
            if (addon != null && addon->IsVisible)
            {
                this.Position = new Vector2(
                    addon->X + addon->GetScaledWidth(true) - 142,
                    addon->Y + addon->GetScaledHeight(true) - 44);
            }
        }
        catch (Exception exception)
        {
            this.pluginLog.Verbose(exception, "Unable to position mannequin restock window.");
        }
    }
}
