using System;
using Mahjong.Core.Rules;
using Xunit;

namespace Mahjong.Tests
{
    /// <summary>
    ///WinChecker 单测：正例 + 反例全覆盖。每条用例都先验算张数 = 14（3n+2）。
    /// </summary>
    public class WinCheckerTests
    {
        private static readonly RuleConfig DefaultRules = new RuleConfig();

        private static int[] Parse(string s)
        {
            // 格式：每个元素为「数字+花色字母」（如 1m、9s）或字牌缩写（E东/S南/W西/N北/Z中/F发/B白）
            int[] counts = new int[34];
            int i = 0;
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == ' ') { i++; continue; }

                int idx;
                if (ch >= '0' && ch <= '9')
                {
                    if (i + 1 >= s.Length) throw new FormatException("缺少花色后缀");
                    char suitChar = s[i + 1];
                    int baseIdx;
                    if (suitChar == 'm') baseIdx = 0;
                    else if (suitChar == 's') baseIdx = 9;
                    else if (suitChar == 'p') baseIdx = 18;
                    else throw new FormatException("未知花色: " + suitChar);
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
                        case 'Z': idx = 31; break;  // 中
                        case 'F': idx = 32; break;  // 发
                        case 'B': idx = 33; break;  // 白
                        default: throw new FormatException("无法解析: " + ch);
                    }
                    i++;
                }
                if (idx < 0 || idx > 33) throw new FormatException("索引越界");
                if (counts[idx] >= 4) throw new FormatException("同种牌超过 4 张: " + ch);
                counts[idx]++;
            }
            return counts;
        }

        // ===== 正例（全部 14 张可胡）=====

        [Theory]
        [InlineData("1m2m3m 4m5m6m 7m8m9m 1s2s3s 4s4s")]   // 四顺一将
        [InlineData("1m1m1m 2s3s4s 5p6p7p 8s8s8s EE")]     // 刻顺混合
        [InlineData("2m3m4m 3m4m5m 6m7m8m 7m8m9m 1m1m")]   // 重叠顺子
        public void 标准胡型_可胡(string hand)
        {
            int[] c = Parse(hand);
            Assert.Equal(14, TotalOf(c));
            Assert.True(WinChecker.CanWinFull(c, DefaultRules), hand);
        }

        [Fact]
        public void 纯字牌_可胡()
        {
            int[] c = Parse("EEE SSS WWW NNN ZZ");
            Assert.Equal(14, TotalOf(c));
            Assert.True(WinChecker.CanWinFull(c, DefaultRules));
        }

        [Fact]
        public void 单骑_可胡()
        {
            // 111m 222m 333m 444m + 55m 将
            int[] c = Parse("1m1m1m 2m2m2m 3m3m3m 4m4m4m 5m5m");
            Assert.Equal(14, TotalOf(c));
            Assert.True(WinChecker.CanWinFull(c, DefaultRules));
        }

        [Fact]
        public void 边张_可胡()
        {
            // 12m 听 3m；摸到后 123m + 456s + 789p + EEE + WW
            int[] c = Parse("1m2m3m 4s5s6s 7p8p9p EEE WW");
            Assert.Equal(14, TotalOf(c));
            Assert.True(WinChecker.CanWinFull(c, DefaultRules));
        }

        [Fact]
        public void 对倒_可胡()
        {
            // 听 9s/9p 双碰；以摸到 9s 的 14 张验证
            int[] c = Parse("1m1m1m 4s4s4s 5p5p5p 9s9s9s 9p9p");
            Assert.Equal(14, TotalOf(c));
            Assert.True(WinChecker.CanWinFull(c, DefaultRules));
        }

        // ===== 反例 =====

        [Fact]
        public void 孤立牌_不可胡()
        {
            // 14 张全部互不搭
            int[] c = Parse("1m 4m 7m 2p 5p 8p 3s 6s 9s E N F B Z");
            Assert.Equal(14, TotalOf(c));
            Assert.False(WinChecker.CanWinFull(c, DefaultRules));
        }

        [Fact]
        public void 字牌不能组顺子()
        {
            // 若东+南+西能当顺子则此牌胡（ESW + 111m + 222m + 333m + 44m）；规则不允许 → 不胡
            int[] c = Parse("E S W 1m1m1m 2m2m2m 3m3m3m 4m4m");
            Assert.Equal(14, TotalOf(c));
            Assert.False(WinChecker.CanWinFull(c, DefaultRules));
        }

        [Fact]
        public void 七对子_默认关()
        {
            int[] c = Parse("1m1m 2s2s 3p3p 4m4m 5s5s 6p6p 7m7m");
            Assert.Equal(14, TotalOf(c));
            Assert.False(WinChecker.CanWinFull(c, DefaultRules));
        }

        [Fact]
        public void 七对子_开启时可胡()
        {
            var rules = new RuleConfig { AllowSevenPairs = true };
            int[] c = Parse("1m1m 2s2s 3p3p 4m4m 5s5s 6p6p 7m7m");
            Assert.True(WinChecker.CanWinFull(c, rules));
        }

        [Fact]
        public void 差一张_不可胡()
        {
            // 13 张（3n+1）直接否：Parse 串里应是 3 顺（9 张）+ 刻子（3 张）+ 单张 = 13 张
            int[] c = Parse("1m2m3m 4m5m6m 7m8m9m EEE");
            Assert.Equal(12, TotalOf(c));
            // 12 张 ≡ 0 (mod 3)，非 3n+2，应直接返回否
            Assert.False(WinChecker.CanWinFull(c, DefaultRules));

            // 补充一个 13 张用例（3n+1）
            int[] c13 = Parse("1m2m3m 4m5m6m 7m8m9m EEE S");
            Assert.Equal(13, TotalOf(c13));
            Assert.False(WinChecker.CanWinFull(c13, DefaultRules));
        }

        [Fact]
        public void 国士十三幺_不可胡()
        {
            // 十三幺标准型：13 种幺九各一 + 其中一张成对（14 张）
            int[] c = Parse("1m9m 1s9s 1p9p E S W N Z F 1m1m");
            Assert.Equal(14, TotalOf(c));
            Assert.False(WinChecker.CanWinFull(c, DefaultRules));
        }

        [Fact]
        public void 非法输入_抛异常()
        {
            Assert.Throws<ArgumentNullException>(() => WinChecker.CanWinFull(null, DefaultRules));
            int[] wrongLen = new int[33];
            Assert.Throws<ArgumentException>(() => WinChecker.CanWinFull(wrongLen, DefaultRules));
        }

        private static int TotalOf(int[] c)
        {
            int t = 0;
            for (int i = 0; i < 34; i++) t += c[i];
            return t;
        }
    }
}
