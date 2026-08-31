using System;
using System.Linq;
using System.Numerics;

using AllaganMarket.Mediator;
using AllaganMarket.Services;
using AllaganMarket.Services.Interfaces;
using AllaganMarket.Settings;

using DalaMock.Host.Mediator;
using DalaMock.Shared.Interfaces;

using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

using Humanizer;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AllaganMarket.Windows;

public class RetainerListOverlayWindow : OverlayWindow
{
    private readonly ICharacterMonitorService characterMonitorService;
    private readonly SaleTrackerService saleTrackerService;
    private readonly IClientState clientState;
    private readonly Configuration configuration;
    private readonly ItemUpdatePeriodSetting updatePeriodSetting;
    private readonly IFont font;
    private readonly RetainerOverlayCollapsedSetting overlayCollapsedSetting;
    private readonly ShowRetainerOverlaySetting retainerOverlaySetting;
    private readonly UndercutService undercutService;
    private readonly AutoUndercutService autoUndercutService;
    private readonly HighlightingRetainerListSetting retainerListSetting;
    private readonly LocalizationService localization;
    private bool showAllRetainers = true;

    public RetainerListOverlayWindow(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IPluginLog logger,
        MediatorService mediator,
        ImGuiService imGuiService,
        ICharacterMonitorService characterMonitorService,
        SaleTrackerService saleTrackerService,
        IClientState clientState,
        Configuration configuration,
        ItemUpdatePeriodSetting updatePeriodSetting,
        IFont font,
        RetainerOverlayCollapsedSetting overlayCollapsedSetting,
        ShowRetainerOverlaySetting retainerOverlaySetting,
        UndercutService undercutService,
        AutoUndercutService autoUndercutService,
        HighlightingRetainerListSetting retainerListSetting,
        LocalizationService localization)
        : base(addonLifecycle, gameGui, logger, mediator, imGuiService, "Retainer List Overlay")
    {
        this.characterMonitorService = characterMonitorService;
        this.saleTrackerService = saleTrackerService;
        this.clientState = clientState;
        this.configuration = configuration;
        this.updatePeriodSetting = updatePeriodSetting;
        this.font = font;
        this.overlayCollapsedSetting = overlayCollapsedSetting;
        this.retainerOverlaySetting = retainerOverlaySetting;
        this.undercutService = undercutService;
        this.autoUndercutService = autoUndercutService;
        this.retainerListSetting = retainerListSetting;
        this.localization = localization;
        this.AttachAddon("RetainerList", AttachPosition.Right);
        this.Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize |
                     ImGuiWindowFlags.NoSavedSettings;
        this.Size = null;
        this.RespectCloseHotkey = false;
    }

    public bool IsCollapsed
    {
        get => this.overlayCollapsedSetting.CurrentValue(this.configuration);

        set => this.overlayCollapsedSetting.UpdateFilterConfiguration(this.configuration, value);
    }

    public override bool DrawConditions()
    {
        return this.retainerOverlaySetting.CurrentValue(this.configuration) && base.DrawConditions();
    }

    public override void PreOpenCheck()
    {
        base.PreOpenCheck();
        if (this.IsOpen && !this.clientState.IsLoggedIn)
        {
            this.IsOpen = false;
        }
    }

    public override void Draw()
    {
        var collapsed = this.IsCollapsed;

        var currentCursorPosX = ImGui.GetCursorPosX();

        this.SizeConstraints = new WindowSizeConstraints()
        {
            MaximumSize = new Vector2(520, 800) * ImGui.GetIO().FontGlobalScale,
        };

        if (collapsed && ImGuiService.DrawIconButton(this.font, FontAwesomeIcon.ChevronRight, ref currentCursorPosX))
        {
            this.IsCollapsed = false;
        }

        if (!collapsed && ImGuiService.DrawIconButton(this.font, FontAwesomeIcon.ChevronLeft, ref currentCursorPosX))
        {
            this.IsCollapsed = true;
        }

        if (collapsed)
        {
            return;
        }

        ImGui.SameLine();
        this.localization.RefreshFromConfiguration();
        ImGui.Text(this.localization.Get("Overlay.Title"));

        ImGui.SameLine();

        currentCursorPosX = ImGui.GetWindowSize().X;

        if (ImGuiService.DrawIconButton(
                this.font,
                FontAwesomeIcon.Bars,
                ref currentCursorPosX,
                this.localization.Get("Overlay.OpenMainWindow"),
                true))
        {
            this.MediatorService.Publish(new ToggleWindowMessage(typeof(MainWindow)));
        }

        ImGui.SameLine();

        if (ImGuiService.DrawIconButton(
                this.font,
                FontAwesomeIcon.Cog,
                ref currentCursorPosX,
                this.localization.Get("Overlay.OpenConfigWindow"),
                true))
        {
            this.MediatorService.Publish(new ToggleWindowMessage(typeof(ConfigWindow)));
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(this.autoUndercutService.IsRunning))
        {
            if (ImGuiService.DrawIconButton(
                    this.font,
                    FontAwesomeIcon.Play,
                    ref currentCursorPosX,
                    this.localization.Get("Overlay.StartAutoUndercut"),
                    true))
            {
                this.autoUndercutService.Start();
            }
        }

        ImGui.SameLine();

        if (ImGuiService.DrawIconButton(
                this.font,
                FontAwesomeIcon.Eye,
                ref currentCursorPosX,
                this.localization.Get("Overlay.ShowHideRetainers"),
                true,
                this.showAllRetainers ? null : ImGuiColors.ParsedGrey))
        {
            this.showAllRetainers = !this.showAllRetainers;
        }

        ImGui.SameLine();

        var retainerHighlighting = this.retainerListSetting.CurrentValue(this.configuration);
        if (ImGuiService.DrawIconButton(
                this.font,
                FontAwesomeIcon.Lightbulb,
                ref currentCursorPosX,
                this.localization.Get("Overlay.ToggleRetainerListHighlighting"),
                true,
                retainerHighlighting ? null : ImGuiColors.ParsedGrey))
        {
            this.retainerListSetting.UpdateFilterConfiguration(this.configuration, !retainerHighlighting);
        }

        ImGui.Separator();

        var activeCharacter = this.characterMonitorService.ActiveCharacter;
        if (this.clientState.IsLoggedIn && activeCharacter != null)
        {
            var retainers = this.characterMonitorService.GetRetainers(activeCharacter.CharacterId)
                                .OrderBy(c => c.DisplayOrder).ToList();
            var interval = this.updatePeriodSetting.CurrentValue(this.configuration);
            var retainersToCheck = false;

            using (ImRaii.Table("RetainerList", 5, ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn(this.localization.Get("Overlay.Name"), ImGuiTableColumnFlags.WidthFixed, 100 * ImGui.GetIO().FontGlobalScale);
                ImGui.TableSetupColumn(this.localization.Get("Overlay.Selling"), ImGuiTableColumnFlags.WidthFixed, 50 * ImGui.GetIO().FontGlobalScale);
                ImGui.TableSetupColumn(this.localization.Get("Overlay.Undercut"), ImGuiTableColumnFlags.WidthFixed, 70 * ImGui.GetIO().FontGlobalScale);
                ImGui.TableSetupColumn(this.localization.Get("Overlay.LastUpdate"), ImGuiTableColumnFlags.WidthFixed, 130 * ImGui.GetIO().FontGlobalScale);
                ImGui.TableSetupColumn(this.localization.Get("Overlay.AutoUndercut"), ImGuiTableColumnFlags.WidthFixed, 90 * ImGui.GetIO().FontGlobalScale);
                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.Name"));
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.Selling"));
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.Undercut"));
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.StalePricing"));
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.AutoUndercut"));
                foreach (var retainer in retainers)
                {
                    var isUnderCut = false;
                    var needsUpdate = false;
                    DateTime? nextUpdate = null;
                    var sellingCount = 0;
                    if (this.saleTrackerService.SaleItems.TryGetValue(retainer.CharacterId, out var value))
                    {
                        isUnderCut = value.Any(c => this.undercutService.IsItemUndercut(c) ?? false);
                        needsUpdate = value.Any(c => !c.IsEmpty() && this.undercutService.NeedsUpdate(c, interval));
                        nextUpdate = value.Where(c => !c.IsEmpty()).DefaultIfEmpty()
                                         .Min(c => c == null ? null : (DateTime?)this.undercutService.NextUpdateDate(c, interval));
                        sellingCount = value.Count(c => !c.IsEmpty());
                    }

                    if (!isUnderCut && !needsUpdate && !this.showAllRetainers)
                    {
                        continue;
                    }

                    retainersToCheck = true;
                    ImGui.TableNextRow();
                    using var green = ImRaii.PushColor(
                        ImGuiCol.Text,
                        ImGuiColors.HealerGreen,
                        !isUnderCut && !needsUpdate);
                    using var yellow = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, needsUpdate);
                    using var red = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed, isUnderCut);
                    ImGui.TableNextColumn();
                    ImGui.Text(retainer.Name);
                    green.Pop();
                    yellow.Pop();
                    red.Pop();

                    ImGui.TableNextColumn();
                    ImGui.Text(sellingCount.ToString());

                    ImGui.TableNextColumn();
                    ImGui.Text(isUnderCut ? this.localization.Get("Overlay.Yes") : this.localization.Get("Overlay.No"));

                    ImGui.TableNextColumn();
                    var needsUpdateText = needsUpdate ? this.localization.Get("Overlay.Yes") : this.localization.Get("Overlay.No");
                    if (nextUpdate != null)
                    {
                        var timeSpan = nextUpdate - DateTime.Now;
                        needsUpdateText += " (" + timeSpan.Value.Humanize(culture: this.localization.Culture) + ")";
                    }

                    ImGui.Text(needsUpdateText);

                    ImGui.TableNextColumn();
                    var autoUndercut = retainer.AutoUndercut;
                    if (ImGui.Checkbox($"##auto-undercut-{retainer.CharacterId}", ref autoUndercut) &&
                        autoUndercut != retainer.AutoUndercut)
                    {
                        retainer.AutoUndercut = autoUndercut;
                        // Keep both references in sync. CharacterMonitor normally
                        // shares this dictionary with Configuration, but an addon
                        // refresh can replace a Character instance before the next
                        // automation run.
                        this.characterMonitorService.Characters[retainer.CharacterId] = retainer;
                        this.configuration.Characters[retainer.CharacterId] = retainer;
                        this.configuration.IsDirty = true;
                    }
                }
            }

            if (!retainersToCheck)
            {
                ImGui.Text(this.localization.Get("Overlay.NoRetainersLeft"));
            }
        }
        else
        {
            ImGui.Text(this.localization.Get("Overlay.PleaseLogin"));
        }
    }
}
