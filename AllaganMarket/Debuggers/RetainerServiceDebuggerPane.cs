using System.Globalization;

using AllaganLib.Shared.Interfaces;

using AllaganMarket.Services.Interfaces;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AllaganMarket.Debugging;

public class RetainerServiceDebuggerPane : IDebugPane
{
    private readonly IRetainerService retainerService;

    public RetainerServiceDebuggerPane(IRetainerService retainerService)
    {
        this.retainerService = retainerService;
    }

    public string Name => "Retainer Service Debugger";

    public void Draw()
    {
        using var child = ImRaii.Child("RetainerServiceDebuggerPaneChild", new System.Numerics.Vector2(0, 0), true);

        if (!child.Success)
        {
            return;
        }

        ImGui.Text("RetainerService State");
        ImGui.Separator();

        using var table = ImRaii.Table(
            "RetainerServiceTable",
            2,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp);

        if (!table.Success)
        {
            return;
        }

        ImGui.TableSetupColumn("Property");
        ImGui.TableSetupColumn("Value");
        ImGui.TableHeadersRow();

        DrawRow("Retainer Id", this.retainerService.RetainerId == 0 ? "(none)" : this.retainerService.RetainerId.ToString(CultureInfo.InvariantCulture));
        DrawRow("Retainer World Id", this.retainerService.RetainerWorldId == 0 ? "(none)" : this.retainerService.RetainerWorldId.ToString(CultureInfo.InvariantCulture));
        DrawRow("Retainer Gil", this.retainerService.RetainerGil.ToString("N0", CultureInfo.InvariantCulture));
    }

    private static void DrawRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text(label);
        ImGui.TableNextColumn();
        ImGui.Text(value);
    }
}