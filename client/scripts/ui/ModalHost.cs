using Godot;

namespace StellarAllegiance.Ui;

// Shared top layer for stacked modals (EscapeMenu, SettingsDialog). The screens that open
// them don't share a parent — ServerLobbyOverlay lives on ServerInputLayer (100), the lobby
// under the Hud CanvasLayer, ShipController is a bare Node — so modals mount on one
// find-or-created "ModalLayer" directly under the tree root instead. Layer 200 keeps them
// above ServerInputLayer (100) and the ConnectLayer (150).
//
// A Theme can't live on a CanvasLayer (see UiTheme): each modal's root Control applies
// UiTheme itself.
public static class ModalHost
{
    public static CanvasLayer Ensure(Node context)
    {
        var root = context.GetTree().Root;
        var layer = root.GetNodeOrNull<CanvasLayer>("ModalLayer");
        if (layer == null)
        {
            layer = new CanvasLayer { Name = "ModalLayer", Layer = 200 };
            root.AddChild(layer);
        }
        return layer;
    }
}
