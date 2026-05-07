using DH.Contracts.Models;
namespace DH.Contracts.Abstractions;

public interface ICurveRenderer
{
    IReadOnlyList<CurvePoint> ToCurvePoints(IReadOnlyList<IDataFrame> frames);
}