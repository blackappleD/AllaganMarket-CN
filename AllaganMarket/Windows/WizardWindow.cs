using System;
using System.Numerics;

using AllaganLib.Interface.Widgets;

using AllaganMarket.Services;
using AllaganMarket.Widgets;

using DalaMock.Host.Mediator;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AllaganMarket.Windows;

public class WizardWindow : ExtendedWindow, IDisposable
{
    private readonly LocalizedWizardWidget<Configuration> wizardWidget;

    public WizardWindow(
        LocalizedWizardWidget<Configuration> wizardWidget,
        MediatorService mediatorService,
        ImGuiService imGuiService,
        LocalizationService localization)
        : base(mediatorService, imGuiService, localization.Get("Window.Wizard.Title") + "##WizardWindow")
    {
        this.wizardWidget = wizardWidget;
        this.wizardWidget.OnClosed += this.WizardWidgetOnOnClosed;
        this.Size = new Vector2(800, 500);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(800, 350),
            MaximumSize = new Vector2(1000, 1000),
        };
    }

    public override void OnOpen()
    {
        this.wizardWidget.Initialize();
        base.OnOpen();
    }

    public override void Draw()
    {
        this.wizardWidget.Draw();
    }

    public new void Dispose()
    {
        this.Dispose(true);
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.wizardWidget.OnClosed -= this.WizardWidgetOnOnClosed;
        }
    }

    private void WizardWidgetOnOnClosed()
    {
        this.IsOpen = false;
    }
}
