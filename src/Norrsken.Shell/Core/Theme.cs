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
    }

    /// <summary>Colours for the theme picker's preview tiles (dock fill, window surface, foreground).</summary>
    public static (Color Dock, Color Surface, Color Text) Preview(string id)
    {
        var p = Palettes[Normalize(id)];
        return (Parse(p["DockBg"]), Parse(p["SurfaceSolid"]), Parse(p["Text"]));
    }

    static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
