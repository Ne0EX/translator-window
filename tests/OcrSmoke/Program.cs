using System.IO;
using System.Drawing;
using System.Drawing.Text;
using System.Text;
using System.Text.Json;
using Translumo.Local;

Console.OutputEncoding = Encoding.UTF8;
SpatialOcr.SelfCheck();
var modelPath = Path.GetFullPath(args.Length > 0 ? args[0] : "models/tessdata");
var output = Path.GetFullPath("artifacts/ocr-smoke");
Directory.CreateDirectory(output);
using var ocr = new SpatialOcr(modelPath, Path.GetFullPath(".venv/Scripts/python.exe"),
    Path.GetFullPath("local-ocr/worker.py"), Path.GetFullPath("models/comic-text-detector/comictextdetector.onnx"));
if (args.Length > 1)
{
    using var page = new Bitmap(args[1]);
    for (var iteration = 0; iteration < 2; iteration++)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var regions = await ocr.RecognizeAsync(page, "ja-vert", CancellationToken.None);
        if (args.Contains("--black-jack-page12"))
        {
            var expected = new[] { "本日は君に当院の当直のアルバイトをお願いする", "それでは斉藤くん", "斉藤英二郎25歳",
                "エリートって奴だな", "永大卒か", "この病院の当直は初めてと", "永禄大学医学部卒", "誠同病院" };
            var actual = regions.Select(region => Normalize(region.Text)).ToArray();
            if (regions.Count != expected.Length || expected.Any(text => !actual.Contains(Normalize(text))))
                throw new InvalidOperationException("Real manga OCR did not recognize all seven bubbles and the sign exactly (ignoring punctuation).");
        }
        var result = new { milliseconds = timer.ElapsedMilliseconds, regions };
        Console.WriteLine(JsonSerializer.Serialize(result));
        File.WriteAllText(Path.ChangeExtension(args[1], ".detected.ocr.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
    return 0;
}
Console.WriteLine("Windows OCR: " + string.Join(", ", SpatialOcr.AvailableLanguages.Select(x => x.LanguageTag)));
var fixtures = new[]
{
    (Language: "en-US", Font: "Arial", First: "Hello world.", Second: "Read this text."),
    (Language: "ja", Font: "Yu Gothic", First: "こんにちは世界", Second: "今日はいい天気です"),
    (Language: "ko-KR", Font: "Malgun Gothic", First: "안녕하세요", Second: "오늘 날씨가 좋아요"),
    (Language: "th-TH", Font: "Leelawadee UI", First: "สวัสดีครับ", Second: "วันนี้อากาศดี")
};
var failures = new List<string>();
foreach (var fixture in fixtures)
{
    using var bitmap = new Bitmap(1100, 420);
    using (var graphics = Graphics.FromImage(bitmap))
    using (var font = new Font(fixture.Font, 32, GraphicsUnit.Pixel))
    {
        graphics.Clear(Color.FromArgb(230, 230, 230));
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.FillEllipse(Brushes.White, 20, 30, 500, 170);
        graphics.FillEllipse(Brushes.White, 580, 220, 500, 170);
        graphics.DrawEllipse(Pens.Black, 20, 30, 500, 170);
        graphics.DrawEllipse(Pens.Black, 580, 220, 500, 170);
        graphics.DrawString(fixture.First, font, Brushes.Black, 70, 88);
        graphics.DrawString(fixture.Second, font, Brushes.Black, 630, 278);
    }
    bitmap.Save(Path.Combine(output, fixture.Language + ".png"));
    var regions = await ocr.RecognizeAsync(bitmap, fixture.Language, CancellationToken.None);
    var comicRegions = await ocr.RecognizeAsync(bitmap, fixture.Language.Split('-')[0] + "-comic", CancellationToken.None);
    var comicJoined = Normalize(string.Join("", comicRegions.Select(x => x.Text)));
    if (!comicJoined.Contains(Normalize(fixture.First)) || !comicJoined.Contains(Normalize(fixture.Second)) || comicRegions.Count != 2)
        failures.Add(fixture.Language + " comic detection/OCR failed its two known text bubbles");
    Console.WriteLine(fixture.Language + ": " + JsonSerializer.Serialize(regions));
    var joined = Normalize(string.Join("", regions.Select(x => x.Text)));
    if (!joined.Contains(Normalize(fixture.First)) || !joined.Contains(Normalize(fixture.Second)))
        failures.Add(fixture.Language + " did not recognize both known source strings");
    if (regions.Count < 2 || regions.Any(region => region.Bounds.Width > 520 || region.Bounds.Height > 180))
        failures.Add(fixture.Language + " merged separate speech bubbles");
    if (regions.Any(region => !new Rectangle(0, 0, bitmap.Width, bitmap.Height).Contains(region.Bounds)))
        failures.Add(fixture.Language + " emitted out-of-image bounds");
}
using (var bitmap = new Bitmap(850, 500))
{
    using (var graphics = Graphics.FromImage(bitmap))
    using (var font = new Font("Yu Gothic", 32, GraphicsUnit.Pixel))
    {
        graphics.Clear(Color.FromArgb(230, 230, 230));
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.FillEllipse(Brushes.White, 30, 30, 330, 420);
        graphics.FillEllipse(Brushes.White, 480, 30, 330, 420);
        graphics.DrawEllipse(Pens.Black, 30, 30, 330, 420);
        graphics.DrawEllipse(Pens.Black, 480, 30, 330, 420);
        DrawVertical(graphics, font, "今日は", 210, 110);
        DrawVertical(graphics, font, "いい天気", 160, 110);
        DrawVertical(graphics, font, "こんにちは", 660, 100);
        DrawVertical(graphics, font, "世界", 610, 100);
    }
    bitmap.Save(Path.Combine(output, "ja-vert.png"));
    var regions = await ocr.RecognizeAsync(bitmap, "ja-vert", CancellationToken.None);
    Console.WriteLine("ja-vert: " + JsonSerializer.Serialize(regions));
    var joined = Normalize(string.Join("", regions.Select(x => x.Text)));
    if (!joined.Contains("今日はいい天気") || !joined.Contains("こんにちは世界"))
        failures.Add("vertical Japanese did not recognize both bubbles in reading order");
    if (regions.Count != 2 || regions.Any(region => region.Bounds.Width > 150))
        failures.Add("vertical Japanese did not preserve separate column groups");
}
using (var bitmap = new Bitmap(4000, 2800))
{
    using (var graphics = Graphics.FromImage(bitmap))
    using (var font = new Font("Arial", 96, GraphicsUnit.Pixel))
    {
        graphics.Clear(Color.White);
        graphics.DrawString("Large image text", font, Brushes.Black, 3200, 2400);
    }
    var regions = await ocr.RecognizeAsync(bitmap, "en-US", CancellationToken.None);
    Console.WriteLine("oversized: " + JsonSerializer.Serialize(regions));
    if (!Normalize(string.Join("", regions.Select(x => x.Text))).Contains("Largeimagetext")
        || regions.Any(region => region.Bounds.X < 3000 || region.Bounds.Y < 2300))
        failures.Add("Windows OCR scaling failed to restore physical source coordinates");
}
var virtualBounds = ScreenCapture.VirtualBounds;
using (var captured = ScreenCapture.Capture(new Rectangle(virtualBounds.Left, virtualBounds.Top, 64, 64)))
    if (captured.Width != 64 || captured.Height != 64) failures.Add("Native screen capture dimensions differ");
Console.WriteLine("Desktop capture: " + virtualBounds + "; visible external windows: " + ScreenCapture.ListWindows().Count);
File.WriteAllText(Path.Combine(output, "result.txt"), failures.Count == 0 ? "PASS: grouping, en/ja/ko/th/vertical OCR, scaling, capture" : string.Join(Environment.NewLine, failures));
foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
return failures.Count == 0 ? 0 : 1;

static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)));
static void DrawVertical(Graphics graphics, Font font, string text, int x, int y)
{
    foreach (var character in text)
    {
        graphics.DrawString(character.ToString(), font, Brushes.Black, x, y);
        y += 38;
    }
}
