using System.Text;
using System.Text.Unicode;

namespace WsSearchMcp.Http;

public enum FileTextEncoding { Binary, Utf8, Utf16Le, Utf16Be }

/// <summary>
/// Text/binary detection and character-boundary trimming for chunked file reads, so a chunk never
/// ends in the middle of a character and <c>nextOffset</c> always lands on a character boundary.
/// </summary>
public static class FileChunking
{
    /// <summary>Classifies a file from its first bytes. A BOM wins; otherwise NUL bytes or invalid UTF-8 mean binary.</summary>
    public static FileTextEncoding Detect(ReadOnlySpan<byte> head, out int bomLength)
    {
        bomLength = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) { bomLength = 3; return FileTextEncoding.Utf8; }
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            if (head.Length >= 4 && head[2] == 0 && head[3] == 0) return FileTextEncoding.Binary; // UTF-32 LE
            bomLength = 2;
            return FileTextEncoding.Utf16Le;
        }
        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF) { bomLength = 2; return FileTextEncoding.Utf16Be; }

        if (head.IndexOf((byte)0) >= 0) return FileTextEncoding.Binary;
        // The head may end mid-character; judge only the complete part.
        return Utf8.IsValid(head[..TrimIncompleteUtf8Tail(head)]) ? FileTextEncoding.Utf8 : FileTextEncoding.Binary;
    }

    /// <summary>Length of <paramref name="data"/> without a trailing incomplete UTF-8 sequence.</summary>
    public static int TrimIncompleteUtf8Tail(ReadOnlySpan<byte> data)
    {
        var n = data.Length;
        // Look back at most 3 bytes for a lead byte whose sequence does not fit.
        for (var back = 1; back <= Math.Min(3, n); back++)
        {
            var b = data[n - back];
            if ((b & 0xC0) == 0x80) continue;          // continuation byte: keep looking for its lead
            var need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return need > back ? n - back : n;           // lead byte of an incomplete sequence → cut before it
        }
        return n;
    }

    /// <summary>
    /// Trims <paramref name="count"/> bytes of <paramref name="data"/> to whole characters for
    /// <paramref name="encoding"/>. At end of file nothing is trimmed (a truncated tail is just data).
    /// </summary>
    public static int TrimToCharacterBoundary(ReadOnlySpan<byte> data, int count, FileTextEncoding encoding, bool atEndOfFile)
    {
        if (atEndOfFile || count == 0) return count;
        switch (encoding)
        {
            case FileTextEncoding.Utf16Le:
            case FileTextEncoding.Utf16Be:
                count &= ~1; // whole 16-bit units
                if (count >= 2)
                {
                    // Do not split a surrogate pair: drop a trailing high surrogate.
                    var hi = encoding == FileTextEncoding.Utf16Le ? data[count - 1] : data[count - 2];
                    if (hi is >= 0xD8 and <= 0xDB) count -= 2;
                }
                return count;
            default:
                return TrimIncompleteUtf8Tail(data[..count]);
        }
    }

    /// <summary>Decodes a chunk. A BOM at the very start of the file (<paramref name="atFileStart"/>) is dropped.</summary>
    public static string Decode(ReadOnlySpan<byte> data, FileTextEncoding encoding, bool atFileStart)
    {
        var enc = encoding switch
        {
            FileTextEncoding.Utf16Le => Encoding.Unicode,
            FileTextEncoding.Utf16Be => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8,
        };
        var s = enc.GetString(data);
        return atFileStart && s.Length > 0 && s[0] == '﻿' ? s[1..] : s;
    }

    public static string Label(FileTextEncoding e) => e switch
    {
        FileTextEncoding.Utf8 => "utf-8",
        FileTextEncoding.Utf16Le => "utf-16le",
        FileTextEncoding.Utf16Be => "utf-16be",
        _ => "binary",
    };
}
