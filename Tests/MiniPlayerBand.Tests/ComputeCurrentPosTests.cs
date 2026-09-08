using System;
using MiniPlayerBand;
using Xunit;

namespace MiniPlayerBand.Tests
{
    public class ComputeCurrentPosTests
    {
        static readonly DateTimeOffset Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        const double StopwatchFrequency = 10_000_000; // Stopwatch.Frequency on most Windows hosts (100ns ticks)

        [Fact]
        public void ComputeCurrentPos_StaysAtLastPos_WhenPaused()
        {
            var lastPos = TimeSpan.FromSeconds(30);

            var result = PlayerControl.ComputeCurrentPos(lastPos, TimeSpan.Zero, TimeSpan.FromSeconds(100),
                playing: false, lastUpdated: Now, now: Now.AddSeconds(5), tlStampTicks: 0, nowTicks: 0, frequency: StopwatchFrequency);

            Assert.Equal(lastPos, result);
        }

        [Fact]
        public void ComputeCurrentPos_InterpolatesFromLastUpdated_WhenPlayingAndTimestampIsFresh()
        {
            var lastPos = TimeSpan.FromSeconds(30);
            var lastUpdated = Now;
            var now = Now.AddSeconds(5);

            var result = PlayerControl.ComputeCurrentPos(lastPos, TimeSpan.Zero, TimeSpan.FromSeconds(100),
                playing: true, lastUpdated: lastUpdated, now: now, tlStampTicks: 0, nowTicks: 0, frequency: StopwatchFrequency);

            Assert.Equal(TimeSpan.FromSeconds(35), result);
        }

        [Fact]
        public void ComputeCurrentPos_FallsBackToStopwatchDelta_WhenLastUpdatedIsDefault()
        {
            var lastPos = TimeSpan.FromSeconds(30);
            long tlStampTicks = 0;
            long nowTicks = (long)(5 * StopwatchFrequency); // 5 seconds later on the Stopwatch clock

            var result = PlayerControl.ComputeCurrentPos(lastPos, TimeSpan.Zero, TimeSpan.FromSeconds(100),
                playing: true, lastUpdated: default, now: Now, tlStampTicks: tlStampTicks, nowTicks: nowTicks, frequency: StopwatchFrequency);

            Assert.Equal(TimeSpan.FromSeconds(35), result);
        }

        [Fact]
        public void ComputeCurrentPos_ClampsToStart_WhenInterpolationGoesNegative()
        {
            var lastPos = TimeSpan.FromSeconds(2);
            var lastUpdated = Now;
            var now = Now.AddSeconds(-5); // now before lastUpdated: elapsed is negative, ignored

            var result = PlayerControl.ComputeCurrentPos(lastPos, TimeSpan.Zero, TimeSpan.FromSeconds(100),
                playing: true, lastUpdated: lastUpdated, now: now, tlStampTicks: 0, nowTicks: 0, frequency: StopwatchFrequency);

            Assert.Equal(lastPos, result);
        }

        [Fact]
        public void ComputeCurrentPos_ClampsToEnd_WhenInterpolationOverruns()
        {
            var lastPos = TimeSpan.FromSeconds(95);
            var lastUpdated = Now;
            var now = Now.AddSeconds(30);
            var end = TimeSpan.FromSeconds(100);

            var result = PlayerControl.ComputeCurrentPos(lastPos, TimeSpan.Zero, end,
                playing: true, lastUpdated: lastUpdated, now: now, tlStampTicks: 0, nowTicks: 0, frequency: StopwatchFrequency);

            Assert.Equal(end, result);
        }

        [Fact]
        public void ComputeCurrentPos_DoesNotClampToEnd_WhenDurationIsUnknown()
        {
            var lastPos = TimeSpan.FromSeconds(95);
            var lastUpdated = Now;
            var now = Now.AddSeconds(30);

            var result = PlayerControl.ComputeCurrentPos(lastPos, TimeSpan.Zero, TimeSpan.Zero,
                playing: true, lastUpdated: lastUpdated, now: now, tlStampTicks: 0, nowTicks: 0, frequency: StopwatchFrequency);

            Assert.Equal(TimeSpan.FromSeconds(125), result);
        }
    }
}
