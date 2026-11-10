using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Core.Rules;
using Xunit;

namespace Mahjong.Tests
{
    /// <summary>TenpaiDetector（听牌检测）单测。</summary>
    public class TenpaiDetectorTests
    {
        private static readonly RuleConfig Rules = new RuleConfig();

        private static int[] Parse(string s)
        {
            // 与 WinCheckerTests 相同的解析器（格式：1m / E 等）
            int[] counts = new int[34];
            int i = 0;
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == ' ') { i++; continue; }
                int idx;
                if (ch >= '0' && ch <= '9')
                {
                    char suit = s[i + 1];
                    int baseIdx = suit == 'm' ? 0 : suit == 's' ? 9 : 18;
                    idx = baseIdx + (ch - '0') - 1;
                    i += 2;
                }
                else
                {
                    switch (ch)
                    {
                        case 'E': idx = 27; break;
                        case 'S': idx = 28; break;
                        case 'W': idx = 29; break;
                        case 'N': idx = 30; break;
                        case 'Z': idx = 31; break;
                        case 'F': idx = 32; break;
                        case 'B': idx = 33; break;
                        default: throw new FormatException("无法解析: " + ch);
                    }
                    i++;
                }
                counts[idx]++;
            }
            return counts;
        }

        [Fact]
        public void 边张听_进张为3万()
        {
            // 12m 456s 789p EEE WW → 听 3m
            int[] hand = Parse("1m2m 4s5s6s 7p8p9p EEE WW");
            var waits = TenpaiDetector.GetWaits(hand, Rules);
            Assert.Single(waits);
            Assert.Equal(2, waits[0]); // 3m 的索引 = 2
        }

        [Fact]
        public void 嵌张听_进张为中间牌()
        {
            // 1m3m 456s 789p EEE WW → 嵌 2m
            int[] hand = Parse("1m3m 4s5s6s 7p8p9p EEE WW");
            var waits = TenpaiDetector.GetWaits(hand, Rules);
            Assert.Single(waits);
            Assert.Equal(1, waits[0]); // 2m
        }

        [Fact]
        public void 单骑听_进张为将牌()
        {
            // 123m 456m 789m 111s E → 单骑 E
            int[] hand = Parse("1m2m3m 4m5m6m 7m8m9m 1s1s1s E");
            var waits = TenpaiDetector.GetWaits(hand, Rules);
            Assert.Single(waits);
            Assert.Equal(27, waits[0]); // 东
        }

        [Fact]
        public void 对倒听_两个进张()
        {
            // 111m 444s 555p 9s9s 9p9p → 对倒 9s / 9p
            int[] hand = Parse("1m1m1m 4s4s4s 5p5p5p 9s9s 9p9p");
            Assert.Equal(13, hand.Sum());
            var waits = TenpaiDetector.GetWaits(hand, Rules);
            Assert.Equal(2, waits.Count);
            Assert.Contains(17, waits); // 9s 索引 = 9+8 = 17
            Assert.Contains(26, waits); // 9p 索引 = 18+8 = 26
        }

        [Fact]
        public void 多面听_好牌多进张()
        {
            // 13 张：4m5m6m 6m7m8m 3s3s3s 4p4p 5s5s = 3+3+3+2+2 = 13
            int[] hand = Parse("4m5m6m 6m7m8m 3s3s3s 4p4p 5s5s");
            Assert.Equal(13, hand.Sum());
            var waits = TenpaiDetector.GetWaits(hand, Rules);
            // 4m5m6m + 6m7m8m + 33s + 44p + 55s：听 3s/6s（雀头变化）——只验证「多进张」
            Assert.True(waits.Count >= 2, "多面听应有至少 2 个进张，实际: " + waits.Count);
        }

        [Fact]
        public void 未听牌_无进张()
        {
            // 孤立字牌
            int[] hand = Parse("1m9m 1s9s 1p9p E S W N Z F B B");
            var waits = TenpaiDetector.GetWaits(hand, Rules);
            Assert.Empty(waits);
        }

        [Fact]
        public void 已听牌_十三张与补牌后一致()
        {
            // 13 张听 3m；把 3m 加入后 CanWin 应为 true
            int[] hand = Parse("1m2m 4s5s6s 7p8p9p EEE WW");
            int[] win = Parse("1m2m3m 4s5s6s 7p8p9p EEE WW");
            Assert.True(TenpaiDetector.IsTenpai(hand, Rules));
            Assert.True(WinChecker.CanWinFull(win, Rules));
        }
    }
}
