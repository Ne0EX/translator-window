using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Translumo.Local;

public enum CaptionRole { Auto, Dialogue, Narration, Emphasis }
public enum CaptionWeight { Normal, SemiBold, Bold }
public enum CaptionEffect { None, Outline, Shadow }

public sealed record CaptionStyleOptions(
    CaptionRole Role = CaptionRole.Dialogue,
    string? Typeface = null,
    CaptionWeight? Weight = null,
    string? Foreground = null,
    CaptionEffect? Effect = null);

public sealed record CaptionStyleProfile(
    CaptionRole Role,
    string Typeface,
    FontWeight Weight,
    Color Foreground,
    CaptionEffect Effect,
    Color EffectColor);

public static class CaptionStyles
{
    public static CaptionStyleProfile Resolve(CaptionStyleOptions options, IEnumerable<string> installedFamilies)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(installedFamilies);
        var installed = installedFamilies.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var preset = options.Role switch
        {
            CaptionRole.Auto => new CaptionStyleProfile(options.Role, "Leelawadee UI", FontWeights.Normal,
                Colors.Black, CaptionEffect.Outline, Colors.White),
            CaptionRole.Narration => new CaptionStyleProfile(options.Role, "Leelawadee UI", FontWeights.SemiBold,
                Color.FromRgb(30, 30, 30), CaptionEffect.Outline, Colors.White),
            CaptionRole.Emphasis => new CaptionStyleProfile(options.Role, "Leelawadee UI", FontWeights.Bold,
                Colors.White, CaptionEffect.Shadow, Colors.Black),
            _ => new CaptionStyleProfile(CaptionRole.Dialogue, "Leelawadee UI", FontWeights.Normal,
                Colors.Black, CaptionEffect.Outline, Colors.White)
        };
        string requested = string.IsNullOrWhiteSpace(options.Typeface) ? preset.Typeface : options.Typeface.Trim();
        string typeface = installed.FirstOrDefault(name => name.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(name => name.Equals("Leelawadee UI", StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(name => name.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault() ?? preset.Typeface;
        var foreground = TryParseColor(options.Foreground, out var color) ? color : preset.Foreground;
        var effect = options.Effect ?? preset.Effect;
        return preset with
        {
            Typeface = typeface,
            Weight = options.Weight switch
            {
                CaptionWeight.Bold => FontWeights.Bold,
                CaptionWeight.SemiBold => FontWeights.SemiBold,
                CaptionWeight.Normal => FontWeights.Normal,
                _ => preset.Weight
            },
            Foreground = foreground,
            Effect = effect,
            EffectColor = Contrast(foreground)
        };
    }

    public static CaptionStyleProfile ResolveInstalled(CaptionStyleOptions options)
        => Resolve(options, Fonts.SystemFontFamilies.Select(font => font.Source));

    public static CaptionStyleProfile ForRegion(CaptionStyleProfile selected, TextRegion region)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(region);
        if (selected.Role != CaptionRole.Auto) return selected;

        var bounds = region.Bounds;
        var role = bounds.Width >= 240 && bounds.Width >= bounds.Height * 5
            ? CaptionRole.Emphasis : CaptionRole.Dialogue;
        var preset = Resolve(new CaptionStyleOptions(role, selected.Typeface), new[] { selected.Typeface });
        bool customWeight = selected.Weight != FontWeights.Normal;
        bool customForeground = selected.Foreground != Colors.Black;
        bool customEffect = selected.Effect != CaptionEffect.Outline;
        var resolved = preset with {
            Weight = customWeight ? selected.Weight : preset.Weight,
            Foreground = customForeground ? selected.Foreground : preset.Foreground,
            Effect = customEffect ? selected.Effect : preset.Effect
        };
        return resolved with { EffectColor = Contrast(resolved.Foreground) };
    }

    private static Color Contrast(Color foreground)
        => 0.299 * foreground.R + 0.587 * foreground.G + 0.114 * foreground.B < 145
            ? Colors.White : Colors.Black;

    private static bool TryParseColor(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string hex = value.Trim().TrimStart('#');
        if (hex.Length is not 6 and not 8 || !uint.TryParse(hex, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out uint packed)) return false;
        color = hex.Length == 6
            ? Color.FromRgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : Color.FromArgb((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        return color.A >= 128;
    }
}
