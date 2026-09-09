using System;
using System.Buffers.Binary;

namespace Foot_Tracker.Services;

/// <summary>
/// §152. Scales the samples of a RIFF/WAVE file in memory, so a clip can play
/// quieter through players that have no volume control of their own -
/// winmm's PlaySound on Windows, aplay on Linux. Pure byte work: no I/O, no
/// platform calls, nothing static - the one part of the volume feature that
/// can be reasoned about on its own (see SoundNotificationService.ScaledWavePath
/// for the caching around it).
///
/// Handles what a .wav from any common tool contains: PCM 8, 16, 24 and
/// 32-bit, IEEE float 32 and 64-bit, plain or WAVE_FORMAT_EXTENSIBLE, with
/// every other chunk (LIST, fact, cue...) copied through untouched and the
/// header left exactly as it was - a scaled copy is the same length as its
/// source. Anything unrecognised comes back as null with the reason, and the
/// caller plays the original at full volume rather than guess at bytes.
/// </summary>
internal static class WaveVolume
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>A copy of <paramref name="wave"/> with every sample multiplied
    /// by <paramref name="gain"/> (0..1; anything above 1 is "nothing to do"
    /// and returns the input itself). Null, with <paramref name="reason"/>
    /// filled in, when the bytes are not a wave file this class understands.</summary>
    internal static byte[]? Scale(byte[] wave, double gain, out string reason)
    {
        reason = string.Empty;

        if (double.IsNaN(gain) || gain >= 1)
            return wave;

        if (gain < 0)
            gain = 0;

        if (!TryLocateSamples(wave, out ushort formatTag, out ushort bitsPerSample, out int dataStart, out int dataLength, out reason))
            return null;

        int bytesPerSample = bitsPerSample / 8;
        int wholeSamples = dataLength - dataLength % bytesPerSample;

        var scaled = new byte[wave.Length];
        Buffer.BlockCopy(wave, 0, scaled, 0, wave.Length);

        Span<byte> samples = scaled.AsSpan(dataStart, wholeSamples);

        switch (formatTag, bitsPerSample)
        {
            case (FormatPcm, 8):
                // 8-bit PCM is unsigned, silence at 128.
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (byte)Math.Clamp(128 + Math.Round((samples[i] - 128) * gain, MidpointRounding.AwayFromZero), 0, 255);
                break;

            case (FormatPcm, 16):
                for (int i = 0; i < samples.Length; i += 2)
                {
                    Span<byte> one = samples.Slice(i, 2);
                    short value = BinaryPrimitives.ReadInt16LittleEndian(one);
                    BinaryPrimitives.WriteInt16LittleEndian(one, (short)Math.Clamp(Math.Round(value * gain, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue));
                }
                break;

            case (FormatPcm, 24):
                for (int i = 0; i < samples.Length; i += 3)
                {
                    int value = samples[i] | (samples[i + 1] << 8) | (samples[i + 2] << 16);

                    if (value >= 0x800000)
                        value -= 0x1000000;

                    int result = (int)Math.Clamp(Math.Round(value * gain, MidpointRounding.AwayFromZero), -8388608, 8388607);
                    samples[i] = (byte)result;
                    samples[i + 1] = (byte)(result >> 8);
                    samples[i + 2] = (byte)(result >> 16);
                }
                break;

            case (FormatPcm, 32):
                for (int i = 0; i < samples.Length; i += 4)
                {
                    Span<byte> one = samples.Slice(i, 4);
                    int value = BinaryPrimitives.ReadInt32LittleEndian(one);
                    BinaryPrimitives.WriteInt32LittleEndian(one, (int)Math.Clamp(Math.Round(value * gain, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue));
                }
                break;

            case (FormatIeeeFloat, 32):
                for (int i = 0; i < samples.Length; i += 4)
                {
                    Span<byte> one = samples.Slice(i, 4);
                    BinaryPrimitives.WriteSingleLittleEndian(one, (float)(BinaryPrimitives.ReadSingleLittleEndian(one) * gain));
                }
                break;

            case (FormatIeeeFloat, 64):
                for (int i = 0; i < samples.Length; i += 8)
                {
                    Span<byte> one = samples.Slice(i, 8);
                    BinaryPrimitives.WriteDoubleLittleEndian(one, BinaryPrimitives.ReadDoubleLittleEndian(one) * gain);
                }
                break;

            default:
                reason = $"format tag {formatTag} with {bitsPerSample} bits per sample is not supported";
                return null;
        }

        return scaled;
    }

    /// <summary>Walks the RIFF chunks for the sample format and the sample
    /// bytes. The data chunk's declared size is trusted only as far as the
    /// file goes - a streaming writer can leave it as 0xFFFFFFFF.</summary>
    private static bool TryLocateSamples(
        byte[] wave, out ushort formatTag, out ushort bitsPerSample, out int dataStart, out int dataLength, out string reason)
    {
        formatTag = 0;
        bitsPerSample = 0;
        dataStart = 0;
        dataLength = 0;
        reason = string.Empty;

        if (wave.Length < 12 || !IsTag(wave, 0, "RIFF") || !IsTag(wave, 8, "WAVE"))
        {
            reason = "not a RIFF/WAVE file";
            return false;
        }

        bool haveFormat = false;
        int position = 12;

        while (position + 8 <= wave.Length)
        {
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(position + 4, 4));
            int bodyStart = position + 8;
            int available = wave.Length - bodyStart;

            if (IsTag(wave, position, "fmt "))
            {
                if (chunkSize < 16 || available < 16)
                {
                    reason = "the fmt chunk is too short";
                    return false;
                }

                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(bodyStart, 2));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(bodyStart + 14, 2));

                // WAVE_FORMAT_EXTENSIBLE: the real format is the first two
                // bytes of the SubFormat GUID, 24 bytes into the chunk.
                if (formatTag == FormatExtensible)
                {
                    if (chunkSize < 40 || available < 40)
                    {
                        reason = "the extensible fmt chunk is too short";
                        return false;
                    }

                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(bodyStart + 24, 2));
                }

                haveFormat = true;
            }
            else if (IsTag(wave, position, "data"))
            {
                if (!haveFormat)
                {
                    reason = "the data chunk comes before the fmt chunk";
                    return false;
                }

                if (bitsPerSample is not (8 or 16 or 24 or 32 or 64))
                {
                    reason = $"{bitsPerSample} bits per sample is not supported";
                    return false;
                }

                dataStart = bodyStart;
                dataLength = chunkSize > (uint)available ? available : (int)chunkSize;
                return true;
            }

            // The next chunk - RIFF pads an odd-sized chunk to an even boundary.
            long next = bodyStart + (long)chunkSize + (chunkSize & 1);

            if (next > wave.Length)
                break;

            position = (int)next;
        }

        reason = haveFormat ? "no data chunk" : "no fmt chunk";
        return false;
    }

    private static bool IsTag(byte[] wave, int offset, string tag) =>
        wave[offset] == tag[0] && wave[offset + 1] == tag[1] && wave[offset + 2] == tag[2] && wave[offset + 3] == tag[3];
}
