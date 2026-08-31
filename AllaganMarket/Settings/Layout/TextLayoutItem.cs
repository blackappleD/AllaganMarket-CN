using Autofac;

using AllaganMarket.Services;

using Dalamud.Bindings.ImGui;

namespace AllaganMarket.Settings.Layout;

public class TextLayoutItem : ISettingLayoutItem
{
    private readonly string key;
    private readonly LocalizationService localization;

    public TextLayoutItem(string key, LocalizationService localization)
    {
        this.key = key;
        this.localization = localization;
    }

    public void Draw(Configuration configuration, int? labelSize = null, int? inputSize = null)
    {
        ImGui.Text(this.localization.Get(this.key));
    }

    public void Build(IComponentContext context)
    {
    }
}
