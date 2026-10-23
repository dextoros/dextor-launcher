// Renders the DexTor OS mark (docs/icon.svg) into every bitmap the launcher and its
// MSIX package need:
//   docs/icon.png         1024 x 1024 master
//   docs/favicon.png      64 x 64, embedded in the launcher (window and tray icon)
//   docs/favicon.ico      16..256, the launcher's ApplicationIcon
//   <assets>/*.png        MSIX visual assets with scale and target-size qualifiers
// Usage: StoreAssets <icon.svg> <docs dir> <assets dir>
using SkiaSharp;
using Svg.Skia;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: StoreAssets <icon.svg> <docs dir> <assets dir>");
    return 2;
}

var svgPath = Path.GetFullPath(args[0]);
var docsDir = Path.GetFullPath(args[1]);
var assetsDir = Path.GetFullPath(args[2]);
Directory.CreateDirectory(assetsDir);

using var svg = new SKSvg();
var picture = svg.Load(svgPath) ?? throw new InvalidOperationException($"Could not read {svgPath}");
var bounds = picture.CullRect;
// The viewBox of docs/icon.svg; keep the two in step.
var viewBox = SKRect.Create(81.19f, 122.28f, 232f, 232f);

// The inner edge of the shield's ring (the first sub-path of the shield in icon.svg) and
// the ring's gradient. At 16 px the ring is under one pixel wide and blurs into the
// background; small sizes thicken it inwards by stroking this edge in the ring's colour.
var shieldInnerEdge = SKPath.ParseSvgPathData(
    "M197.19,210.37c5.66,3.25,13,6.98,22.37,10.93c4.28,1.81,8.79,3.58,13.52,5.3c0.66,0.24,1.11,0.87,1.11,1.58" +
    "v42.2c0,3.09-0.5,6.08-1.47,8.82c-3.16,8.86-11.68,24.44-34.03,35.51c-0.93,0.46-2.06,0.46-2.99,0" +
    "c-22.35-11.07-30.86-26.64-34-35.44c-0.99-2.82-1.5-5.81-1.5-8.89v-42.2c0-0.7,0.45-1.33,1.11-1.58c4.45-1.63,8.76-3.31,12.89-5.03" +
    "C183.86,217.52,191.41,213.7,197.19,210.37z");
using var shieldShader = SKShader.CreateLinearGradient(
    new SKPoint(250.8302f, 180.6572f),
    new SKPoint(137.593f, 328.6504f),
    [SKColor.Parse("#632D87"), SKColor.Parse("#612C85"), SKColor.Parse("#5A287C"), SKColor.Parse("#4D226F"), SKColor.Parse("#3C1A5B"), SKColor.Parse("#30144E")],
    [0f, 0.3834f, 0.599f, 0.773f, 0.9239f, 1f],
    SKShaderTileMode.Clamp);

// How many device pixels the ring gains, by the mark's size in pixels.
static double RingBoost(double side) => side switch
{
    <= 16 => 0.7,
    <= 20 => 0.6,
    <= 24 => 0.5,
    <= 32 => 0.4,
    <= 40 => 0.3,
    <= 48 => 0.2,
    _ => 0
};

// The mark fills its square edge to edge but for the margin drawn into the SVG itself.
// `fill` is the share of the target the mark's square takes; the rest is transparent.
SKBitmap Render(int width, int height, double fill)
{
    var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    var side = Math.Min(width, height) * fill;
    // Whole pixels for the mark's square, so small sizes do not straddle pixel borders.
    side = Math.Round(side);
    var left = Math.Round((width - side) / 2);
    var top = Math.Round((height - side) / 2);
    canvas.Translate((float)left, (float)top);
    canvas.Scale((float)(side / bounds.Width), (float)(side / bounds.Height));
    canvas.Translate(-bounds.Left, -bounds.Top);
    canvas.DrawPicture(picture);
    var boost = RingBoost(side);
    if (boost > 0)
    {
        // A stroke centred on the inner edge: its outer half lies on the ring already,
        // its inner half widens the ring by `boost` pixels.
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeWidth = (float)(2 * boost * viewBox.Width / side),
            Shader = shieldShader
        };
        // The picture is in the SVG's width/height units; the path in its viewBox units.
        canvas.Translate(bounds.Left, bounds.Top);
        canvas.Scale(bounds.Width / viewBox.Width, bounds.Height / viewBox.Height);
        canvas.Translate(-viewBox.Left, -viewBox.Top);
        canvas.DrawPath(shieldInnerEdge, paint);
    }
    canvas.Flush();
    return bitmap;
}

static byte[] EncodePng(SKBitmap bitmap)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

void SavePng(SKBitmap bitmap, string path)
{
    File.WriteAllBytes(path, EncodePng(bitmap));
    Console.WriteLine($"  {Path.GetRelativePath(Environment.CurrentDirectory, path)} ({bitmap.Width}x{bitmap.Height})");
}

void RenderPng(string path, int width, int height, double fill)
{
    using var bitmap = Render(width, height, fill);
    SavePng(bitmap, path);
}

// --- Master, favicon.png and favicon.ico -------------------------------------------------
RenderPng(Path.Combine(docsDir, "icon.png"), 1024, 1024, 1.0);
RenderPng(Path.Combine(docsDir, "favicon.png"), 64, 64, 1.0);

int[] iconSizes = [16, 20, 24, 32, 40, 48, 64, 256];
var frames = iconSizes.Select(size =>
{
    using var bitmap = Render(size, size, 1.0);
    // 256 is stored as PNG (Vista and later read it, and it keeps the file small); the
    // smaller frames as classic 32-bit DIBs, which every reader - the resource compiler,
    // Explorer, old shell code - understands.
    return (Size: size, Data: size >= 256 ? EncodePng(bitmap) : EncodeDib(bitmap));
}).ToArray();
WriteIco(Path.Combine(docsDir, "favicon.ico"), frames);
Console.WriteLine($"  favicon.ico ({string.Join(", ", iconSizes)})");

// --- MSIX visual assets ------------------------------------------------------------------
int[] scales = [100, 125, 150, 200, 400];
int Scaled(int baseSize, int scale) => (int)Math.Round(baseSize * scale / 100.0, MidpointRounding.AwayFromZero);

// App list, taskbar, Start: the mark nearly fills the plate-less icon.
foreach (var scale in scales)
{
    var size = Scaled(44, scale);
    RenderPng(Path.Combine(assetsDir, $"Square44x44Logo.scale-{scale}.png"), size, size, 0.92);
}
foreach (var size in new[] { 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256 })
{
    RenderPng(Path.Combine(assetsDir, $"Square44x44Logo.targetsize-{size}.png"), size, size, 1.0);
    RenderPng(Path.Combine(assetsDir, $"Square44x44Logo.targetsize-{size}_altform-unplated.png"), size, size, 1.0);
    RenderPng(Path.Combine(assetsDir, $"Square44x44Logo.targetsize-{size}_altform-lightunplated.png"), size, size, 1.0);
}

// Tiles: the mark in the middle third, as Windows' own tiles are drawn.
foreach (var scale in scales)
{
    RenderPng(Path.Combine(assetsDir, $"Square150x150Logo.scale-{scale}.png"), Scaled(150, scale), Scaled(150, scale), 0.6);
    RenderPng(Path.Combine(assetsDir, $"Wide310x150Logo.scale-{scale}.png"), Scaled(310, scale), Scaled(150, scale), 0.6);
    RenderPng(Path.Combine(assetsDir, $"SmallTile.scale-{scale}.png"), Scaled(71, scale), Scaled(71, scale), 0.7);
    RenderPng(Path.Combine(assetsDir, $"LargeTile.scale-{scale}.png"), Scaled(310, scale), Scaled(310, scale), 0.55);
    RenderPng(Path.Combine(assetsDir, $"StoreLogo.scale-{scale}.png"), Scaled(50, scale), Scaled(50, scale), 0.92);
}

return 0;

static byte[] EncodeDib(SKBitmap bitmap)
{
    var width = bitmap.Width;
    var height = bitmap.Height;
    var maskStride = ((width + 31) / 32) * 4;
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    // BITMAPINFOHEADER; the height counts the colour rows and the AND mask together.
    writer.Write(40);
    writer.Write(width);
    writer.Write(height * 2);
    writer.Write((short)1);
    writer.Write((short)32);
    writer.Write(0);
    writer.Write(width * height * 4 + maskStride * height);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    // Bottom-up, straight (not premultiplied) BGRA.
    for (var y = height - 1; y >= 0; y--)
    {
        for (var x = 0; x < width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            writer.Write(color.Blue);
            writer.Write(color.Green);
            writer.Write(color.Red);
            writer.Write(color.Alpha);
        }
    }
    // AND mask: a pixel is transparent where its alpha is zero.
    for (var y = height - 1; y >= 0; y--)
    {
        var row = new byte[maskStride];
        for (var x = 0; x < width; x++)
        {
            if (bitmap.GetPixel(x, y).Alpha == 0)
            {
                row[x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }
        writer.Write(row);
    }
    writer.Flush();
    return stream.ToArray();
}

static void WriteIco(string path, (int Size, byte[] Data)[] frames)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((short)0);
    writer.Write((short)1);
    writer.Write((short)frames.Length);
    var offset = 6 + 16 * frames.Length;
    foreach (var (size, data) in frames)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(data.Length);
        writer.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in frames)
    {
        writer.Write(data);
    }
}
