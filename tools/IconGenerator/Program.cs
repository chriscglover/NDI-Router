using SkiaSharp;

// Draws the NDI Router app icon.
// Usage: dotnet run --project tools/IconGenerator -- <out-folder> [preview.png]
// Writes NdiRouter.ico (16-256 px) and NdiRouter-256.png; optionally a preview of every size, magnified.
string outDir = args[0];
Directory.CreateDirectory(outDir);
int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };

var pngs = sizes.Select(s => (Size: s, Png: Render(s))).ToList();
File.WriteAllBytes(Path.Combine(outDir, "NdiRouter-256.png"), pngs[^1].Png);
WriteIco(Path.Combine(outDir, "NdiRouter.ico"), pngs);
if (args.Length > 1)
    WritePreview(args[1], pngs);
Console.WriteLine("done");

// Design on a 16-unit grid, so 16/32/48/64/128/256 px land every edge on a whole pixel.
// A router crosspoint matrix: three inputs across, three outputs down, and in each output's
// column the one crosspoint that is made, lit red like the panel's on-air input.
static byte[] Render(int size)
{
    float u = size / 16f;
    bool smooth = size >= 48; // antialiased curves only where there are enough pixels
    // Every edge on a whole pixel (exact for multiples of 16; rounded for e.g. 24 px)
    float S(float v) => MathF.Round(v * u, MidpointRounding.AwayFromZero);
    SKRect R(float l, float t, float r, float b) => new(S(l), S(t), S(r), S(b));

    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);

    // Tile: light rim so it reads on dark and light backgrounds, dark gradient inside
    var rimColor = new SKColor(0x8A, 0x93, 0xA6);
    using var rim = new SKPaint { IsAntialias = smooth, Color = rimColor };
    using var fill = new SKPaint
    {
        IsAntialias = smooth,
        Shader = SKShader.CreateLinearGradient(new SKPoint(0, S(1)), new SKPoint(0, S(15)),
            new[] { new SKColor(0x34, 0x3B, 0x4A), new SKColor(0x14, 0x17, 0x1E) }, SKShaderTileMode.Clamp),
    };

    if (smooth)
    {
        c.DrawRoundRect(R(0, 0, 16, 16), 3 * u, 3 * u, rim);
        c.DrawRoundRect(R(1, 1, 15, 15), 2 * u, 2 * u, fill);
    }
    else
    {
        // Pixel-art corners: cut the outer corners, round the inner ones with rim pixels
        float px = MathF.Max(1, MathF.Round(u));
        var outer = R(0, 0, 16, 16);
        var inner = R(1, 1, 15, 15);
        c.DrawRect(outer, rim);
        using var clear = new SKPaint { BlendMode = SKBlendMode.Clear };
        foreach (var (x, y) in new[] { (outer.Left, outer.Top), (outer.Right - px, outer.Top), (outer.Left, outer.Bottom - px), (outer.Right - px, outer.Bottom - px) })
            c.DrawRect(SKRect.Create(x, y, px, px), clear);
        c.DrawRect(inner, fill);
        foreach (var (x, y) in new[] { (inner.Left, inner.Top), (inner.Right - px, inner.Top), (inner.Left, inner.Bottom - px), (inner.Right - px, inner.Bottom - px) })
            c.DrawRect(SKRect.Create(x, y, px, px), rim);
    }

    // Busses on 4-unit centres. Lines are a unit wide where pixels are scarce and finer where
    // there are plenty, so the matrix does not turn into a solid block at small sizes.
    float[] centres = { 4, 8, 12 };
    float half = size >= 64 ? 0.35f : 0.5f;
    using var inputs = new SKPaint { Color = new SKColor(0x5E, 0xA8, 0xE8) };  // blue: sources in
    using var outputs = new SKPaint { Color = new SKColor(0x9A, 0xA3, 0xB5) }; // grey: destinations out

    foreach (var x in centres) c.DrawRect(R(x - half, 2.5f, x + half, 13.5f), outputs);
    foreach (var y in centres) c.DrawRect(R(2.5f, y - half, 13.5f, y + half), inputs);

    // The made crosspoints: one per output column, each from a different input
    var lit = new SKColor(0xE5, 0x3B, 0x3B);
    using var dot = new SKPaint { IsAntialias = smooth, Color = lit };
    using var glow = new SKPaint { IsAntialias = true, Color = lit.WithAlpha(0x55) };
    using var shine = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0xB0, 0xA8) };
    (float X, float Y)[] made = { (4, 8), (8, 12), (12, 4) };
    foreach (var (x, y) in made)
    {
        if (smooth)
        {
            c.DrawCircle(S(x), S(y), 2.1f * u, glow);
            c.DrawCircle(S(x), S(y), 1.45f * u, dot);
            c.DrawCircle(S(x) - 0.4f * u, S(y) - 0.4f * u, 0.45f * u, shine);
        }
        else
        {
            c.DrawRect(R(x - 1.5f, y - 1.5f, x + 1.5f, y + 1.5f), dot);
        }
    }

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// ICO with a PNG image per size (supported by Windows Vista and later)
static void WriteIco(string path, List<(int Size, byte[] Png)> images)
{
    using var w = new BinaryWriter(File.Create(path));
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images)
        w.Write(png);
}

// Every size side by side, small ones magnified with hard pixels, on dark and light backgrounds
static void WritePreview(string path, List<(int Size, byte[] Png)> images)
{
    const int cell = 280;
    using var surface = SKSurface.Create(new SKImageInfo(cell * images.Count, cell * 2));
    var c = surface.Canvas;
    using var dark = new SKPaint { Color = new SKColor(0x20, 0x20, 0x20) };
    using var light = new SKPaint { Color = new SKColor(0xF0, 0xF0, 0xF0) };
    c.DrawRect(0, 0, cell * images.Count, cell, dark);
    c.DrawRect(0, cell, cell * images.Count, cell, light);
    using var paint = new SKPaint { FilterQuality = SKFilterQuality.None };
    for (int i = 0; i < images.Count; i++)
    {
        using var bmp = SKBitmap.Decode(images[i].Png);
        float scale = MathF.Floor(256f / images[i].Size);
        var dest = SKRect.Create(i * cell + (cell - bmp.Width * scale) / 2, (cell - bmp.Height * scale) / 2, bmp.Width * scale, bmp.Height * scale);
        c.DrawBitmap(bmp, dest, paint);
        dest.Offset(0, cell);
        c.DrawBitmap(bmp, dest, paint);
    }
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
}
