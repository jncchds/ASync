// Renders the app icon for the installers into packaging/icons: async-<size>.png (16…1024), async.ico (Windows) and
// async.icns (macOS). The same logo the app draws at run time (src/ASync.App.Desktop/TrayIcons.cs): a fuchsia rounded
// square with the white sync arrows. The files are committed; run this again only when the logo changes:
//   dotnet run scripts/render-icons.cs
#:property TargetFramework=net10.0
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false
#:package SkiaSharp@3.119.4
#:package SkiaSharp.NativeAssets.Linux@3.119.4

using System.Buffers.Binary;
using SkiaSharp;

const string Glyph = "M12 4a8 8 0 0 1 7.4 5h-2.2A6 6 0 0 0 7.3 8.7L9.6 11H3.5V4.9l2.4 2.4A8 8 0 0 1 12 4zM20.5 13v6.1l-2.4-2.4A8 8 0 0 1 4.6 15h2.2a6 6 0 0 0 9.9 1.3L14.4 13h6.1z";
var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? "scripts", ".."));
var outDir = Path.Combine(root, "packaging", "icons");
Directory.CreateDirectory(outDir);

byte[] Render(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    var s = size / 64f;
    using var fill = new SKPaint { Color = SKColor.Parse("#a21caf"), IsAntialias = true };
    c.DrawRoundRect(new SKRect(2 * s, 2 * s, 62 * s, 62 * s), 14 * s, 14 * s, fill);
    using var path = SKPath.ParseSvgPathData(Glyph);
    c.Translate(6.8f * s, 6.8f * s);
    c.Scale(2.1f * s, 2.1f * s);
    using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
    c.DrawPath(path, white);
    using var image = surface.Snapshot();
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    return png.ToArray();
}

var pngs = new Dictionary<int, byte[]>();
foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256, 512, 1024 })
{
    pngs[size] = Render(size);
    File.WriteAllBytes(Path.Combine(outDir, $"async-{size}.png"), pngs[size]);
}

// ICO: PNG-compressed entries (Vista and later), 16–256.
int[] icoSizes = [16, 24, 32, 48, 64, 128, 256];
using (var ico = File.Create(Path.Combine(outDir, "async.ico")))
using (var w = new BinaryWriter(ico))
{
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)icoSizes.Length);
    var offset = 6 + 16 * icoSizes.Length;
    foreach (var size in icoSizes)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
        w.Write(pngs[size].Length); w.Write(offset);
        offset += pngs[size].Length;
    }
    foreach (var size in icoSizes) w.Write(pngs[size]);
}

// ICNS: PNG entries by type code (ic07 128, ic08 256, ic09 512, ic10 1024 = 512@2x, ic11 32 = 16@2x, ic12 64 = 32@2x,
// ic13 256 = 128@2x, ic14 512 = 256@2x).
(string Type, int Size)[] icns = [("ic11", 32), ("ic12", 64), ("ic07", 128), ("ic13", 256), ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024)];
using (var file = File.Create(Path.Combine(outDir, "async.icns")))
{
    var total = 8 + icns.Sum(e => 8 + pngs[e.Size].Length);
    void Write4(string tag, int length)
    {
        var header = new byte[8];
        for (var i = 0; i < 4; i++) header[i] = (byte)tag[i];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), length);
        file.Write(header);
    }
    Write4("icns", total);
    foreach (var (type, size) in icns)
    {
        Write4(type, 8 + pngs[size].Length);
        file.Write(pngs[size]);
    }
}
Console.WriteLine($"Wrote {outDir}: async-*.png, async.ico, async.icns");
