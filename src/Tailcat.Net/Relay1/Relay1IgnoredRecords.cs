// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Net.Relay1;

/// <summary>
/// Counts the records one relay1 session ignored, and decides when that is
/// worth telling an observer.
/// </summary>
/// <remarks>
/// A relay cut can bring back up to the whole resend backlog of the session
/// before — thousands of records — and keys that disagree bring one with
/// every record until a heartbeat gives up. A report for each flooded the
/// operator's log. The first is reported at once, because the first is what
/// says something is happening; after that, at most one report per
/// <see cref="ReportInterval"/>, carrying how many were ignored since the last.
/// A steady stream still shows as a report every interval.
/// </remarks>
internal sealed class Relay1IgnoredRecords(TimeProvider time)
{
    internal static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(10);

    // Records arrive on the receive loop, but nothing promises only one.
    private readonly Lock _mu = new();
    private long _unreported;
    private long? _lastReportedAt;

    /// <summary>Counts one ignored record.</summary>
    /// <param name="toReport">How many to report, when a report is due.</param>
    /// <returns>Whether a report is due now.</returns>
    internal bool Count(out long toReport)
    {
        lock (_mu)
        {
            _unreported++;
            if (_lastReportedAt is long reportedAt && time.GetElapsedTime(reportedAt) < ReportInterval)
            {
                toReport = 0;
                return false;
            }

            toReport = _unreported;
            _unreported = 0;
            _lastReportedAt = time.GetTimestamp();
            return true;
        }
    }
}
