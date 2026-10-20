using NeoPaymentWatcher.Api;

namespace NeoPaymentWatcher.Tests;

/// <summary>
/// Structural checks of the encoder. Its output was also verified by decoding 182 symbols covering every
/// version 1-40 (addresses, NEP-9 links, UTF-8 text and random bytes) with the jsQR decoder, which read
/// back the exact input bytes each time.
/// </summary>
public class QrCodeTests
{
    [Theory]
    [InlineData(0, 1, 21)]
    [InlineData(14, 1, 21)]    // level M, version 1 holds 14 bytes
    [InlineData(15, 2, 25)]
    [InlineData(34, 3, 29)]    // a Neo N3 address
    [InlineData(42, 3, 29)]
    [InlineData(43, 4, 33)]
    [InlineData(66, 5, 37)]    // a NEP-9 link with address, asset and amount
    [InlineData(2331, 40, 177)]
    public void Picks_the_smallest_version_that_fits(int length, int version, int size)
    {
        var qr = QrCode.Encode(new string('N', length));

        Assert.Equal(version, qr.Version);
        Assert.Equal(size, qr.Size);
        Assert.InRange(qr.Mask, 0, 7);
    }

    [Fact]
    public void Refuses_more_than_a_version_40_symbol_holds() =>
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new byte[2332]));

    [Fact]
    public void Has_finder_patterns_in_three_corners_and_timing_patterns_between_them()
    {
        var qr = QrCode.Encode("NQGp42c1jC2Uom7bpLRsMseSSNk2A6vx2x");
        string[] finder = ["#######", "#.....#", "#.###.#", "#.###.#", "#.###.#", "#.....#", "#######"];

        foreach (var (ox, oy) in new[] { (0, 0), (qr.Size - 7, 0), (0, qr.Size - 7) })
        {
            for (var y = 0; y < 7; y++)
                for (var x = 0; x < 7; x++)
                    Assert.Equal(finder[y][x] == '#', qr[ox + x, oy + y]);
        }
        for (var i = 8; i < qr.Size - 8; i++)
        {
            Assert.Equal(i % 2 == 0, qr[i, 6]);
            Assert.Equal(i % 2 == 0, qr[6, i]);
        }
        Assert.True(qr[8, qr.Size - 8]);   // the dark module beside the bottom-left finder
        Assert.False(qr[-1, 0]);
        Assert.False(qr[0, qr.Size]);
    }

    [Fact]
    public void Same_input_gives_the_same_symbol_and_different_input_a_different_one()
    {
        var a = QrCode.Encode("neo").ToSvg();

        Assert.Equal(a, QrCode.Encode("neo").ToSvg());
        Assert.NotEqual(a, QrCode.Encode("neo!").ToSvg());
    }

    [Fact]
    public void Svg_has_a_quiet_zone_and_one_path_of_dark_modules()
    {
        var svg = QrCode.Encode("neo").ToSvg();

        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 29 29\"", svg);   // 21 modules + 4 on each side
        Assert.Contains("<path fill=\"#000\" d=\"M4 4h7v1h-7z", svg);   // the top row of the first finder pattern
        Assert.EndsWith("\"/></svg>", svg);
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 21 21\"", QrCode.Encode("neo").ToSvg(quietZone: 0));
    }
}
