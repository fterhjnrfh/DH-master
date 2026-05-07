namespace DH.Contracts.Abstractions;

public interface ISignalDecoder
{
    bool TryDecode(ReadOnlySpan<byte> packet, out IDataFrame frame);
}