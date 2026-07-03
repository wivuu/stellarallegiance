using System;
using Godot;

namespace StellarAllegiance.Ui;

// Factory helpers for components that are a stock Godot control + design-system styling
// (no custom _Draw needed). Anything requiring bespoke geometry or per-frame state is a
// Control subclass instead (ChamferButton, BracketPanel, RadialGauge, …).
public static class UiKit
{
    public enum TextStyle
    {
        Display, // 34 / bold
        Title, // 22 / bold
        Label, // 13 / caps + letter-spacing
        Body, // 15 / regular
        Data, // 14 / mono
    }

    public static Label MakeLabel(string text, TextStyle style = TextStyle.Body, Color? color = null)
    {
        UiFonts.EnsureLoaded();
        var l = new Label { Text = text };
        (Font font, int size, Color def) = style switch
        {
            TextStyle.Display => (UiFonts.SairaBold, DesignTokens.DisplaySize, DesignTokens.TextHi),
            TextStyle.Title => (UiFonts.SairaBold, DesignTokens.TitleSize, DesignTokens.TextHi),
            TextStyle.Label => (UiFonts.SairaLabel, DesignTokens.LabelSize, DesignTokens.Text2),
            TextStyle.Data => (UiFonts.Mono, DesignTokens.DataSize, DesignTokens.Data),
            _ => (UiFonts.Saira, DesignTokens.BodySize, DesignTokens.TextHi),
        };
        l.AddThemeFontOverride("font", font);
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color ?? def);
        return l;
    }

    public static ChamferButton MakeButton(string text, Action? onPressed, ButtonVariant variant = ButtonVariant.Secondary)
    {
        var b = new ChamferButton
        {
            Text = text,
            Variant = variant,
            CustomMinimumSize = new Vector2(variant == ButtonVariant.Icon ? 46 : 130, variant == ButtonVariant.Icon ? 46 : 38),
        };
        if (onPressed != null)
            b.Pressed += onPressed;
        return b;
    }

    // "<label>  [====slider====]  NN%" — writes through onChanged live; the optional mono
    // readout shows the value as a percentage of its range (matching the spec), or via a
    // custom formatter when one is supplied (e.g. "1.25×" for a multiplier).
    public static HBoxContainer MakeSliderRow(
        string label,
        double min,
        double max,
        double step,
        double value,
        Action<double>? onChanged,
        bool readout = true,
        Func<double, string>? format = null
    )
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.AddChild(MakeLabel(label, TextStyle.Label).With(l => l.CustomMinimumSize = new Vector2(80, 0)));

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = value,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(240, 0),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        row.AddChild(slider);

        Label? pct = null;
        if (readout)
        {
            pct = MakeLabel(format?.Invoke(value) ?? Percent(value, min, max), TextStyle.Data);
            pct.CustomMinimumSize = new Vector2(46, 0);
            pct.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(pct);
        }
        slider.ValueChanged += v =>
        {
            onChanged?.Invoke(v);
            if (pct != null)
                pct.Text = format?.Invoke(v) ?? Percent(v, min, max);
        };
        return row;
    }

    public static CheckButton MakeToggle(string label, bool on, Action<bool>? onToggled)
    {
        var c = new CheckButton { Text = label, ButtonPressed = on };
        c.AddThemeColorOverride("font_color", DesignTokens.TextHi);
        if (onToggled != null)
            c.Toggled += p => onToggled.Invoke(p);
        return c;
    }

    public static CheckBox MakeCheckbox(string label, bool on, Action<bool>? onToggled)
    {
        var c = new CheckBox { Text = label, ButtonPressed = on };
        if (onToggled != null)
            c.Toggled += p => onToggled.Invoke(p);
        return c;
    }

    // A segmented control: one button per option, the active one rendered Primary.
    public static HBoxContainer MakeSegmented(string[] options, int selected, Action<int>? onSelect)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 2);
        var buttons = new ChamferButton[options.Length];
        for (int i = 0; i < options.Length; i++)
        {
            int idx = i;
            var b = MakeButton(options[i], null, i == selected ? ButtonVariant.Primary : ButtonVariant.Secondary);
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            b.Pressed += () =>
            {
                for (int j = 0; j < buttons.Length; j++)
                {
                    buttons[j].Variant = j == idx ? ButtonVariant.Primary : ButtonVariant.Secondary;
                    buttons[j].QueueRedraw();
                }
                onSelect?.Invoke(idx);
            };
            buttons[i] = b;
            row.AddChild(b);
        }
        return row;
    }

    // "<label>  [ - NN + ]" integer stepper, clamped to [min, max].
    public static HBoxContainer MakeStepper(string label, int value, int min, int max, Action<int>? onChanged)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.AddChild(MakeLabel(label, TextStyle.Label).With(l => l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill));

        int current = Mathf.Clamp(value, min, max);
        Label val = MakeLabel(current.ToString("00"), TextStyle.Data);
        val.CustomMinimumSize = new Vector2(40, 0);
        val.HorizontalAlignment = HorizontalAlignment.Center;

        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", 0);
        var minus = MakeButton("-", null, ButtonVariant.Secondary);
        var plus = MakeButton("+", null, ButtonVariant.Secondary);
        minus.CustomMinimumSize = plus.CustomMinimumSize = new Vector2(34, 32);
        void Step(int d)
        {
            current = Mathf.Clamp(current + d, min, max);
            val.Text = current.ToString("00");
            onChanged?.Invoke(current);
        }
        minus.Pressed += () => Step(-1);
        plus.Pressed += () => Step(1);
        box.AddChild(minus);
        box.AddChild(val);
        box.AddChild(plus);
        row.AddChild(box);
        return row;
    }

    public static OptionButton MakeSelect(string[] options, int selected, Action<int>? onSelect)
    {
        var o = new OptionButton();
        foreach (string s in options)
            o.AddItem(s);
        if (selected >= 0 && selected < options.Length)
            o.Selected = selected;
        o.ItemSelected += i => onSelect?.Invoke((int)i);
        return o;
    }

    private static string Percent(double v, double min, double max)
    {
        double f = max > min ? (v - min) / (max - min) : 0;
        return $"{Mathf.RoundToInt((float)(f * 100))}%";
    }

    // Tiny fluent helper so factories can tweak a control inline.
    private static T With<T>(this T node, Action<T> configure)
        where T : Node
    {
        configure(node);
        return node;
    }
}
