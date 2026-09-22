using System;
using System.Drawing;
using System.Windows.Media;
using Rect = System.Windows.Rect;

namespace Translumo.Local;

internal static class OcrGeometry
{
    internal static Rectangle ToSourceBounds(Rect bounds, Size recognizedImage, Size sourceImage, double angle)
    {
        // Windows OCR reports boxes in its deskewed image; restore original image coordinates first.
        // https://github.com/microsoft/Windows-universal-samples/blob/main/Samples/OCR/cs/OcrFileImage.xaml.cs
        var transform = Matrix.Identity;
        transform.RotateAt(angle, recognizedImage.Width / 2d, recognizedImage.Height / 2d);
        transform.Scale(sourceImage.Width / (double)recognizedImage.Width, sourceImage.Height / (double)recognizedImage.Height);
        bounds.Transform(transform);
        return Rectangle.Intersect(new Rectangle(Point.Empty, sourceImage), Rectangle.FromLTRB(
            (int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
            (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom)));
    }

    internal static void SelfCheck()
    {
        var scaled = ToSourceBounds(new Rect(10, 20, 30, 40), new Size(100, 100), new Size(200, 200), 0);
        if (scaled != new Rectangle(20, 40, 60, 80))
            throw new InvalidOperationException("OCR scaling changed the source bounds.");
        var rotated = ToSourceBounds(new Rect(140, 90, 40, 20), new Size(200, 200), new Size(200, 200), 90);
        if (!rotated.Contains(new Rectangle(90, 140, 20, 40)) || rotated.Width > 22 || rotated.Height > 42)
            throw new InvalidOperationException("OCR deskew boxes must rotate around the image center, clockwise.");
        var negative = ToSourceBounds(new Rect(90, 140, 20, 40), new Size(200, 200), new Size(400, 400), -90);
        if (!negative.Contains(new Rectangle(280, 180, 80, 40)) || negative.Width > 82 || negative.Height > 42)
            throw new InvalidOperationException("OCR deskew restoration must precede scaling and support negative angles.");
    }
}
