using System.Text;
using WsSearchMcp.Http;
using Xunit;

namespace WsSearchMcp.Tests.Files;

public class FileChunkingTests
{
    [Fact]
    public void Detects_bom_encodings()
    {
        Assert.Equal(FileTextEncoding.Utf8, FileChunking.Detect(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, out var b1));
        Assert.Equal(3, b1);
        Assert.Equal(FileTextEncoding.Utf16Le, FileChunking.Detect(new byte[] { 0xFF, 0xFE, 0x61, 0 }, out var b2));
        Assert.Equal(2, b2);
        Assert.Equal(FileTextEncoding.Utf16Be, FileChunking.Detect(new byte[] { 0xFE, 0xFF, 0, 0x61 }, out _));
        Assert.Equal(FileTextEncoding.Binary, FileChunking.Detect(new byte[] { 0xFF, 0xFE, 0, 0, 0x61, 0, 0, 0 }, out _)); // UTF-32
    }

    [Fact]
    public void Plain_utf8_and_empty_are_text_nul_and_invalid_bytes_are_binary()
    {
        Assert.Equal(FileTextEncoding.Utf8, FileChunking.Detect("héllo €"u8, out _));
        Assert.Equal(FileTextEncoding.Utf8, FileChunking.Detect(ReadOnlySpan<byte>.Empty, out _));
        Assert.Equal(FileTextEncoding.Binary, FileChunking.Detect(new byte[] { 0x61, 0x00, 0x62 }, out _));
        Assert.Equal(FileTextEncoding.Binary, FileChunking.Detect(new byte[] { 0x80, 0x81, 0x82, 0x83 }, out _));
        Assert.Equal(FileTextEncoding.Binary, FileChunking.Detect(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 }, out _)); // PNG
    }

    [Fact]
    public void A_head_cut_in_the_middle_of_a_character_is_still_text()
    {
        var bytes = Encoding.UTF8.GetBytes("abc€"); // € is 3 bytes
        Assert.Equal(FileTextEncoding.Utf8, FileChunking.Detect(bytes.AsSpan(0, bytes.Length - 1), out _));
    }

    [Theory]
    [InlineData("a", 1)]
    [InlineData("é", 2)]
    [InlineData("€", 3)]
    [InlineData("😀", 4)]
    public void Utf8_trim_keeps_complete_characters_and_cuts_partial_ones(string ch, int bytes)
    {
        var data = Encoding.UTF8.GetBytes("xy" + ch);
        Assert.Equal(data.Length, FileChunking.TrimIncompleteUtf8Tail(data));
        for (var cut = 1; cut < bytes; cut++)
            Assert.Equal(2, FileChunking.TrimIncompleteUtf8Tail(data.AsSpan(0, 2 + cut)));
    }

    [Fact]
    public void Trim_never_cuts_at_end_of_file()
    {
        var data = Encoding.UTF8.GetBytes("a€").AsSpan(0, 3).ToArray(); // truncated tail at EOF
        Assert.Equal(3, FileChunking.TrimToCharacterBoundary(data, 3, FileTextEncoding.Utf8, atEndOfFile: true));
        Assert.Equal(1, FileChunking.TrimToCharacterBoundary(data, 3, FileTextEncoding.Utf8, atEndOfFile: false));
    }

    [Fact]
    public void Utf16_trim_uses_whole_units_and_never_splits_a_surrogate_pair()
    {
        var data = Encoding.Unicode.GetBytes("ab😀"); // a b + high+low surrogate = 8 bytes
        Assert.Equal(8, data.Length);
        Assert.Equal(4, FileChunking.TrimToCharacterBoundary(data, 5, FileTextEncoding.Utf16Le, false));  // odd → 4
        Assert.Equal(4, FileChunking.TrimToCharacterBoundary(data, 6, FileTextEncoding.Utf16Le, false));  // ends after high surrogate
        Assert.Equal(8, FileChunking.TrimToCharacterBoundary(data, 8, FileTextEncoding.Utf16Le, false));
        var be = Encoding.BigEndianUnicode.GetBytes("ab😀");
        Assert.Equal(4, FileChunking.TrimToCharacterBoundary(be, 6, FileTextEncoding.Utf16Be, false));
    }

    [Fact]
    public void Decode_strips_a_bom_only_at_the_start_of_the_file()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("hi"u8.ToArray()).ToArray();
        Assert.Equal("hi", FileChunking.Decode(withBom, FileTextEncoding.Utf8, atFileStart: true));
        Assert.Equal("﻿hi", FileChunking.Decode(withBom, FileTextEncoding.Utf8, atFileStart: false));
        var u16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hé")).ToArray();
        Assert.Equal("hé", FileChunking.Decode(u16, FileTextEncoding.Utf16Le, atFileStart: true));
    }
}
