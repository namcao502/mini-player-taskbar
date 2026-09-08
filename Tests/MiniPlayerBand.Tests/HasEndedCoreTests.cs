using System;
using MiniPlayerBand;
using Windows.Media.Control;
using Xunit;

namespace MiniPlayerBand.Tests
{
    public class HasEndedCoreTests
    {
        static readonly TimeSpan Start = TimeSpan.Zero;
        static readonly TimeSpan End = TimeSpan.FromSeconds(100);

        [Theory]
        [InlineData(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)]
        [InlineData(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)]
        public void HasEndedCore_ReturnsTrue_WhenStoppedOrClosedRegardlessOfPosition(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        {
            var result = PlayerControl.HasEndedCore(status, Start, End, TimeSpan.FromSeconds(5));

            Assert.True(result);
        }

        [Fact]
        public void HasEndedCore_ReturnsFalse_WhenDurationIsUnknown()
        {
            var result = PlayerControl.HasEndedCore(
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(5));

            Assert.False(result);
        }

        [Fact]
        public void HasEndedCore_ReturnsTrue_WhenPositionIsWithinOneAndHalfSecondsOfEnd()
        {
            var position = End - TimeSpan.FromSeconds(1);

            var result = PlayerControl.HasEndedCore(
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, Start, End, position);

            Assert.True(result);
        }

        [Fact]
        public void HasEndedCore_ReturnsFalse_WhenPausedMidTrack()
        {
            var position = TimeSpan.FromSeconds(50);

            var result = PlayerControl.HasEndedCore(
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, Start, End, position);

            Assert.False(result);
        }
    }
}
