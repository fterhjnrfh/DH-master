using System;
using System.Runtime.InteropServices;

namespace DH.Client.App.Services.Storage;

internal sealed class FloatSampleCompressionResult
{
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    public int OriginalBytes { get; init; }

    public int PayloadBytes { get; init; }

    public CompressionType Algorithm { get; init; }

    public PreprocessType Preprocess { get; init; }
}

internal static class FloatSampleCompressionCodec
{
    public static FloatSampleCompressionResult Encode(
        ReadOnlySpan<float> samples,
        StorageCompressionSettings settings)
    {
        int originalBytes = checked(samples.Length * sizeof(float));
        byte[] rawBytes = new byte[originalBytes];
        MemoryMarshal.AsBytes(samples).CopyTo(rawBytes);

        if (!settings.Enabled || (settings.Algorithm == CompressionType.None && settings.Preprocess == PreprocessType.None))
        {
            return new FloatSampleCompressionResult
            {
                Payload = rawBytes,
                OriginalBytes = originalBytes,
                PayloadBytes = rawBytes.Length,
                Algorithm = CompressionType.None,
                Preprocess = PreprocessType.None
            };
        }

        byte[] processedBytes = rawBytes;
        if (settings.Preprocess != PreprocessType.None && samples.Length > 1)
        {
            uint[] words = new uint[samples.Length];
            Buffer.BlockCopy(rawBytes, 0, words, 0, rawBytes.Length);
            ApplyPreprocess(words, settings.Preprocess);
            processedBytes = new byte[rawBytes.Length];
            Buffer.BlockCopy(words, 0, processedBytes, 0, processedBytes.Length);
        }

        byte[] payload;
        int payloadSize;
        if (settings.Algorithm == CompressionType.None)
        {
            payload = processedBytes;
            payloadSize = processedBytes.Length;
        }
        else
        {
            (payload, payloadSize) = StorageCodec.CompressBytes(processedBytes, settings.Algorithm, settings.Options);
            if (payloadSize != payload.Length)
            {
                Array.Resize(ref payload, payloadSize);
            }
        }

        return new FloatSampleCompressionResult
        {
            Payload = payload,
            OriginalBytes = originalBytes,
            PayloadBytes = payloadSize,
            Algorithm = settings.Algorithm,
            Preprocess = settings.Preprocess
        };
    }

    public static float[] Decode(
        byte[] payload,
        int payloadBytes,
        int sampleCount,
        CompressionType algorithm,
        PreprocessType preprocess)
    {
        int originalBytes = checked(sampleCount * sizeof(float));
        byte[] rawBytes = algorithm == CompressionType.None
            ? CopyPayload(payload, payloadBytes, originalBytes)
            : StorageCodec.DecompressBytes(payload, payloadBytes, originalBytes, algorithm);

        if (preprocess != PreprocessType.None && sampleCount > 1)
        {
            uint[] words = new uint[sampleCount];
            Buffer.BlockCopy(rawBytes, 0, words, 0, originalBytes);
            ReversePreprocess(words, preprocess);
            Buffer.BlockCopy(words, 0, rawBytes, 0, originalBytes);
        }

        float[] samples = new float[sampleCount];
        Buffer.BlockCopy(rawBytes, 0, samples, 0, originalBytes);
        return samples;
    }

    private static byte[] CopyPayload(byte[] payload, int payloadBytes, int expectedBytes)
    {
        if (payloadBytes != expectedBytes)
        {
            throw new InvalidOperationException($"Uncompressed payload size mismatch. Expected {expectedBytes}, actual {payloadBytes}.");
        }

        byte[] copy = new byte[expectedBytes];
        Buffer.BlockCopy(payload, 0, copy, 0, expectedBytes);
        return copy;
    }

    private static void ApplyPreprocess(uint[] words, PreprocessType preprocess)
    {
        switch (preprocess)
        {
            case PreprocessType.DiffOrder1:
                ApplyDiffOrder1(words);
                break;
            case PreprocessType.DiffOrder2:
                ApplyDiffOrder1(words);
                ApplyDiffOrder1(words);
                break;
            case PreprocessType.LinearPrediction:
                ApplyLinearPrediction(words);
                break;
        }
    }

    private static void ReversePreprocess(uint[] words, PreprocessType preprocess)
    {
        switch (preprocess)
        {
            case PreprocessType.DiffOrder1:
                ReverseDiffOrder1(words);
                break;
            case PreprocessType.DiffOrder2:
                ReverseDiffOrder1(words);
                ReverseDiffOrder1(words);
                break;
            case PreprocessType.LinearPrediction:
                ReverseLinearPrediction(words);
                break;
        }
    }

    private static void ApplyDiffOrder1(uint[] words)
    {
        uint previous = words[0];
        for (int i = 1; i < words.Length; i++)
        {
            uint current = words[i];
            words[i] = unchecked(current - previous);
            previous = current;
        }
    }

    private static void ReverseDiffOrder1(uint[] words)
    {
        for (int i = 1; i < words.Length; i++)
        {
            words[i] = unchecked(words[i] + words[i - 1]);
        }
    }

    private static void ApplyLinearPrediction(uint[] words)
    {
        if (words.Length <= 1)
        {
            return;
        }

        uint previous2 = words[0];
        uint previous1 = words[1];
        words[1] = unchecked(previous1 - previous2);
        for (int i = 2; i < words.Length; i++)
        {
            uint current = words[i];
            uint predicted = unchecked((2u * previous1) - previous2);
            words[i] = unchecked(current - predicted);
            previous2 = previous1;
            previous1 = current;
        }
    }

    private static void ReverseLinearPrediction(uint[] words)
    {
        if (words.Length <= 1)
        {
            return;
        }

        words[1] = unchecked(words[1] + words[0]);
        for (int i = 2; i < words.Length; i++)
        {
            uint predicted = unchecked((2u * words[i - 1]) - words[i - 2]);
            words[i] = unchecked(words[i] + predicted);
        }
    }
}
