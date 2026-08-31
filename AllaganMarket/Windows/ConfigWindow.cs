using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using AllaganLib.Interface.Widgets;
using AllaganLib.Interface.Wizard;
using AllaganLib.Shared.Extensions;

using AllaganMarket.Mediator;
using AllaganMarket.Services;
using AllaganMarket.Settings;
using AllaganMarket.Settings.Layout;

using DalaMock.Host.Mediator;

using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using Serilog.Events;

// ReSharper disable DisposeOnUsingVariable
namespace AllaganMarket.Windows;

public class ConfigWindow : ExtendedWindow, IDisposable
{
    private readonly LocalizationService localization;
    private readonly Configuration configuration;
    private readonly IConfigurationWizardService<Configuration> configurationWizardService;
    private readonly SettingTypeConfiguration settingTypeConfiguration;
    private readonly IPluginLog pluginLog;
    private readonly VerticalSplitter verticalSplitter;
    private readonly List<IGrouping<SettingType, ISetting>> settings;
    private SettingType currentSettingType;
    private readonly Dictionary<SettingType, SettingPage> settingPages;

    public ConfigWindow(
        MediatorService mediatorService,
        ImGuiService imGuiService,
        LocalizationService localization,
        Configuration configuration,
        IEnumerable<ISetting> settings,
        IConfigurationWizardService<Configuration> configurationWizardService,
        SettingTypeConfiguration settingTypeConfiguration,
        IEnumerable<SettingPage> settingPages,
        IPluginLog pluginLog)
        : base(mediatorService, imGuiService, localization.Get("Window.Config.Title") + "##ConfigWindow")
    {
        this.localization = localization;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.Size = new Vector2(600, 600);
        this.SizeCondition = ImGuiCond.FirstUseEver;

        this.configuration = configuration;
        this.configurationWizardService = configurationWizardService;
        this.settingTypeConfiguration = settingTypeConfiguration;
        this.pluginLog = pluginLog;
        this.settingPages = settingPages.ToDictionary(c => c.SettingType, c => c);
        this.settings =
            [.. settings.Where(c => c.ShowInSettings).GroupBy(c => c.Type).OrderBy(c => settingTypeConfiguration.GetCategoryOrder().IndexOf(c.Key))];
        this.LocalizeSettings();
        this.Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.MenuBar;
        this.verticalSplitter = new VerticalSplitter(150, new Vector2(100, 200));
        this.currentSettingType = this.settings.First().Key;
    }

    public override void PreDraw()
    {
        // Flags must be added or removed before Draw() is being called, or they won't apply
        if (this.configuration.IsConfigWindowMovable)
        {
            this.Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            this.Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        this.localization.RefreshFromConfiguration();
        this.LocalizeSettings();
        if (ImGui.BeginMenuBar())
        {
            if (ImGui.BeginMenu(this.localization.Get("Menu.File")))
            {
                if (ImGui.MenuItem(this.localization.Get("Menu.MainWindow")))
                {
                    this.MediatorService.Publish(new OpenWindowMessage(typeof(MainWindow)));
                }

                if (ImGui.MenuItem(this.localization.Get("Menu.ReportIssue")))
                {
                    "https://github.com/Critical-Impact/AllaganMarket".OpenBrowser();
                }

                if (ImGui.MenuItem(this.localization.Get("Menu.VerboseLogging"), "", this.pluginLog.MinimumLogLevel == LogEventLevel.Verbose))
                {
                    if (this.pluginLog.MinimumLogLevel == LogEventLevel.Verbose)
                    {
                        this.pluginLog.MinimumLogLevel = LogEventLevel.Debug;
                    }
                    else
                    {
                        this.pluginLog.MinimumLogLevel = LogEventLevel.Verbose;
                    }
                }

                if (ImGui.MenuItem(this.localization.Get("Menu.KoFi")))
                {
                    "https://ko-fi.com/critical_impact".OpenBrowser();
                }

                if (ImGui.MenuItem(this.localization.Get("Menu.Close")))
                {
                    this.IsOpen = false;
                }

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu(this.localization.Get("Menu.Wizard")))
            {
                var hasNewFeatures = this.configurationWizardService.HasNewFeatures;
                using var disabled = ImRaii.Disabled(!hasNewFeatures);
                if (ImGui.MenuItem(this.localization.Get("Menu.ConfigureNewFeatures")))
                {
                    this.MediatorService.Publish(new OpenWindowMessage(typeof(WizardWindow)));
                }

                disabled.Dispose();

                if (ImGui.MenuItem(this.localization.Get("Menu.ReconfigureAllFeatures")))
                {
                    this.configurationWizardService.ClearFeaturesSeen();
                    this.MediatorService.Publish(new OpenWindowMessage(typeof(WizardWindow)));
                }

                ImGui.EndMenu();
            }

            ImGui.EndMenuBar();
        }

        this.verticalSplitter.Draw(
            () =>
            {
                ImGui.Text(this.localization.Get("Window.Config.Configuration"));
                ImGui.Separator();
                foreach (var group in this.settings)
                {
                    if (ImGui.Selectable(this.localization.Get($"SettingType.{group.Key}"), group.Key == this.currentSettingType))
                    {
                        this.currentSettingType = group.Key;
                    }
                }
            },
            () =>
            {
                foreach (var group in this.settings)
                {
                    if (group.Key == this.currentSettingType)
                    {
                        if (this.settingPages.ContainsKey(group.Key))
                        {
                            this.settingPages[group.Key].Draw(this.configuration);
                        }
                        else
                        {
                            foreach (var setting in group)
                            {
                                this.localization.Localize(setting);
                                setting.Draw(this.configuration);
                            }
                        }
                    }
                }
            });
    }

    private void LocalizeSettings()
    {
        foreach (var setting in this.settings.SelectMany(group => group))
        {
            this.localization.Localize(setting);
        }
    }
}
