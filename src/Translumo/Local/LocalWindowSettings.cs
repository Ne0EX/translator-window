namespace Translumo.Local;

internal sealed record LocalWindowSettings(string Source, string Target, string Python, string Model, bool Overwrite,
    int Padding, bool Comics = true, string CaptionRole = "Dialogue", string CaptionTypeface = "Leelawadee UI",
    string CaptionWeight = "Normal", string CaptionForeground = "#FF000000", string CaptionEffect = "Outline");
