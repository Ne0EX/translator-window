using System;
using System.Drawing;

namespace Translumo.Local;

public sealed record TextRegion(string Text, Rectangle Bounds);

public enum CaptureMode { SelectedArea, Window, Screen }

public sealed record WindowTarget(nint Handle, string Title)
{
    public override string ToString() => Title;
}

public sealed record OcrLanguage(string LanguageTag, string DisplayName)
{
    public override string ToString() => DisplayName;
}
