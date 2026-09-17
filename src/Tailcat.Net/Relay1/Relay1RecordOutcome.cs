// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Net.Relay1;

/// <summary>What became of one record taken off the relay.</summary>
internal enum Relay1RecordOutcome
{
    /// <summary>Taken, or recognised as a copy of one already taken.</summary>
    Taken,

    /// <summary>
    /// Ignored because it would not open under this session's keys: usually
    /// the session before's, resent after a cut, but also every record of an
    /// end whose keys disagree with these — which is why it is reported rather
    /// than swallowed.
    /// </summary>
    Unopened,

    /// <summary>The session cannot continue and the caller closes it.</summary>
    SessionBroken,
}
