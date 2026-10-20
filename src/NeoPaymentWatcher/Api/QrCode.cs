using System.Text;

namespace NeoPaymentWatcher.Api;

/// <summary>
/// A QR code encoder (ISO/IEC 18004): byte mode, error correction level M, versions 1-40. It exists so
/// the checkout page needs no graphics library. The algorithm follows Project Nayuki's reference
/// implementation (qrcodegen, MIT), with the error-correction tables of the standard.
/// </summary>
internal sealed class QrCode
{
    // Level M, by version (index 0 unused): error-correction codewords per block, and number of blocks.
    static readonly byte[] EccPerBlock =
        [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28];
    static readonly byte[] EccBlocks =
        [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49];

    const int PenaltyN1 = 3, PenaltyN2 = 3, PenaltyN3 = 40, PenaltyN4 = 10;

    readonly bool[,] modules;
    readonly bool[,] isFunction;

    public int Version { get; }

    public int Size { get; }

    public int Mask { get; }

    /// <summary>True when the module at column x, row y is dark. Outside the symbol everything is light.</summary>
    public bool this[int x, int y] => x >= 0 && y >= 0 && x < Size && y < Size && modules[y, x];

    public static QrCode Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    public static QrCode Encode(byte[] data)
    {
        var version = 1;
        while (version <= 40 && DataCodewords(version) * 8 < 4 + CountBits(version) + 8 * data.Length)
            version++;
        if (version > 40)
            throw new ArgumentException("Too much data for a QR code.", nameof(data));
        return new QrCode(version, data);
    }

    QrCode(int version, byte[] data)
    {
        Version = version;
        Size = version * 4 + 17;
        modules = new bool[Size, Size];
        isFunction = new bool[Size, Size];
        DrawFunctionPatterns();
        DrawCodewords(AddErrorCorrection(EncodeBytes(data)));
        Mask = ChooseMask();
    }

    /// <summary>The symbol as an SVG with a quiet zone, one path for all dark modules.</summary>
    public string ToSvg(int quietZone = 4)
    {
        var total = Size + 2 * quietZone;
        var svg = new StringBuilder();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {total} {total}\" shape-rendering=\"crispEdges\" role=\"img\" aria-label=\"QR code\">");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/><path fill=\"#000\" d=\"");
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (!modules[y, x])
                    continue;
                var run = 1;
                while (x + run < Size && modules[y, x + run])
                    run++;
                svg.Append($"M{x + quietZone} {y + quietZone}h{run}v1h-{run}z");
                x += run - 1;
            }
        }
        return svg.Append("\"/></svg>").ToString();
    }

    // ── Data encoding ──────────────────────────────────────────────────────────────

    static int CountBits(int version) => version <= 9 ? 8 : 16;

    /// <summary>Modules available for data and error correction, i.e. everything outside the function patterns.</summary>
    static int RawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var numAlign = version / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (version >= 7)
                result -= 36;
        }
        return result;
    }

    static int DataCodewords(int version) => RawDataModules(version) / 8 - EccPerBlock[version] * EccBlocks[version];

    byte[] EncodeBytes(byte[] data)
    {
        var capacity = DataCodewords(Version) * 8;
        var bits = new List<bool>(capacity);
        Append(bits, 0b0100, 4);                       // byte mode
        Append(bits, data.Length, CountBits(Version));
        foreach (var b in data)
            Append(bits, b, 8);
        Append(bits, 0, Math.Min(4, capacity - bits.Count));   // terminator
        Append(bits, 0, (8 - bits.Count % 8) % 8);             // to a byte boundary
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11)
            Append(bits, pad, 8);

        var result = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
                result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        }
        return result;
    }

    static void Append(List<bool> bits, int value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
            bits.Add(((value >> i) & 1) != 0);
    }

    /// <summary>Splits the data into blocks, appends each block's Reed-Solomon codewords and interleaves them.</summary>
    byte[] AddErrorCorrection(byte[] data)
    {
        int numBlocks = EccBlocks[Version], blockEccLen = EccPerBlock[Version];
        var rawCodewords = RawDataModules(Version) / 8;
        var numShortBlocks = numBlocks - rawCodewords % numBlocks;
        var shortBlockLen = rawCodewords / numBlocks;

        var blocks = new List<byte[]>(numBlocks);
        var divisor = ReedSolomonDivisor(blockEccLen);
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            var dat = data[k..(k + shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1))];
            k += dat.Length;
            var block = new byte[shortBlockLen + 1];
            dat.CopyTo(block, 0);
            ReedSolomonRemainder(dat, divisor).CopyTo(block, block.Length - blockEccLen);
            blocks.Add(block);
        }

        var result = new byte[rawCodewords];
        var index = 0;
        for (var i = 0; i < blocks[0].Length; i++)
        {
            for (var j = 0; j < blocks.Count; j++)
            {
                // Skip the padding byte that short blocks have in place of a last data byte.
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                    result[index++] = blocks[j][i];
            }
        }
        return result;
    }

    static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[^1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], (byte)root);
                if (j + 1 < degree)
                    result[j] ^= result[j + 1];
            }
            root = Multiply((byte)root, 0x02);
        }
        return result;
    }

    static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++)
                result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    /// <summary>Multiplication in GF(2^8) with the QR code's primitive polynomial x^8 + x^4 + x^3 + x^2 + 1.</summary>
    static byte Multiply(byte x, byte y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    // ── Module placement ───────────────────────────────────────────────────────────

    void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var align = AlignmentPositions();
        var n = align.Length;
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                if (!((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)))
                    DrawAlignment(align[i], align[j]);
            }
        }
        DrawFormatBits(0);   // reserves the modules; the real bits come with the mask
        DrawVersion();
    }

    void DrawFinder(int x, int y)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size)
                    SetFunction(xx, yy, dist != 2 && dist != 4);
            }
        }
    }

    void DrawAlignment(int x, int y)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }
    }

    int[] AlignmentPositions()
    {
        if (Version == 1)
            return [];
        var numAlign = Version / 7 + 2;
        var step = Version == 32 ? 26 : (Version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = Size - 7; i >= 1; i--, pos -= step)
            result[i] = pos;
        return result;
    }

    void DrawFormatBits(int mask)
    {
        var data = mask;   // level M has format bits 00, so the 5 data bits are the mask alone
        var rem = data;
        for (var i = 0; i < 10; i++)
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = (data << 10 | rem) ^ 0x5412;

        for (var i = 0; i <= 5; i++)
            SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (var i = 9; i < 15; i++)
            SetFunction(14 - i, 8, Bit(bits, i));

        for (var i = 0; i < 8; i++)
            SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (var i = 8; i < 15; i++)
            SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true);
    }

    void DrawVersion()
    {
        if (Version < 7)
            return;
        var rem = Version;
        for (var i = 0; i < 12; i++)
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        var bits = Version << 12 | rem;
        for (var i = 0; i < 18; i++)
        {
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, Bit(bits, i));
            SetFunction(b, a, Bit(bits, i));
        }
    }

    /// <summary>Places the codewords in the zigzag order of the standard, skipping function modules.</summary>
    void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
                right = 5;
            for (var vert = 0; vert < Size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vert : vert;
                    if (!isFunction[y, x] && i < data.Length * 8)
                    {
                        modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
            }
        }
    }

    int ChooseMask()
    {
        var best = 0;
        var min = long.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            var penalty = Penalty();
            if (penalty < min)
            {
                min = penalty;
                best = mask;
            }
            ApplyMask(mask);   // undo
        }
        ApplyMask(best);
        DrawFormatBits(best);
        return best;
    }

    void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert && !isFunction[y, x])
                    modules[y, x] = !modules[y, x];
            }
        }
    }

    /// <summary>The standard's four penalty rules; the mask with the lowest score is the most readable.</summary>
    long Penalty()
    {
        long result = 0;
        var history = new int[7];

        for (var y = 0; y < Size; y++)
        {
            var runColor = false;
            var runX = 0;
            Array.Clear(history);
            for (var x = 0; x < Size; x++)
            {
                if (modules[y, x] == runColor)
                {
                    runX++;
                    if (runX == 5)
                        result += PenaltyN1;
                    else if (runX > 5)
                        result++;
                }
                else
                {
                    AddRun(runX, history);
                    if (!runColor)
                        result += CountFinderLikePatterns(history) * PenaltyN3;
                    runColor = modules[y, x];
                    runX = 1;
                }
            }
            result += TerminateRuns(runColor, runX, history) * PenaltyN3;
        }

        for (var x = 0; x < Size; x++)
        {
            var runColor = false;
            var runY = 0;
            Array.Clear(history);
            for (var y = 0; y < Size; y++)
            {
                if (modules[y, x] == runColor)
                {
                    runY++;
                    if (runY == 5)
                        result += PenaltyN1;
                    else if (runY > 5)
                        result++;
                }
                else
                {
                    AddRun(runY, history);
                    if (!runColor)
                        result += CountFinderLikePatterns(history) * PenaltyN3;
                    runColor = modules[y, x];
                    runY = 1;
                }
            }
            result += TerminateRuns(runColor, runY, history) * PenaltyN3;
        }

        for (var y = 0; y < Size - 1; y++)
        {
            for (var x = 0; x < Size - 1; x++)
            {
                var color = modules[y, x];
                if (color == modules[y, x + 1] && color == modules[y + 1, x] && color == modules[y + 1, x + 1])
                    result += PenaltyN2;
            }
        }

        var dark = 0;
        foreach (var module in modules)
        {
            if (module)
                dark++;
        }
        var total = Size * Size;
        var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return result + k * PenaltyN4;
    }

    /// <summary>Counts 1:1:3:1:1 dark-light patterns with a 4-module light side, which could be mistaken for a finder.</summary>
    static int CountFinderLikePatterns(int[] history)
    {
        var n = history[1];
        var core = n > 0 && history[2] == n && history[3] == n * 3 && history[4] == n && history[5] == n;
        return (core && history[0] >= n * 4 && history[6] >= n ? 1 : 0)
             + (core && history[6] >= n * 4 && history[0] >= n ? 1 : 0);
    }

    int TerminateRuns(bool currentRunColor, int currentRunLength, int[] history)
    {
        if (currentRunColor)
        {
            AddRun(currentRunLength, history);
            currentRunLength = 0;
        }
        currentRunLength += Size;   // the light border counts as a run
        AddRun(currentRunLength, history);
        return CountFinderLikePatterns(history);
    }

    void AddRun(int currentRunLength, int[] history)
    {
        if (history[0] == 0)
            currentRunLength += Size;
        Array.Copy(history, 0, history, 1, history.Length - 1);
        history[0] = currentRunLength;
    }

    void SetFunction(int x, int y, bool dark)
    {
        modules[y, x] = dark;
        isFunction[y, x] = true;
    }

    static bool Bit(int value, int index) => ((value >> index) & 1) != 0;
}
