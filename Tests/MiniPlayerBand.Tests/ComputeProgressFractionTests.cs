using System;
using MiniPlayerBand;
using Xunit;

namespace MiniPlayerBand.Tests
{
    public class ComputeProgressFractionTests
    {
        [Fact]
        public void ComputeProgressFraction_ReturnsNegativeOne_WhenDurationIsUnknown()
        {
            var result = PlayerControl.ComputeProgressFraction(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

            Assert.Equal(-1, result);
        }

        [Fact]
        public void ComputeProgressFraction_ReturnsHalf_WhenPositionIsMidTrack()
        {
            var start = TimeSpan.Zero;
            var end = TimeSpan.FromSeconds(100);
            var pos = TimeSpan.FromSeconds(50);

            var result = PlayerControl.ComputeProgressFraction(start, end, pos);

            Assert.Equal(0.5, result, 3);
        }

        [Fact]
        public void ComputeProgressFraction_ReturnsZero_WhenPositionIsAtStart()
        {
            var start = TimeSpan.Zero;
            var end = TimeSpan.FromSeconds(100);

            var result = PlayerControl.ComputeProgressFraction(start, end, start);

            Assert.Equal(0, result, 3);
        }

        [Fact]
        public void ComputeProgressFraction_ReturnsOne_WhenPositionIsAtEnd()
        {
            var start = TimeSpan.Zero;
            var end = TimeSpan.FromSeconds(100);

            var result = PlayerControl.ComputeProgressFraction(start, end, end);

            Assert.Equal(1, result, 3);
        }
    }
}
