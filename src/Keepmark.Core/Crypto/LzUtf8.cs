namespace Keepmark.Core.Crypto;

// Faithful port of the lzutf8@0.6.0 single-block format (see analysis of
// Compressor.compressUtf8Block / Decompressor.decompressBlock).
//
// Wire format (no container header, single block):
//   - raw byte:       any value < 0xC0 is copied verbatim
//   - UTF-8 lead byte (>= 0xC0) followed by a continuation byte (>= 0x80)
//     is also verbatim (valid UTF-8 is therefore always safe raw)
//   - pointer:        0xC0|len + 1 distance byte  (distance 1..127)
//                     0xE0|len + 2 distance bytes (distance 128..32767, big-endian)
//                     len is 4..31
//
// Byte-identical output vs. the JS compressor is NOT required: any valid token
// stream decoding to the same bytes interoperates. The decompressor is exact.
internal static class LzUtf8
{
    private const int MinMatch = 4;
    private const int MaxMatch = 31;
    private const int MaxDistance = 32767;
    private const int HashSize = 65537;
    private const int MaxBucketEntries = 64;

    public static byte[] Compress(byte[] input)
    {
        if (input.Length == 0)
        {
            return [];
        }

        var output = new List<byte>(input.Length);
        var buckets = new Dictionary<int, List<int>>();

        int n = input.Length;
        int pos = 0;
        while (pos < n)
        {
            if (pos <= n - MinMatch && TryFindMatch(input, buckets, pos, out int length, out int distance))
            {
                if (distance < 128)
                {
                    output.Add((byte)(0xC0 | length));
                    output.Add((byte)distance);
                }
                else
                {
                    output.Add((byte)(0xE0 | length));
                    output.Add((byte)(distance >> 8));
                    output.Add((byte)(distance & 0xFF));
                }

                // Index skipped positions so later matches can reference them,
                // mirroring the JS scanner which hashes every position.
                for (int k = 1; k < length && pos + k <= n - MinMatch; k++)
                {
                    AddPosition(buckets, input, pos + k);
                }

                pos += length;
            }
            else
            {
                output.Add(input[pos]);
                if (pos <= n - MinMatch)
                {
                    AddPosition(buckets, input, pos);
                }

                pos++;
            }
        }

        return output.ToArray();
    }

    public static byte[] Decompress(byte[] input)
    {
        var output = new List<byte>(Math.Max(input.Length * 2, 64));

        int pos = 0;
        while (pos < input.Length)
        {
            byte b = input[pos];
            if (b < 0xC0)
            {
                output.Add(b);
                pos++;
                continue;
            }

            int id = b >> 5; // 6 (0xC0-0xDF) or 7 (0xE0-0xFF)
            bool atEnd = pos == input.Length - 1 || (pos == input.Length - 2 && id == 7);
            if (atEnd)
            {
                throw new InvalidDataException("Truncated LZUTF8 stream.");
            }

            if (input[pos + 1] >= 0x80)
            {
                // UTF-8 multibyte lead byte: verbatim.
                output.Add(b);
                pos++;
                continue;
            }

            int length = b & 31;
            int distance;
            if (id == 6)
            {
                distance = input[pos + 1];
                pos += 2;
            }
            else
            {
                distance = (input[pos + 1] << 8) | input[pos + 2];
                pos += 3;
            }

            int matchPos = output.Count - distance;
            if (matchPos < 0 || distance <= 0)
            {
                throw new InvalidDataException("Invalid LZUTF8 match distance.");
            }

            for (int k = 0; k < length; k++)
            {
                output.Add(output[matchPos + k]);
            }
        }

        return output.ToArray();
    }

    private static bool TryFindMatch(
        byte[] input, Dictionary<int, List<int>> buckets, int pos, out int length, out int distance)
    {
        length = 0;
        distance = 0;

        if (!buckets.TryGetValue(HashPrefix(input, pos), out var bucket))
        {
            return false;
        }

        int n = input.Length;
        for (int i = bucket.Count - 1; i >= 0; i--)
        {
            int cand = bucket[i];
            int dist = pos - cand;
            if (dist <= 0 || dist > MaxDistance)
            {
                continue;
            }

            int maxLen = Math.Min(MaxMatch, n - pos);
            int len = 0;
            while (len < maxLen && input[cand + len] == input[pos + len])
            {
                len++;
            }

            if (len >= MinMatch && len > length)
            {
                length = len;
                distance = dist;
                if (len == MaxMatch)
                {
                    break;
                }
            }
        }

        return length >= MinMatch;
    }

    private static void AddPosition(Dictionary<int, List<int>> buckets, byte[] input, int pos)
    {
        int h = HashPrefix(input, pos);
        if (!buckets.TryGetValue(h, out var bucket))
        {
            buckets[h] = bucket = new List<int>(4);
        }
        else if (bucket.Count == MaxBucketEntries - 1)
        {
            bucket.RemoveRange(0, MaxBucketEntries / 2);
        }

        bucket.Add(pos);
    }

    private static int HashPrefix(byte[] input, int pos) =>
        (int)(((long)input[pos] * 7880599 + (long)input[pos + 1] * 39601 +
               (long)input[pos + 2] * 199 + input[pos + 3]) % HashSize);
}
