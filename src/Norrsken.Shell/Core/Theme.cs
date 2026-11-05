using System.Windows;
using System.Windows.Media;

namespace Norrsken.Shell.Core;

/// <summary>
/// Norrsken themes are strictly monochrome: the only colour on screen comes from the apps
/// themselves (their icons and windows). Every brush is a DynamicResource, so switching is live.
/// </summary>
public static class Theme
{
    public sealed record Info(string Id, string Name);

    public static readonly IReadOnlyList<Info> All =
    [
        new("graphite", "Graphite"),
        new("ash", "Ash"),
        new("slate", "Slate"),
        new("ink", "Ink"),
        new("paper", "Paper"),
        new("fog", "Fog"),
        new("sand", "Sand"),
        new("high-contrast", "High Contrast"),
    ];

    // DockBg/DockLine: the floating dock. Surface: translucent popups. SurfaceSolid: opaque windows.
    // Dot: running indicator. PebbleFill: resting window-control colour.
    static readonly Dictionary<string, Dictionary<string, string>> Palettes = new()
    {
        ["graphite"] = new()
        {
            ["DockBg"] = "#D41E1E1E", ["DockLine"] = "#26FFFFFF", ["Surface"] = "#F51A1A1A", ["SurfaceSolid"] = "#1A1A1A",
            ["SurfaceAlt"] = "#10FFFFFF", ["Border"] = "#1AFFFFFF", ["Text"] = "#F2F2F2", ["TextDim"] = "#8E8E8E",
            ["Hover"] = "#12FFFFFF", ["Pressed"] = "#20FFFFFF", ["Selection"] = "#1CFFFFFF", ["Dot"] = "#E6FFFFFF",
            ["PebbleFill"] = "#4A4A4A",
        },
        ["paper"] = new()
        {
            ["DockBg"] = "#DCF6F6F6", ["DockLine"] = "#1C000000", ["Surface"] = "#F7FAFAFA", ["SurfaceSolid"] = "#FAFAFA",
            ["SurfaceAlt"] = "#09000000", ["Border"] = "#14000000", ["Text"] = "#141414", ["TextDim"] = "#707070",
            ["Hover"] = "#0C000000", ["Pressed"] = "#16000000", ["Selection"] = "#12000000", ["Dot"] = "#D0141414",
            ["PebbleFill"] = "#CDCDCD",
        },
        ["ink"] = new()
        {
            ["DockBg"] = "#EB000000", ["DockLine"] = "#2EFFFFFF", ["Surface"] = "#FA050505", ["SurfaceSolid"] = "#050505",
            ["SurfaceAlt"] = "#0FFFFFFF", ["Border"] = "#1CFFFFFF", ["Text"] = "#FFFFFF", ["TextDim"] = "#7B7B7B",
            ["Hover"] = "#14FFFFFF", ["Pressed"] = "#22FFFFFF", ["Selection"] = "#1CFFFFFF", ["Dot"] = "#FFFFFF",
            ["PebbleFill"] = "#363636",
        },
        ["ash"] = new()
        {
            ["DockBg"] = "#D8323232", ["DockLine"] = "#2AFFFFFF", ["Surface"] = "#F52C2C2C", ["SurfaceSolid"] = "#2C2C2C",
            ["SurfaceAlt"] = "#12FFFFFF", ["Border"] = "#1EFFFFFF", ["Text"] = "#F0F0F0", ["TextDim"] = "#9A9A9A",
            ["Hover"] = "#14FFFFFF", ["Pressed"] = "#22FFFFFF", ["Selection"] = "#1EFFFFFF", ["Dot"] = "#E6FFFFFF",
            ["PebbleFill"] = "#575757",
        },
        ["slate"] = new()
        {
            ["DockBg"] = "#D6202326", ["DockLine"] = "#26FFFFFF", ["Surface"] = "#F51B1E21", ["SurfaceSolid"] = "#1B1E21",
            ["SurfaceAlt"] = "#10FFFFFF", ["Border"] = "#1AFFFFFF", ["Text"] = "#EDF0F2", ["TextDim"] = "#899096",
            ["Hover"] = "#12FFFFFF", ["Pressed"] = "#20FFFFFF", ["Selection"] = "#1CFFFFFF", ["Dot"] = "#E6FFFFFF",
            ["PebbleFill"] = "#474C51",
        },
        ["fog"] = new()
        {
            ["DockBg"] = "#DCE6E6E6", ["DockLine"] = "#1C000000", ["Surface"] = "#F7EBEBEB", ["SurfaceSolid"] = "#EBEBEB",
            ["SurfaceAlt"] = "#0A000000", ["Border"] = "#16000000", ["Text"] = "#1A1A1A", ["TextDim"] = "#6B6B6B",
            ["Hover"] = "#0E000000", ["Pressed"] = "#18000000", ["Selection"] = "#14000000", ["Dot"] = "#D01A1A1A",
            ["PebbleFill"] = "#BDBDBD",
        },
        ["sand"] = new()
        {
            ["DockBg"] = "#DCF2EFE9", ["DockLine"] = "#1C000000", ["Surface"] = "#F7F5F2EC", ["SurfaceSolid"] = "#F5F2EC",
            ["SurfaceAlt"] = "#0A2A1A00", ["Border"] = "#15000000", ["Text"] = "#1D1B18", ["TextDim"] = "#78736B",
            ["Hover"] = "#0C000000", ["Pressed"] = "#16000000", ["Selection"] = "#12000000", ["Dot"] = "#D01D1B18",
            ["PebbleFill"] = "#CDC7BD",
        },
        ["high-contrast"] = new()
        {
            ["DockBg"] = "#FF000000", ["DockLine"] = "#FFFFFFFF", ["Surface"] = "#FF000000", ["SurfaceSolid"] = "#000000",
            ["SurfaceAlt"] = "#000000", ["Border"] = "#FFFFFF", ["Text"] = "#FFFFFF", ["TextDim"] = "#FFFFFF",
            ["Hover"] = "#40FFFFFF", ["Pressed"] = "#66FFFFFF", ["Selection"] = "#4DFFFFFF", ["Dot"] = "#FFFFFF",
            ["PebbleFill"] = "#FFFFFF",
        },
    };

    static readonly Dictionary<string, double> ShadowOpacity = new()
    {
        ["graphite"] = 0.5, ["paper"] = 0.16, ["ink"] = 0.65, ["high-contrast"] = 0, ["ash"] = 0.45, ["slate"] = 0.5, ["fog"] = 0.18, ["sand"] = 0.16,
    };

    // Ids from 0.1.0 (colour themes) map onto their nearest monochrome successor.
    static readonly Dictionary<string, string> Legacy = new()
    {
        ["polar-night"] = "graphite", ["aurora"] = "graphite", ["granite"] = "graphite", ["snow"] = "paper",
    };

    public static string Current { get; private set; } = "graphite";

    /// <summary>Light themes switch Windows itself to light mode too.</summary>
    public static bool IsLight(string id) => Normalize(id) is "paper" or "fog" or "sand";
    public static bool CurrentIsLight => IsLight(Current);

    public static string Normalize(string id) =>
        Palettes.ContainsKey(id) ? id : Legacy.GetValueOrDefault(id, "graphite");

    public static void Apply(string id)
    {
        id = Normalize(id);
        var res = Application.Current.Resources;
        foreach (var (key, hex) in Palettes[id])
        {
            var brush = new SolidColorBrush(Parse(hex));
            brush.Freeze();
            res[key] = brush;
        }
        res["ShadowColor"] = Colors.Black;
        res["ShadowOpacity"] = ShadowOpacity[id];
        Current = id;
        ApplyLook(res);
    }

    /// <summary>Accent colours to pick from. Empty keeps Norrsken's monochrome look (the accent is the text colour).</summary>
    public static readonly (string Id, string Name, string Hex)[] Accents =
    [
        ("", "None (monochrome)", ""), ("aurora", "Aurora", "#3DDC97"), ("glacier", "Glacier", "#4CA8FF"), ("violet", "Violet", "#9D7CFF"),
        ("rose", "Rose", "#FF6B9A"), ("ember", "Ember", "#FF8A3D"), ("gold", "Gold", "#F2C14E"), ("mint", "Mint", "#5EEAD4"), ("crimson", "Crimson", "#E5484D"),
    ];

    static readonly double[] Radii = [0, 1, 2, 2.5, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 21, 22, 23, 24];

    /// <summary>A corner radius in the user's shape (square … extra round), for code-built UI.</summary>
    public static CornerRadius R(double n) => new(n * Math.Clamp(App.Settings.CornerScale, 0, 2));

    public static Color? AccentColor()
    {
        string a = App.Settings.Accent ?? "";
        if (a.Length == 0) return null;
        string hex = Accents.FirstOrDefault(x => x.Id == a).Hex ?? a;
        if (hex is null or "") hex = a;
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch (Exception) { return null; }
    }

    /// <summary>The user's look on top of the theme: accent, corner shape, dock glass.</summary>
    public static void ApplyLook(ResourceDictionary? res = null)
    {
        res ??= Application.Current.Resources;
        var s = App.Settings;
        Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        var text = (res["Text"] as SolidColorBrush)?.Color ?? Colors.White;
        var solid = (res["SurfaceSolid"] as SolidColorBrush)?.Color ?? Colors.Black;
        if (AccentColor() is { } accent)
        {
            double luma = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
            res["Accent"] = Frozen(accent);
            res["AccentText"] = Frozen(luma > 0.62 ? Color.FromRgb(16, 16, 18) : Colors.White);
            res["Dot"] = Frozen(Color.FromArgb(0xE6, accent.R, accent.G, accent.B));
            res["Selection"] = Frozen(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
        }
        else
        {
            res["Accent"] = Frozen(text);
            res["AccentText"] = Frozen(solid);
        }
        double k = Math.Clamp(s.CornerScale, 0, 2);
        foreach (var r in Radii) res["R" + r.ToString(System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_')] = new CornerRadius(r * k);
        if (res["DockBg"] is SolidColorBrush dock && s.DockOpacity < 0.999)
        {
            var c = dock.Color;
            res["DockBg"] = Frozen(Color.FromArgb((byte)Math.Clamp(c.A * s.DockOpacity, 40, 255), c.R, c.G, c.B));
        }
    }

    /// <summary>Colours for the theme picker's preview tiles (dock fill, window surface, foreground).</summary>
    public static (Color Dock, Color Surface, Color Text) Preview(string id)
    {
        var p = Palettes[Normalize(id)];
        return (Parse(p["DockBg"]), Parse(p["SurfaceSolid"]), Parse(p["Text"]));
    }

    static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>
    /// Runs a look change (theme, accent, corners…) as a soft crossfade instead of a hard cut: every open window
    /// keeps a picture of how it looked, the change happens underneath, and the picture fades away.
    /// </summary>
    public static void Crossfade(Action change)
    {
        var shots = new List<(System.Windows.Documents.AdornerLayer Layer, Snapshot Adorner)>();
        if (!Motion.Reduced && Application.Current != null)
            foreach (Window w in Application.Current.Windows)
            {
                try
                {
                    if (!w.IsVisible || w.Content is not UIElement root || root.RenderSize.Width < 2 || root.RenderSize.Height < 2) continue;
                    if (System.Windows.Documents.AdornerLayer.GetAdornerLayer(root) is not { } layer) continue;
                    var dpi = VisualTreeHelper.GetDpi(root);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(root.RenderSize.Width * dpi.DpiScaleX), (int)(root.RenderSize.Height * dpi.DpiScaleY),
                        96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                    rtb.Render(root);
                    rtb.Freeze();
                    var a = new Snapshot(root, rtb) { IsHitTestVisible = false };
                    layer.Add(a);
                    shots.Add((layer, a));
                }
                catch (Exception) { }
            }
        change();
        foreach (var (layer, a) in shots)
            Motion.To(a, UIElement.OpacityProperty, 0, 280, Motion.InOut, () => layer.Remove(a));
    }

    sealed class Snapshot(UIElement owner, ImageSource image) : System.Windows.Documents.Adorner(owner)
    {
        protected override void OnRender(DrawingContext dc) => dc.DrawImage(image, new Rect(owner.RenderSize));
    }}
