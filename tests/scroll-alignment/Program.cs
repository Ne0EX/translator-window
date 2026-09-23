using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Translumo.Local;

const int width = 640, height = 480, offset = 120;
using var previous = new Bitmap(width, height, PixelFormat.Format32bppArgb);
var random = new Random(17);
for (int y = 0; y < height; y++)
    for (int x = 0; x < width; x++)
    {
        int value = random.Next(2) == 0 ? 20 : 235;
        previous.SetPixel(x, y, Color.FromArgb(value, value, value));
    }
var region = new TextRegion("unchanged dialogue", new Rectangle(220, 120, 100, 50));
using (var graphics = Graphics.FromImage(previous))
{
    graphics.FillRectangle(Brushes.White, region.Bounds);
    graphics.DrawRectangle(Pens.Black, region.Bounds);
    graphics.DrawLine(Pens.Black, 230, 130, 300, 130);
    graphics.DrawLine(Pens.Black, 230, 145, 290, 145);
}

using var scrolled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
using (var graphics = Graphics.FromImage(scrolled))
{
    graphics.Clear(Color.White);
    graphics.DrawImageUnscaled(previous, 0, offset);
}
if (!ScrollAlignment.TryEstimateVerticalShift(previous, scrolled, out int detected) || detected != offset
    || !ScrollAlignment.RegionsMatchAfterShift(previous, scrolled, new[] { region }, detected))
    throw new InvalidOperationException($"Unchanged dialogue scroll was not verified (shift {detected}).");
var edgeRegion = new TextRegion("leaving view", new Rectangle(220, 430, 100, 40));
if (!ScrollAlignment.TryMatchRegionsAfterShift(previous, scrolled, new[] { region, edgeRegion }, detected,
        out var retained, out var retainedIndices) || retained.Length != 1 || retainedIndices.Length != 1
    || retainedIndices[0] != 0 || retained[0].Bounds.Y != region.Bounds.Y + offset)
    throw new InvalidOperationException("Scrolling out an old region rejected a retained in-frame region.");

using var changedDialogue = (Bitmap)scrolled.Clone();
changedDialogue.SetPixel(region.Bounds.Left + 5, region.Bounds.Top + offset + 5, Color.Red);
if (ScrollAlignment.RegionsMatchAfterShift(previous, changedDialogue, new[] { region }, detected))
    throw new InvalidOperationException("Changed dialogue passed shifted-region verification.");

using var sparsePrevious = new Bitmap(600, 400, PixelFormat.Format32bppArgb);
using var sparseCurrent = new Bitmap(600, 400, PixelFormat.Format32bppArgb);
using (var graphics = Graphics.FromImage(sparsePrevious))
{
    graphics.Clear(Color.White);
    for (int i = 0; i < 7; i++) graphics.FillRectangle(Brushes.Black, 35 + i * 25, 20 + i * 55, 80 + i * 7, 6);
    graphics.FillRectangle(Brushes.Black, 230, 80, 100, 50);
    graphics.FillRectangle(Brushes.White, 235, 84, 90, 42);
    graphics.DrawString("dialogue", SystemFonts.DefaultFont, Brushes.Black, 240, 90);
}
using (var graphics = Graphics.FromImage(sparseCurrent))
{
    graphics.Clear(Color.White);
    graphics.DrawImageUnscaled(sparsePrevious, 0, offset);
}
var sparseRegion = new TextRegion("dialogue", new Rectangle(230, 80, 100, 50));
if (!ScrollAlignment.TryEstimateVerticalShift(sparsePrevious, sparseCurrent, out int sparseShift)
    || sparseShift != offset || !ScrollAlignment.RegionsMatchAfterShift(sparsePrevious, sparseCurrent, new[] { sparseRegion }, sparseShift))
    throw new InvalidOperationException($"Sparse page scroll was not verified (shift {sparseShift}).");

using var largePrevious = new Bitmap(3840, 2160, PixelFormat.Format32bppArgb);
using var largeCurrent = new Bitmap(3840, 2160, PixelFormat.Format32bppArgb);
const int largeOffset = 300;
var noise = new byte[largePrevious.Width * largePrevious.Height * 4];
new Random(29).NextBytes(noise);
for (int i = 3; i < noise.Length; i += 4) noise[i] = 255;
var largeData = largePrevious.LockBits(new Rectangle(0, 0, largePrevious.Width, largePrevious.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
try { Marshal.Copy(noise, 0, largeData.Scan0, noise.Length); }
finally { largePrevious.UnlockBits(largeData); }
var shiftedNoise = new byte[noise.Length];
int largeRow = largePrevious.Width * 4;
for (int y = 0; y < largePrevious.Height - largeOffset; y++)
    Array.Copy(noise, y * largeRow, shiftedNoise, (y + largeOffset) * largeRow, largeRow);
var shiftedData = largeCurrent.LockBits(new Rectangle(0, 0, largeCurrent.Width, largeCurrent.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
try { Marshal.Copy(shiftedNoise, 0, shiftedData.Scan0, shiftedNoise.Length); }
finally { largeCurrent.UnlockBits(shiftedData); }
var timer = Stopwatch.StartNew();
if (!ScrollAlignment.TryEstimateVerticalShift(largePrevious, largeCurrent, out int largeShift) || largeShift != largeOffset)
    throw new InvalidOperationException($"4K page scroll was not verified (shift {largeShift}).");
timer.Stop();
Console.WriteLine($"4K shift estimator: {timer.ElapsedMilliseconds} ms.");

Console.WriteLine("PASS: vertical shift aligns unchanged text and rejects changed dialogue pixels.");
