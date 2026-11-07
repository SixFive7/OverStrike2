using SkiaSharp;

namespace OverStrike2.Deploy;

/// <summary>An RGB picture as bytes, three a pixel, row by row.</summary>
sealed class Rgb(int width, int height, byte[]? pixels = null)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels ?? new byte[width * height * 3];

    public Rgb Crop(int left, int top, int right, int bottom)
    {
        var w = right - left;
        var h = bottom - top;
        var output = new Rgb(w, h);
        for (var y = 0; y < h; y++)
            Array.Copy(Pixels, ((top + y) * Width + left) * 3, output.Pixels, y * w * 3, w * 3);
        return output;
    }

    /// <summary>Another picture put on this one at (x, y), as Pillow's paste without a mask does.</summary>
    public void Paste(Rgb other, int x, int y)
    {
        for (var row = 0; row < other.Height; row++)
        {
            var ty = y + row;
            if (ty < 0 || ty >= Height)
                continue;
            for (var col = 0; col < other.Width; col++)
            {
                var tx = x + col;
                if (tx < 0 || tx >= Width)
                    continue;
                Array.Copy(other.Pixels, (row * other.Width + col) * 3, Pixels, (ty * Width + tx) * 3, 3);
            }
        }
    }
}

/// <summary>The few picture operations hud/thumbs.py did with Pillow, done the way Pillow does them, number for number, so a picture made here
/// is the picture Pillow made from the same pixels: its fixed-point resample (Lanczos), its luma, its Gaussian blur (three extended box
/// blurs), its blend and its paste through a mask. SkiaSharp only reads a preview's bytes and writes the JPEG.</summary>
static class Pictures
{
    // ------------------------------------------------------------------------------------------------------------ in and out

    /// <summary>A preview's bytes as RGB, as Pillow's open-then-convert made it: a picture with transparency laid on black first. Its own
    /// colour profile is not applied (Pillow does not either). null when the bytes are no picture SkiaSharp reads.</summary>
    public static Rgb? Decode(byte[] data)
    {
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return null;
        var rgba = bitmap.Bytes;
        var opaque = codec.Info.AlphaType == SKAlphaType.Opaque;
        var output = new Rgb(info.Width, info.Height);
        var o = output.Pixels;
        for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
        {
            if (opaque)
            {
                o[j] = rgba[i];
                o[j + 1] = rgba[i + 1];
                o[j + 2] = rgba[i + 2];
            }
            else
            {
                int a = rgba[i + 3];   // Pillow's paste on black through the alpha band: DIV255(in * mask)
                o[j] = Div255(rgba[i] * a);
                o[j + 1] = Div255(rgba[i + 1] * a);
                o[j + 2] = Div255(rgba[i + 2] * a);
            }
        }
        return output;
    }

    static byte Div255(int a)
    {
        var tmp = a + 128;
        return (byte)(((tmp >> 8) + tmp) >> 8);
    }

    /// <summary>The picture as a baseline JPEG of this quality (4:2:0, as Pillow writes one by default; full: no chroma subsampling, Pillow's
    /// subsampling=0).</summary>
    public static byte[] Jpeg(Rgb im, int quality, bool full = false)
    {
        using var bitmap = ToBitmap(im);
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SKJpegEncoderOptions(quality, full ? SKJpegEncoderDownsample.Downsample444 : SKJpegEncoderDownsample.Downsample420,
                                                                SKJpegEncoderAlphaOption.Ignore))
                         ?? throw new InvalidOperationException("the JPEG encoder gave nothing");
        return data.ToArray();
    }

    /// <summary>A picture file as RGB, as Pillow's open(...).convert("RGB") reads it: an alpha band dropped, not laid on anything. null when
    /// the bytes are no picture SkiaSharp reads.</summary>
    public static Rgb? DecodeRgb(byte[] data)
    {
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return null;
        return FromBitmap(bitmap);
    }

    /// <summary>A file read as RGB, or the end of the command with what is wrong with it.</summary>
    public static Rgb Open(string path) => DecodeRgb(File.ReadAllBytes(path)) ?? throw Out.Stop($"{path}: not a picture this can read");

    /// <summary>The picture in the format its file name says: .jpg or .jpeg a JPEG (Pillow's default quality 75), anything else a PNG.</summary>
    public static void Save(Rgb im, string path, int quality = 75)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        File.WriteAllBytes(path, ext is ".jpg" or ".jpeg" ? Jpeg(im, quality) : Png(im));
    }

    /// <summary>An SKBitmap's RGB, its alpha dropped.</summary>
    public static Rgb FromBitmap(SKBitmap bitmap)
    {
        var rgba = bitmap.Bytes;
        var output = new Rgb(bitmap.Width, bitmap.Height);
        var o = output.Pixels;
        for (int i = 0, j = 0; j < o.Length; i += 4, j += 3)
        {
            o[j] = rgba[i];
            o[j + 1] = rgba[i + 1];
            o[j + 2] = rgba[i + 2];
        }
        return output;
    }

    /// <summary>The picture as a PNG: lossless, every pixel as made.</summary>
    public static byte[] Png(Rgb im)
    {
        using var bitmap = ToBitmap(im);
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SKPngEncoderOptions()) ?? throw new InvalidOperationException("the PNG encoder gave nothing");
        return data.ToArray();
    }

    public static SKBitmap ToBitmap(Rgb im)
    {
        var bitmap = new SKBitmap(new SKImageInfo(im.Width, im.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var rgba = new byte[im.Width * im.Height * 4];
        for (int i = 0, j = 0; j < im.Pixels.Length; i += 4, j += 3)
        {
            rgba[i] = im.Pixels[j];
            rgba[i + 1] = im.Pixels[j + 1];
            rgba[i + 2] = im.Pixels[j + 2];
            rgba[i + 3] = 255;
        }
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        return bitmap;
    }

    // ------------------------------------------------------------------------------------------------- Pillow's resample

    const int PrecisionBits = 32 - 8 - 2;

    /// <summary>Pillow's resampling filters this tool uses: BOX, BICUBIC and LANCZOS, each with its support.</summary>
    public enum Filter { Box, Bicubic, Lanczos }

    static double Sinc(double x)
    {
        if (x == 0.0)
            return 1.0;
        x *= Math.PI;
        return Math.Sin(x) / x;
    }

    static double Lanczos(double x) => -3.0 <= x && x < 3.0 ? Sinc(x) * Sinc(x / 3) : 0.0;

    static double Bicubic(double x)
    {
        const double a = -0.5;
        if (x < 0.0)
            x = -x;
        if (x < 1.0)
            return ((a + 2.0) * x - (a + 3.0)) * x * x + 1;
        if (x < 2.0)
            return (((x - 5) * x + 8) * x - 4) * a;
        return 0.0;
    }

    static double Weight(Filter f, double x) => f switch
    {
        Filter.Box => x > -0.5 && x <= 0.5 ? 1.0 : 0.0,
        Filter.Bicubic => Bicubic(x),
        _ => Lanczos(x),
    };

    static double Support(Filter f) => f switch { Filter.Box => 0.5, Filter.Bicubic => 2.0, _ => 3.0 };

    /// <summary>Pillow's precompute_coeffs and normalize_coeffs_8bpc for one axis: per output pixel its first input, how many, and the
    /// weights in fixed point.</summary>
    static (int[] Bounds, int[] Kk, int KSize) Coefficients(int inSize, int outSize, Filter f)
    {
        double scale = (double)inSize / outSize, filterscale = Math.Max(1.0, scale);
        var support = Support(f) * filterscale;
        var ksize = (int)Math.Ceiling(support) * 2 + 1;
        var bounds = new int[outSize * 2];
        var kk = new int[outSize * ksize];
        var k = new double[ksize];
        for (var xx = 0; xx < outSize; xx++)
        {
            var center = (xx + 0.5) * scale;
            var ww = 0.0;
            var ss = 1.0 / filterscale;
            var xmin = (int)(center - support + 0.5);
            if (xmin < 0)
                xmin = 0;
            var xmax = (int)(center + support + 0.5);
            if (xmax > inSize)
                xmax = inSize;
            xmax -= xmin;
            for (var x = 0; x < xmax; x++)
            {
                var w = Weight(f, (x + xmin - center + 0.5) * ss);
                k[x] = w;
                ww += w;
            }
            for (var x = 0; x < ksize; x++)
            {
                var v = x < xmax ? (ww != 0.0 ? k[x] / ww : k[x]) : 0.0;
                kk[xx * ksize + x] = v < 0 ? (int)(-0.5 + v * (1 << PrecisionBits)) : (int)(0.5 + v * (1 << PrecisionBits));
            }
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }
        return (bounds, kk, ksize);
    }

    static byte Clip8(int v) => v >= (1 << PrecisionBits << 8) ? (byte)255 : v <= 0 ? (byte)0 : (byte)(v >> PrecisionBits);

    /// <summary>Pillow's resize with LANCZOS, each pass only when that size changes; the same size is a copy.</summary>
    public static Rgb Resize(Rgb im, int width, int height) => Resize(im, width, height, Filter.Lanczos);

    public static Rgb Resize(Rgb im, int width, int height, Filter f) =>
        new(width, height, Resample(im.Pixels, im.Width, im.Height, 3, width, height, f));

    /// <summary>Pillow's ImagingResample (13.0) on 8-bit bands, interleaved (3 for RGB, 1 for L): the horizontal pass, then the vertical - but
    /// the vertical first when the height shrinks by more than twice what the width does (Pillow's ImagingResampleInner, to make the horizontal
    /// pass faster). The order shows in the result: each pass rounds to whole values.</summary>
    public static byte[] Resample(byte[] pixels, int width, int height, int bands, int outWidth, int outHeight, Filter f)
    {
        if (outWidth == width && outHeight == height)
            return (byte[])pixels.Clone();
        var horizontalFirst = !(height - outHeight > 0 && height - outHeight > (width - outWidth) * 2);
        if (horizontalFirst)
        {
            var mid = outWidth != width ? Horizontal(pixels, width, height, bands, outWidth, f) : pixels;
            return outHeight != height ? Vertical(mid, outWidth, height, bands, outHeight, f) : mid;
        }
        var first = outHeight != height ? Vertical(pixels, width, height, bands, outHeight, f) : pixels;
        return outWidth != width ? Horizontal(first, width, outHeight, bands, outWidth, f) : first;
    }

    static byte[] Horizontal(byte[] cur, int width, int height, int bands, int outWidth, Filter f)
    {
        var (bounds, kk, ksize) = Coefficients(width, outWidth, f);
        var output = new byte[outWidth * height * bands];
        var sums = new int[bands];
        for (var y = 0; y < height; y++)
        {
            for (var xx = 0; xx < outWidth; xx++)
            {
                int xmin = bounds[xx * 2], xmax = bounds[xx * 2 + 1];
                Array.Fill(sums, 1 << (PrecisionBits - 1));
                for (var x = 0; x < xmax; x++)
                {
                    var p = (y * width + x + xmin) * bands;
                    var w = kk[xx * ksize + x];
                    for (var b = 0; b < bands; b++)
                        sums[b] += cur[p + b] * w;
                }
                var q = (y * outWidth + xx) * bands;
                for (var b = 0; b < bands; b++)
                    output[q + b] = Clip8(sums[b]);
            }
        }
        return output;
    }

    static byte[] Vertical(byte[] cur, int width, int height, int bands, int outHeight, Filter f)
    {
        var (bounds, kk, ksize) = Coefficients(height, outHeight, f);
        var output = new byte[width * outHeight * bands];
        var sums = new int[bands];
        for (var yy = 0; yy < outHeight; yy++)
        {
            int ymin = bounds[yy * 2], ymax = bounds[yy * 2 + 1];
            for (var x = 0; x < width; x++)
            {
                Array.Fill(sums, 1 << (PrecisionBits - 1));
                for (var y = 0; y < ymax; y++)
                {
                    var p = ((y + ymin) * width + x) * bands;
                    var w = kk[yy * ksize + y];
                    for (var b = 0; b < bands; b++)
                        sums[b] += cur[p + b] * w;
                }
                var q = (yy * width + x) * bands;
                for (var b = 0; b < bands; b++)
                    output[q + b] = Clip8(sums[b]);
            }
        }
        return output;
    }

    // ------------------------------------------------------------------------------------- Pillow's luma, blur and blend

    /// <summary>Pillow's convert("L"): (R x 19595 + G x 38470 + B x 7471 + 0x8000) >> 16, one byte a pixel.</summary>
    public static byte[] Luma(Rgb im)
    {
        var l = new byte[im.Width * im.Height];
        for (int i = 0, j = 0; i < l.Length; i++, j += 3)
            l[i] = (byte)((im.Pixels[j] * 19595 + im.Pixels[j + 1] * 38470 + im.Pixels[j + 2] * 7471 + 0x8000) >> 16);
        return l;
    }

    /// <summary>Pillow's _gaussian_blur_radius: the extended box blur's radius that three passes of it make a Gaussian of this radius.</summary>
    static float BoxRadius(float radius, int passes)
    {
        var sigma2 = radius * radius / passes;
        var bigL = MathF.Sqrt(12.0f * sigma2 + 1.0f);
        var l = MathF.Floor((bigL - 1.0f) / 2.0f);
        var a = (2 * l + 1) * (l * (l + 1) - 3 * sigma2);
        a /= 6 * (sigma2 - (l + 1) * (l + 1));
        return l + a;
    }

    /// <summary>Pillow's ImagingLineBoxBlur32 on one line of pixels (stride 3), the edges extended.</summary>
    static void LineBoxBlur(byte[] lineOut, byte[] lineIn, int lastx, int radius, int edgeA, int edgeB, uint ww, uint fw)
    {
        var acc = new uint[3];
        var bulk = new uint[3];
        uint P(int x, int c) => lineIn[x * 3 + c];
        void Move(int subtract, int add)
        {
            for (var c = 0; c < 3; c++)
                acc[c] += P(add, c) - P(subtract, c);
        }
        void Far(int left, int right)
        {
            for (var c = 0; c < 3; c++)
                bulk[c] = acc[c] * ww + (P(left, c) + P(right, c)) * fw;
        }
        void Save(int x)
        {
            for (var c = 0; c < 3; c++)
                lineOut[x * 3 + c] = (byte)((bulk[c] + (1u << 23)) >> 24);
        }
        for (var c = 0; c < 3; c++)
            acc[c] = P(0, c) * (uint)(radius + 1);
        for (var x = 0; x < edgeA - 1; x++)
            for (var c = 0; c < 3; c++)
                acc[c] += P(x, c);
        for (var c = 0; c < 3; c++)
            acc[c] += P(lastx, c) * (uint)(radius - edgeA + 1);
        if (edgeA <= edgeB)
        {
            for (var x = 0; x < edgeA; x++)
            {
                Move(0, x + radius);
                Far(0, x + radius + 1);
                Save(x);
            }
            for (var x = edgeA; x < edgeB; x++)
            {
                Move(x - radius - 1, x + radius);
                Far(x - radius - 1, x + radius + 1);
                Save(x);
            }
            for (var x = edgeB; x <= lastx; x++)
            {
                Move(x - radius - 1, lastx);
                Far(x - radius - 1, lastx);
                Save(x);
            }
        }
        else
        {
            for (var x = 0; x < edgeB; x++)
            {
                Move(0, x + radius);
                Far(0, x + radius + 1);
                Save(x);
            }
            for (var x = edgeB; x < edgeA; x++)
            {
                Move(0, lastx);
                Far(0, lastx);
                Save(x);
            }
            for (var x = edgeA; x <= lastx; x++)
            {
                Move(x - radius - 1, lastx);
                Far(x - radius - 1, lastx);
                Save(x);
            }
        }
    }

    /// <summary>One horizontal pass of Pillow's box blur over every row of a picture, in place.</summary>
    static void HorizontalBoxBlur(Rgb im, float floatRadius)
    {
        var radius = (int)floatRadius;
        var ww = (uint)((1 << 24) / (floatRadius * 2 + 1));
        var fw = (uint)(((1 << 24) - (radius * 2 + 1) * ww) / 2);
        var edgeA = Math.Min(radius + 1, im.Width);
        var edgeB = Math.Max(im.Width - radius - 1, 0);
        var lineIn = new byte[im.Width * 3];
        var lineOut = new byte[im.Width * 3];
        for (var y = 0; y < im.Height; y++)
        {
            Array.Copy(im.Pixels, y * im.Width * 3, lineIn, 0, lineIn.Length);
            LineBoxBlur(lineOut, lineIn, im.Width - 1, radius, edgeA, edgeB, ww, fw);
            Array.Copy(lineOut, 0, im.Pixels, y * im.Width * 3, lineOut.Length);
        }
    }

    static Rgb Transpose(Rgb im)
    {
        var output = new Rgb(im.Height, im.Width);
        for (var y = 0; y < im.Height; y++)
            for (var x = 0; x < im.Width; x++)
                Array.Copy(im.Pixels, (y * im.Width + x) * 3, output.Pixels, (x * im.Height + y) * 3, 3);
        return output;
    }

    /// <summary>Pillow's GaussianBlur(radius): three extended box blurs along the rows, then three along the columns.</summary>
    public static Rgb GaussianBlur(Rgb im, float radius)
    {
        const int passes = 3;
        var r = BoxRadius(radius, passes);
        var output = new Rgb(im.Width, im.Height, (byte[])im.Pixels.Clone());
        if (r != 0)
            for (var i = 0; i < passes; i++)
                HorizontalBoxBlur(output, r);
        if (r == 0)
            return output;
        var t = Transpose(output);
        for (var i = 0; i < passes; i++)
            HorizontalBoxBlur(t, r);
        return Transpose(t);
    }

    /// <summary>Pillow's ImageEnhance.Brightness(...).enhance(factor) for a factor from 0 to 1: a blend with black, each value
    /// truncated as Pillow's ImagingBlend truncates it (in single precision).</summary>
    public static Rgb Brightness(Rgb im, float factor)
    {
        var output = new Rgb(im.Width, im.Height);
        for (var i = 0; i < im.Pixels.Length; i++)
            output.Pixels[i] = (byte)(int)(factor * im.Pixels[i]);
        return output;
    }
}
