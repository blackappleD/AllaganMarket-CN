using System;
using System.Collections.Generic;
using System.Numerics;

using AllaganLib.Interface.FormFields;
using AllaganLib.Interface.Wizard;
using AllaganLib.Interface.Widgets;

using AllaganMarket.Services;
using AllaganMarket.Settings;

using ImGuiService = AllaganLib.Interface.Services.ImGuiService;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;

namespace AllaganMarket.Widgets;

/// <summary>Wizard widget with plugin-owned strings instead of the library's fixed English labels.</summary>
public class LocalizedWizardWidget<T>
    where T : class, IWizardConfiguration
{
    public delegate void ClosedDelegate();

    private readonly WizardWidgetSettings widgetSettings;
    private readonly ImGuiService imGuiService;
    private readonly IConfigurationWizardService<T> configurationWizardService;
    private readonly LocalizationService localization;
    private readonly T configuration;
    private List<IFeature<T>> availableFeatures = [];
    private int currentFeature;

    public LocalizedWizardWidget(
        WizardWidgetSettings widgetSettings,
        ImGuiService imGuiService,
        IConfigurationWizardService<T> configurationWizardService,
        LocalizationService localization,
        T configuration)
    {
        this.widgetSettings = widgetSettings;
        this.imGuiService = imGuiService;
        this.configurationWizardService = configurationWizardService;
        this.localization = localization;
        this.configuration = configuration;
        this.Initialize();
    }

    public event ClosedDelegate? OnClosed;

    public void Initialize()
    {
        this.availableFeatures = this.configurationWizardService.GetNewFeatures();
        this.currentFeature = 0;
    }

    public void Draw()
    {
        this.localization.RefreshFromConfiguration();

        using (var sidebar = ImRaii.Child("wizardSidebar", new Vector2(200, 0) * ImGui.GetIO().FontGlobalScale, true))
        {
            if (sidebar)
            {
                using (var menu = ImRaii.Child("wizardSidebarMenu", new Vector2(200, -195) * ImGui.GetIO().FontGlobalScale))
                {
                    if (menu)
                    {
                        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, this.currentFeature == 0))
                        {
                            ImGui.Text(this.localization.Get("Wizard.Welcome"));
                        }

                        for (var index = 0; index < this.availableFeatures.Count; index++)
                        {
                            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, index + 1 == this.currentFeature))
                            {
                                ImGui.Text($"{index + 1}. {this.availableFeatures[index].Name}");
                            }
                        }
                    }
                }

                using (var image = ImRaii.Child("wizardSidebarImage", new Vector2(200, 0) * ImGui.GetIO().FontGlobalScale, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                {
                    if (image)
                    {
                        ImGui.Separator();
                        ImGui.Image(this.imGuiService.LoadImage(this.widgetSettings.LogoPath).GetWrapOrEmpty().Handle, new Vector2(190, 190));
                    }
                }
            }
        }

        ImGui.SameLine();
        using var mainWindow = ImRaii.Child("wizardMainWindow", new Vector2(0, 0));
        if (!mainWindow)
        {
            return;
        }

        using (var mainContainer = ImRaii.Child("wizardMainContainer", new Vector2(-1, -80) * ImGui.GetIO().FontGlobalScale, true))
        {
            if (mainContainer)
            {
                this.DrawContent();
            }
        }

        using var navigation = ImRaii.Child("wizardNavigation", new Vector2(-1, -1) * ImGui.GetIO().FontGlobalScale, true);
        if (navigation)
        {
            this.DrawNavigation();
        }
    }

    private void DrawContent()
    {
        if (this.currentFeature == 0)
        {
            var welcomeKey = this.configurationWizardService.ConfiguredOnce
                ? "Wizard.WelcomeBack"
                : "Wizard.WelcomeFirstRun";
            ImGui.TextWrapped(this.localization.Format(welcomeKey, this.widgetSettings.PluginName));
            ImGui.Separator();
            ImGui.TextWrapped(this.localization.Get(
                this.configurationWizardService.ConfiguredOnce
                    ? "Wizard.NewFeaturesDescription"
                    : "Wizard.FirstRunDescription"));
            return;
        }

        var feature = this.availableFeatures[this.currentFeature - 1];
        ImGui.Text(feature.Name);
        ImGui.Separator();
        ImGui.PushTextWrapPos();
        ImGui.Text(feature.Description);
        ImGui.PopTextWrapPos();
        ImGui.Separator();

        foreach (var setting in this.configurationWizardService.GetApplicableSettings(feature))
        {
            if (setting is ISetting pluginSetting)
            {
                this.localization.Localize(pluginSetting);
            }

            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 5);
            setting.LabelSize = (int)(ImGui.GetWindowContentRegionMax().X - 20);
            setting.Draw(this.configuration);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 5);
        }
    }

    private void DrawNavigation()
    {
        if (this.currentFeature == 0)
        {
            if (this.configurationWizardService.ConfiguredOnce)
            {
                if (ImGui.Button(this.localization.Get("Wizard.Continue")))
                {
                    this.NextStep();
                    this.configuration.ShowWizardNewFeatures = true;
                }

                if (ImGui.Button(this.localization.Get("Wizard.CloseShowNext")))
                {
                    this.Close(true);
                }

                return;
            }

            if (ImGui.Button(this.localization.Get("Wizard.ContinueShowNew")))
            {
                this.NextStep();
                this.configuration.ShowWizardNewFeatures = true;
            }

            ImGui.SameLine();
            if (ImGui.Button(this.localization.Get("Wizard.ContinueNeverShow")))
            {
                this.NextStep();
                this.configuration.ShowWizardNewFeatures = false;
            }

            if (ImGui.Button(this.localization.Get("Wizard.CloseShowNext")))
            {
                this.Close(true);
            }

            ImGui.SameLine();
            if (ImGui.Button(this.localization.Get("Wizard.CloseNeverShow")))
            {
                this.Close(false);
            }

            return;
        }

        using (ImRaii.Disabled(this.currentFeature == 0))
        {
            if (ImGui.Button(this.localization.Get("Wizard.Previous")))
            {
                this.currentFeature--;
            }
        }

        ImGui.SameLine();
        if (this.currentFeature < this.availableFeatures.Count)
        {
            if (ImGui.Button(this.localization.Get("Wizard.Next")))
            {
                this.NextStep();
            }
        }
        else if (ImGui.Button(this.localization.Get("Wizard.Finish")))
        {
            this.Finish();
        }
    }

    private void NextStep()
    {
        if (this.currentFeature < this.availableFeatures.Count)
        {
            this.currentFeature++;
        }
    }

    private void Close(bool showNextTime)
    {
        this.configuration.ShowWizardNewFeatures = showNextTime;
        this.OnClosed?.Invoke();
    }

    private void Finish()
    {
        this.OnClosed?.Invoke();
        this.currentFeature = 0;
        foreach (var feature in this.availableFeatures)
        {
            feature.OnFinish();
        }

        this.configurationWizardService.MarkFeaturesSeen();
    }
}
