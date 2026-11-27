// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Coarse time-of-day bucket used to pick a contextually-appropriate
    /// greeting or goodbye phrase — see GreetingPhrases.
    /// </summary>
    public enum TimeOfDayBucket
    {
        /// <summary>05:00–11:59</summary>
        Morning,

        /// <summary>12:00–13:59</summary>
        Noon,

        /// <summary>14:00–17:59</summary>
        Afternoon,

        /// <summary>18:00–22:59</summary>
        Evening,

        /// <summary>23:00–04:59</summary>
        AfterMidnight
    }
}
