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
Bitmap? negativePrevious = null, negativeScrolled = null;
IntPtr negativePreviousMemory = IntPtr.Zero, negativeScrolledMemory = IntPtr.Zero;
try
{
    negativePrevious = NegativeStrideCopy(previous, out negativePreviousMemory);
    negativeScrolled = NegativeStrideCopy(scrolled, out negativeScrolledMemory);
    if (!ScrollAlignment.TryEstimateVerticalShift(negativePrevious, negativeScrolled, out int negativeShift)
        || negativeShift != offset)
        throw new InvalidOperationException($"Negative-stride scroll was not verified (shift {negativeShift}).");
}
finally
{
    negativePrevious?.Dispose();
    negativeScrolled?.Dispose();
    if (negativePreviousMemory != IntPtr.Zero) Marshal.FreeHGlobal(negativePreviousMemory);
    if (negativeScrolledMemory != IntPtr.Zero) Marshal.FreeHGlobal(negativeScrolledMemory);
}
var edgeRegion = new TextRegion("leaving view", new Rectangle(220, 430, 100, 40));
if (!ScrollAlignment.TryMatchRegionsAfterShift(previous, scrolled, new[] { region, edgeRegion }, detected,
        out var retained, out var retainedIndices) || retained.Length != 1 || retainedIndices.Length != 1
    || retainedIndices[0] != 0 || retained[0].Bounds.Y != region.Bounds.Y + offset)
    throw new InvalidOperationException("Scrolling out an old region rejected a retained in-frame region.");
var initialProofs = ScrollAlignment.FingerprintOcrInputs(previous, new[] { region, edgeRegion });
var shiftedProofs = ScrollAlignment.ShiftOcrProofs(previous, scrolled, new[] { region, edgeRegion }, initialProofs,
    retainedIndices, retained);
if (ScrollAlignment.ValidateOcrProofs(scrolled, retained, shiftedProofs).Length != 1)
    throw new InvalidOperationException("Unchanged padded OCR input did not retain its recognition proof.");

using var changedPadding = (Bitmap)scrolled.Clone();
changedPadding.SetPixel(region.Bounds.Right + 4, region.Bounds.Top + offset + 5, Color.Red);
if (!ScrollAlignment.RegionsMatchAfterShift(previous, changedPadding, new[] { region }, detected))
    throw new InvalidOperationException("A padding-only change should not invalidate the visible caption region.");
var paddingProof = ScrollAlignment.ShiftOcrProofs(previous, changedPadding, new[] { region },
    new[] { initialProofs[0] }, new[] { 0 }, new[] { retained[0] });
if (ScrollAlignment.ValidateOcrProofs(changedPadding, retained, paddingProof).Length != 0)
    throw new InvalidOperationException("A changed glyph in the recognizer padding retained stale OCR proof.");
using var changedAgain = (Bitmap)changedPadding.Clone();
var invalidProof = ScrollAlignment.ShiftOcrProofs(changedPadding, changedAgain, retained, paddingProof,
    new[] { 0 }, retained);
if (ScrollAlignment.ValidateOcrProofs(changedAgain, retained, invalidProof).Length != 0)
    throw new InvalidOperationException("Refreshing the frame manufactured a new OCR proof after padding changed.");
using var changedBeforeRequest = (Bitmap)scrolled.Clone();
changedBeforeRequest.SetPixel(region.Bounds.Right + 3, region.Bounds.Top + offset + 4, Color.Blue);
if (ScrollAlignment.ValidateOcrProofs(changedBeforeRequest, retained, shiftedProofs).Length != 0)
    throw new InvalidOperationException("OCR proof was not revalidated against the actual requested frame.");

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

new Random(43).NextBytes(shiftedNoise);
for (int i = 3; i < shiftedNoise.Length; i += 4) shiftedNoise[i] = 255;
shiftedData = largeCurrent.LockBits(new Rectangle(0, 0, largeCurrent.Width, largeCurrent.Height),
    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
try { Marshal.Copy(shiftedNoise, 0, shiftedData.Scan0, shiftedNoise.Length); }
finally { largeCurrent.UnlockBits(shiftedData); }
if (ScrollAlignment.TryEstimateVerticalShift(largePrevious, largeCurrent, out int pageFlipShift))
    throw new InvalidOperationException($"An unrelated 4K page was accepted as a scroll (shift {pageFlipShift}).");

Console.WriteLine("PASS: vertical shift aligns unchanged text, carries exact padded OCR proof, and rejects changed dialogue or padding pixels.");

static Bitmap NegativeStrideCopy(Bitmap source, out IntPtr memory)
{
    int stride = checked(source.Width * 4);
    memory = Marshal.AllocHGlobal(checked(stride * source.Height));
    Bitmap? result = null;
    try
    {
        result = new Bitmap(source.Width, source.Height, -stride, PixelFormat.Format32bppArgb,
            IntPtr.Add(memory, checked(stride * (source.Height - 1))));
        var sourceData = source.LockBits(new Rectangle(0, 0, source.Width, source.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData? destinationData = null;
        var row = new byte[stride];
        try
        {
            destinationData = result.LockBits(new Rectangle(0, 0, result.Width, result.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            if (destinationData.Stride >= 0)
                throw new InvalidOperationException("Negative-stride fixture did not retain a signed stride.");
            for (int y = 0; y < source.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(sourceData.Scan0, checked(y * sourceData.Stride)), row, 0, stride);
                Marshal.Copy(row, 0, IntPtr.Add(destinationData.Scan0, checked(y * destinationData.Stride)), stride);
            }
        }
        finally
        {
            if (destinationData is not null) result.UnlockBits(destinationData);
            source.UnlockBits(sourceData);
        }
        return result;
    }
    catch
    {
        result?.Dispose();
        Marshal.FreeHGlobal(memory);
        memory = IntPtr.Zero;
        throw;
    }
}
