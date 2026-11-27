// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Picks a random, time-of-day-appropriate greeting or goodbye
    /// phrase. Pure and stateless aside from the one shared Random
    /// instance — safe to call from the UI thread (the only place it's
    /// ever used: once at startup, once at shutdown), which is also why
    /// a single lock around that Random instance is sufficient rather
    /// than a per-thread instance — System.Random isn't thread-safe,
    /// but there's realistically never contention here.
    /// </summary>
    public static class GreetingPhrases
    {
        private static readonly Random Rng = new Random();
        private static readonly object RngLock = new object();

        private static readonly Dictionary<TimeOfDayBucket, string[]> Greetings = new Dictionary<TimeOfDayBucket, string[]>
        {
            [TimeOfDayBucket.Morning] = new[]
            {
                "Good morning! Ready to explore?",
                "Morning! Let's get things organized.",
                "Rise and shine — ByteExplorer's ready when you are.",
                "Good morning! Hope your files behave today."
            },
            [TimeOfDayBucket.Noon] = new[]
            {
                "Good midday! Right on time.",
                "Hello! Perfect break for a bit of file browsing.",
                "Good noon! Let's see what's new.",
                "Hi there — lunchtime browsing, nice choice."
            },
            [TimeOfDayBucket.Afternoon] = new[]
            {
                "Good afternoon! Let's get to it.",
                "Afternoon! Ready when you are.",
                "Hello! Hope your day's going smoothly.",
                "Good afternoon — let's find what you need."
            },
            [TimeOfDayBucket.Evening] = new[]
            {
                "Good evening! Welcome back.",
                "Evening! Let's wrap up the day's files.",
                "Good evening — settling in for some browsing?",
                "Hello! Hope you had a good day."
            },
            [TimeOfDayBucket.AfterMidnight] = new[]
            {
                "Burning the midnight oil? Welcome back.",
                "Still up? Let's make this quick and painless.",
                "Good to see you this late — or is it early?",
                "Night owl mode activated. Welcome back."
            }
        };

        private static readonly Dictionary<TimeOfDayBucket, string[]> Goodbyes = new Dictionary<TimeOfDayBucket, string[]>
        {
            [TimeOfDayBucket.Morning] = new[]
            {
                "Have a great morning ahead!",
                "Off you go — have a productive day!",
                "See you later — enjoy your morning!",
                "Goodbye! Make it a good one."
            },
            [TimeOfDayBucket.Noon] = new[]
            {
                "Enjoy the rest of your day!",
                "See you soon — enjoy lunch!",
                "Goodbye! Back to it when you're ready.",
                "Take care — catch you later!"
            },
            [TimeOfDayBucket.Afternoon] = new[]
            {
                "Have a great rest of your afternoon!",
                "See you later — enjoy the rest of your day!",
                "Goodbye! Hope the rest of your day goes well.",
                "Take it easy — catch you soon!"
            },
            [TimeOfDayBucket.Evening] = new[]
            {
                "Have a wonderful evening!",
                "Enjoy your evening — see you soon!",
                "Take care! Rest well this evening.",
                "Goodbye — hope you had a good one today!"
            },
            [TimeOfDayBucket.AfterMidnight] = new[]
            {
                "Get some rest — goodnight!",
                "It's late — sleep well!",
                "Goodnight! Don't stay up too much longer.",
                "See you soon — go catch some sleep!"
            }
        };

        /// <summary>Buckets an hour (0-23) into a TimeOfDayBucket — 05-11 Morning, 12-13 Noon, 14-17 Afternoon, 18-22 Evening, 23/00-04 AfterMidnight.</summary>
        public static TimeOfDayBucket GetBucket(DateTime localTime)
        {
            int hour = localTime.Hour;

            if (hour >= 5 && hour <= 11)
            {
                return TimeOfDayBucket.Morning;
            }

            if (hour >= 12 && hour <= 13)
            {
                return TimeOfDayBucket.Noon;
            }

            if (hour >= 14 && hour <= 17)
            {
                return TimeOfDayBucket.Afternoon;
            }

            if (hour >= 18 && hour <= 22)
            {
                return TimeOfDayBucket.Evening;
            }

            return TimeOfDayBucket.AfterMidnight; // 23, 0-4
        }

        public static string RandomGreeting(DateTime localTime) => Pick(Greetings, GetBucket(localTime));

        public static string RandomGoodbye(DateTime localTime) => Pick(Goodbyes, GetBucket(localTime));

        private static string Pick(Dictionary<TimeOfDayBucket, string[]> table, TimeOfDayBucket bucket)
        {
            // Falls back to a generic phrase rather than throwing if a
            // bucket somehow has no entries — defensive against a future
            // edit to the tables above accidentally leaving one empty,
            // since a popup failing to appear is a far smaller problem
            // than an unhandled exception at startup or shutdown.
            if (!table.TryGetValue(bucket, out string[] options) || options == null || options.Length == 0)
            {
                return "Hello!";
            }

            lock (RngLock)
            {
                return options[Rng.Next(options.Length)];
            }
        }
    }
}
