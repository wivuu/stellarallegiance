using Godot;

// Stateless node-visual helpers shared by the renderers: per-instance transparency dimming and the
// one-shot dissolve-then-free tween. GeometryInstance3D.Transparency is a per-instance fade (0 opaque … 1
// invisible) that spans all of a GLB's baked materials without touching them.
public static class NodeFx
{
    // Set GeometryInstance3D.Transparency on every mesh under `node` (recursive).
    public static void DimNode(Node node, float transparency)
    {
        if (node is GeometryInstance3D gi)
            gi.Transparency = transparency;
        foreach (var child in node.GetChildren())
            DimNode(child, transparency);
    }

    // Dissolve every mesh under `node` (Transparency 0→1 over `seconds`) then free it — a quiet exit that
    // slips out under cover instead of popping (a lost-contact ship, a rock consumed under a build sphere).
    public static void QuietFade(Node3D node, float seconds)
    {
        var tween = node.CreateTween();
        int faded = 0;
        FadeMeshes(node, tween, seconds, ref faded);
        if (faded == 0)
        {
            node.QueueFree(); // nothing to fade (shouldn't happen) — just drop it
            return;
        }
        tween.Chain().TweenCallback(Callable.From(node.QueueFree));
    }

    // Add a parallel Transparency 0→1 tween for every GeometryInstance3D under `node`.
    private static void FadeMeshes(Node node, Tween tween, float seconds, ref int count)
    {
        if (node is GeometryInstance3D gi)
        {
            tween.Parallel().TweenProperty(gi, "transparency", 1f, seconds);
            count++;
        }
        foreach (var child in node.GetChildren())
            FadeMeshes(child, tween, seconds, ref count);
    }
}
