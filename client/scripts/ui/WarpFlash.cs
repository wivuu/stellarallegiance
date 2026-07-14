using Godot;

namespace StellarAllegiance.Ui;

// Full-screen "jump" flash played when the local ship warps through an aleph gate. A sector warp
// hard-swaps the whole rendered rock field (and, on a first fog reveal, instantiates it in one batch);
// this brief cyan bloom covers that swap so the transition reads as a deliberate jump rather than a
// pop of asteroids appearing/disappearing. Fired off WorldRenderer.Warped (see Hud). Purely cosmetic
// and never interactive: the ColorRect ignores the mouse so it can't eat clicks while it's up.
public partial class WarpFlash : CanvasLayer
{
    // Above the gameplay HUD but BELOW the ConnectLayer/modals (150) so a warp mid-dialog never
    // paints over the dialog. Warps rarely coincide with modals, but keep the ordering honest.
    private const int FlashLayer = 140;

    // Play() ramps to peak fast and HOLDS there (the hard sector swap happens fully covered); Release()
    // eases back out once WorldRenderer reports the destination sector loaded. The hold has no fixed
    // duration — it lasts exactly as long as the load does (see WorldRenderer.TickWarpSettle).
    // Public so WorldRenderer can time its deferred sector swap to the flash reaching peak (Phase B runs
    // only once the flash is fully up) — the cover delay can't drift from the actual ramp this way.
    public const float RiseDur = 0.12f;
    private const float FallDur = 0.28f;
    private const float PeakAlpha = 0.9f;

    private ColorRect _rect = null!;
    private Tween? _tween;

    public override void _Ready()
    {
        Layer = FlashLayer;

        // Cyan structural accent (faction-tinted at runtime) lifted toward white so the bloom reads as
        // the game's own chrome, not a raw white frame. Built from tokens, not a hardcoded colour.
        Color flash = DesignTokens.TeamAccent.Lerp(Colors.White, 0.45f);
        flash.A = 0f; // start invisible; Play() ramps the alpha

        _rect = new ColorRect
        {
            Name = "Flash",
            Color = flash,
            MouseFilter = Control.MouseFilterEnum.Ignore, // cosmetic overlay — never intercept input
        };
        AddChild(_rect);
        _rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); // code-built overlay: use the preset
    }

    // Raise the flash: alpha ramps 0 → peak and HOLDS there until Release(). Re-entrant — a second warp
    // mid-flash just re-ramps from the current alpha and holds again.
    public void Play()
    {
        _tween?.Kill();
        _tween = CreateTween();
        _tween.TweenProperty(_rect, "color:a", PeakAlpha, RiseDur).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
    }

    // Clear the flash: ease the alpha back to 0, revealing the (now loaded) destination sector. Safe to
    // call when already clear — it just re-runs a no-op fade from alpha 0.
    public void Release()
    {
        _tween?.Kill();
        _tween = CreateTween();
        _tween.TweenProperty(_rect, "color:a", 0f, FallDur).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
    }
}
