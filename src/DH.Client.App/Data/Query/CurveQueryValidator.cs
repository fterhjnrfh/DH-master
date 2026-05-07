using System;
using System.Collections.Generic;

namespace DH.Client.App.Data.Query;

public sealed record CurveQueryValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors)
{
    public static CurveQueryValidationResult Valid { get; } =
        new(true, Array.Empty<string>());
}

public static class CurveQueryValidator
{
    public static CurveQueryValidationResult ValidateRequest(PreviewReadRequest request)
    {
        var errors = new List<string>();

        if (request.SessionId == Guid.Empty)
        {
            errors.Add("SessionId must not be empty.");
        }

        if (request.ChannelIds is null || request.ChannelIds.Count == 0)
        {
            errors.Add("ChannelIds must not be empty.");
        }

        if (request.WindowEnd <= request.WindowStart)
        {
            errors.Add("WindowEnd must be greater than WindowStart.");
        }

        if (request.MaxPointsPerChannel <= 0)
        {
            errors.Add("MaxPointsPerChannel must be greater than 0.");
        }

        return errors.Count == 0
            ? CurveQueryValidationResult.Valid
            : new CurveQueryValidationResult(false, errors);
    }

    public static CurveQueryValidationResult ValidateSnapshot(CurveWindowSnapshot snapshot)
    {
        var errors = new List<string>();

        if (snapshot.SessionId == Guid.Empty)
        {
            errors.Add("Snapshot.SessionId must not be empty.");
        }

        if (snapshot.WindowEnd <= snapshot.WindowStart)
        {
            errors.Add("Snapshot.WindowEnd must be greater than Snapshot.WindowStart.");
        }

        if (snapshot.ChannelIds.Count == 0 && snapshot.BuildState != BuildState.Missing)
        {
            errors.Add("Snapshot without channels must be Missing.");
        }

        if (!snapshot.IsComplete && snapshot.BuildState == BuildState.Ready)
        {
            errors.Add("Incomplete snapshot cannot have BuildState Ready.");
        }

        if (snapshot.TotalActualPoints < 0 || snapshot.MaxActualPointsPerChannel < 0)
        {
            errors.Add("Actual point counters must not be negative.");
        }

        return errors.Count == 0
            ? CurveQueryValidationResult.Valid
            : new CurveQueryValidationResult(false, errors);
    }
}
