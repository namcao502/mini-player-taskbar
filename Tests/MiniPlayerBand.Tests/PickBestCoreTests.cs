using System.Collections.Generic;
using MiniPlayerBand;
using Xunit;

namespace MiniPlayerBand.Tests
{
    public class PickBestCoreTests
    {
        [Fact]
        public void PickBestCore_ReturnsCurrent_WhenCurrentIsPlaying()
        {
            var items = new List<string> { "a", "b" };

            var result = PlayerControl.PickBestCore("current", items, s => s == "current" || s == "a");

            Assert.Equal("current", result);
        }

        [Fact]
        public void PickBestCore_ReturnsPlayingItem_WhenCurrentIsNotPlaying()
        {
            var items = new List<string> { "a", "b" };

            var result = PlayerControl.PickBestCore("current", items, s => s == "b");

            Assert.Equal("b", result);
        }

        [Fact]
        public void PickBestCore_FallsBackToCurrent_WhenNothingIsPlaying()
        {
            var items = new List<string> { "a", "b" };

            var result = PlayerControl.PickBestCore("current", items, s => false);

            Assert.Equal("current", result);
        }

        [Fact]
        public void PickBestCore_ReturnsNull_WhenCurrentIsNullAndNothingIsPlaying()
        {
            var items = new List<string> { "a", "b" };

            var result = PlayerControl.PickBestCore(null, items, s => false);

            Assert.Null(result);
        }

        [Fact]
        public void PickBestCore_ReturnsCurrent_WhenItemsListIsNull()
        {
            var result = PlayerControl.PickBestCore("current", null, s => false);

            Assert.Equal("current", result);
        }
    }
}
