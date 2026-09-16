using QuickByte.Core.Helpers;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// Checking a finished file against its publisher's checksum. Parsing is the part
/// with the traps — people paste whatever the download page shows — and the
/// hashes themselves are pinned against the standard "abc" test vectors.
/// </summary>
public sealed class FileChecksumTests
{
    private const string Sha256Abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Md5Abc = "900150983cd24fb0d6963f7d28e17f72";
    private const string Sha1Abc = "a9993e364706816aba3e25717850c26c9cd0d89d";
    private const string Sha384Abc = "cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7";
    private const string Sha512Abc = "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f";

    // -------------------------------------------------------------- parsing --

    [Theory]
    [InlineData(Md5Abc, ChecksumAlgorithm.Md5)]
    [InlineData(Sha1Abc, ChecksumAlgorithm.Sha1)]
    [InlineData(Sha256Abc, ChecksumAlgorithm.Sha256)]
    [InlineData(Sha384Abc, ChecksumAlgorithm.Sha384)]
    [InlineData(Sha512Abc, ChecksumAlgorithm.Sha512)]
    public void A_bare_hex_value_says_which_hash_it_is_by_its_length(string hex, ChecksumAlgorithm expected)
    {
        var parsed = FileChecksum.Parse(hex);

        Assert.Equal(expected, parsed!.Algorithm);
        Assert.Equal(hex, parsed.Hex);
    }

    [Theory]
    [InlineData("sha256:" + Sha256Abc)]
    [InlineData("SHA-256=" + Sha256Abc)]
    [InlineData("SHA256 " + Sha256Abc)]
    [InlineData("  " + "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD" + "  ")]
    [InlineData(Sha256Abc + "  QuickByte-1.13.0-x64.exe")]
    [InlineData(Sha256Abc + " *QuickByte-1.13.0-x64.exe")]
    [InlineData("SHA256 (QuickByte-1.13.0-x64.exe) = " + Sha256Abc)]
    public void The_forms_download_pages_list_all_read_as_the_same_checksum(string pasted)
    {
        Assert.Equal(new ExpectedChecksum(ChecksumAlgorithm.Sha256, Sha256Abc), FileChecksum.Parse(pasted));
    }

    [Theory]
    [InlineData("md5:" + Sha256Abc)]
    [InlineData("sha1:" + Md5Abc)]
    public void A_named_hash_of_the_wrong_length_is_refused_rather_than_guessed(string pasted)
    {
        Assert.Null(FileChecksum.Parse(pasted));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a checksum")]
    [InlineData("zz7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015")]
    public void Anything_that_is_not_a_checksum_is_null(string? pasted)
    {
        Assert.Null(FileChecksum.Parse(pasted));
    }

    [Fact]
    public void The_stored_form_names_its_hash_and_reads_back_exactly()
    {
        var checksum = new ExpectedChecksum(ChecksumAlgorithm.Sha512, Sha512Abc);

        Assert.Equal("sha512:" + Sha512Abc, checksum.ToString());
        Assert.Equal(checksum, FileChecksum.FromStored(checksum.ToString()));
    }

    [Fact]
    public void Only_md5_and_sha1_are_called_weak()
    {
        Assert.True(FileChecksum.IsWeak(ChecksumAlgorithm.Md5));
        Assert.True(FileChecksum.IsWeak(ChecksumAlgorithm.Sha1));
        Assert.False(FileChecksum.IsWeak(ChecksumAlgorithm.Sha256));
    }

    // ------------------------------------------------------------ computing --

    [Theory]
    [InlineData(ChecksumAlgorithm.Md5, Md5Abc)]
    [InlineData(ChecksumAlgorithm.Sha1, Sha1Abc)]
    [InlineData(ChecksumAlgorithm.Sha256, Sha256Abc)]
    [InlineData(ChecksumAlgorithm.Sha384, Sha384Abc)]
    [InlineData(ChecksumAlgorithm.Sha512, Sha512Abc)]
    public async Task Every_hash_matches_its_standard_test_vector(ChecksumAlgorithm algorithm, string expected)
    {
        using var folder = new TempFolder();
        string file = folder.WriteFile("abc.txt", "abc"u8.ToArray());

        Assert.Equal(expected, await FileChecksum.ComputeAsync(file, algorithm, null, default));
    }

    [Fact]
    public async Task A_file_larger_than_one_read_hashes_the_same_as_in_one_piece()
    {
        using var folder = new TempFolder();
        byte[] content = Enumerable.Range(0, 3 * 1024 * 1024 + 17).Select(i => (byte)(i * 31)).ToArray();
        string file = folder.WriteFile("big.bin", content);

        string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

        Assert.Equal(expected, await FileChecksum.ComputeAsync(file, ChecksumAlgorithm.Sha256, null, default));
    }

    [Fact]
    public async Task Verifying_says_match_or_mismatch()
    {
        using var folder = new TempFolder();
        string file = folder.WriteFile("abc.txt", "abc"u8.ToArray());

        Assert.Equal(ChecksumResult.Match,
            await FileChecksum.VerifyAsync(file, new ExpectedChecksum(ChecksumAlgorithm.Md5, Md5Abc), null, default));
        Assert.Equal(ChecksumResult.Mismatch,
            await FileChecksum.VerifyAsync(file, new ExpectedChecksum(ChecksumAlgorithm.Md5, new string('0', 32)), null, default));
    }

    [Fact]
    public async Task Progress_ends_on_the_file_size()
    {
        using var folder = new TempFolder();
        string file = folder.WriteFile("abc.txt", "abc"u8.ToArray());
        var reports = new List<long>();

        await FileChecksum.ComputeAsync(file, ChecksumAlgorithm.Sha256, new Inline(reports.Add), default);

        Assert.Equal(3, reports[^1]);
    }

    private sealed class Inline : IProgress<long>
    {
        private readonly Action<long> _report;
        public Inline(Action<long> report) => _report = report;
        public void Report(long value) => _report(value);
    }
}
