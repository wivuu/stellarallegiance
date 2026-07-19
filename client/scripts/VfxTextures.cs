using Godot;

// Shared VFX texture builders — DRY home for the small procedural textures that keep getting
// copy-pasted across one-shot/looping effects (EngineGlow cores, ExplosionEffect flash/embers,
// HitFlash, BaseBeacon nav lights, ...). Each call builds a fresh instance (no caching) so no
// caller can stomp another's copy by mutating the returned texture/gradient.
public static class VfxTextures
{
    // Soft round mote: hot centre fading to transparent, radial fill from the texture's centre
    // out to its edge — drives bloom via emission (texture/energy, not raw colour). `size` is
    // the square texture's width/height in px; callers with a tighter on-screen mote (e.g. base
    // nav beacons) pass a smaller size.
    public static GradientTexture2D RadialDot(int size = 128)
    {
        var gradient = new Gradient
        {
            Offsets = [0f, 0.5f, 1f],
            Colors = [new Color(1f, 1f, 1f, 1f), new Color(1f, 1f, 1f, 0.4f), new Color(1f, 1f, 1f, 0f)],
        };
        return new GradientTexture2D
        {
            Gradient = gradient,
            Width = size,
            Height = size,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0f),
        };
    }
}
