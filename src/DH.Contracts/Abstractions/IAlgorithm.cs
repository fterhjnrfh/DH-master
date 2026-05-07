using DH.Contracts.Models;

namespace DH.Contracts.Abstractions;

public interface IAlgorithm
{
    string Name { get; }
    AlgorithmResult Execute(IReadOnlyList<IDataFrame> frames, CancellationToken ct = default);
}