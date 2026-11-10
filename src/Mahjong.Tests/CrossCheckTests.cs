using System;
using Mahjong.Core.Rules;
using Xunit;

namespace Mahjong.Tests
{
    /// <summary>
    ///对拍测试：测试项目里独立实现一个暴力参照（结构上不同于 WinChecker，
    ///用「枚举雀头 + 逐一尝试刻子/顺子」的完整逻辑），随机生成上万组牌型对拍，
    ///两算法结果必须完全一致——胡牌判定正确性的硬保证。
    /// </summary>
    public class CrossCheckTests
    {
        /// <summary>
        ///暴力参照实现（与 WinChecker 完全独立的第二实现）：
        ///先选雀头，再对剩余 3n 张牌递归尝试刻子和顺子。
        /// </summary>
        private static class BruteForce
        {
            public static bool CanWin(int[] counts)
            {
                int total = 0;
                for (int k = 0; k < 34; k++) total += counts[k];
                if (total % 3 != 2) return false;

                // 尝试每一种雀头选择
                for (int head = 0; head < 34; head++)
                {
                    if (counts[head] >= 2)
                    {
                        counts[head] -= 2;
                        bool ok = Solve(counts, 0);
                        counts[head] += 2;
                        if (ok) return true;
                    }
                }
                return false;
            }

            /// <summary>把剩余牌全部拆成刻子/顺子。</summary>
            private static bool Solve(int[] counts, int start)
            {
                int pos = -1;
                for (int k = start; k < 34; k++)
                {
                    if (counts[k] > 0) { pos = k; break; }
                }
                if (pos == -1) return true;

                // 尝试刻子
                if (counts[pos] >= 3)
                {
                    counts[pos] -= 3;
                    bool ok = Solve(counts, pos);
                    counts[pos] += 3;
                    if (ok) return true;
                }

                // 尝试顺子（仅数牌；pos%9<=6 保证 pos+1/pos+2 在同花色内）
                if (pos < 27 && (pos % 9) <= 6)
                {
                    if (counts[pos + 1] > 0 && counts[pos + 2] > 0)
                    {
                        counts[pos] -= 1;
                        counts[pos + 1] -= 1;
                        counts[pos + 2] -= 1;
                        bool ok = Solve(counts, pos);
                        counts[pos] += 1;
                        counts[pos + 1] += 1;
                        counts[pos + 2] += 1;
                        if (ok) return true;
                    }
                }
                return false;
            }
        }

        [Fact]
        public void 暴力参照_自身正确性_标准胡型()
        {
            int[] c = new int[34];
            c[0] = 1; c[1] = 1; c[2] = 1; // 123m
            c[3] = 2;                      // 44m 雀头
            c[9] = 3;                      // 111s
            c[18] = 1; c[19] = 1; c[20] = 1; // 123p
            c[27] = 3;                      // EEE
            Assert.True(BruteForce.CanWin(c));
        }

        [Fact]
        public void 暴力参照_自身正确性_标准不胡()
        {
            int[] c = new int[34];
            for (int k = 0; k < 34; k++) c[k] = 0;
            c[0] = 1; c[1] = 1; c[2] = 1;
            c[3] = 1;                       // 只有一张 4m，差雀头
            Assert.False(BruteForce.CanWin(c));
        }

        [Fact]
        public void 随机上万组对拍_结果完全一致()
        {
            // 结构不同于 WinChecker：这里生成随机 14 张（3n+2）计数并对拍
            var rng = new Random(2024);
            int agree = 0, disagree = 0;
            for (int round = 0; round < 10000; round++)
            {
                int[] counts = RandomCounts(rng, 14);
                bool mine = WinChecker.CanWinFull(counts, new RuleConfig());
                bool brute = BruteForce.CanWin(counts);
                if (mine == brute) agree++;
                else disagree++;
            }
            Assert.Equal(0, disagree);
        }

        [Fact]
        public void 随机上万组对拍_13张听牌场景()
        {
            // 生成 13 张并对拍（模 3 = 1，两算法都应返回 false）
            var rng = new Random(2025);
            int disagree = 0;
            for (int round = 0; round < 10000; round++)
            {
                int[] counts = RandomCounts(rng, 13);
                bool mine = WinChecker.CanWinFull(counts, new RuleConfig());
                bool brute = BruteForce.CanWin(counts);
                if (mine != brute) disagree++;
            }
            Assert.Equal(0, disagree);
        }

        private static int[] RandomCounts(Random rng, int total)
        {
            int[] counts = new int[34];
            int placed = 0;
            while (placed < total)
            {
                int k = rng.Next(34);
                if (counts[k] < 4)
                {
                    counts[k]++;
                    placed++;
                }
            }
            return counts;
        }
    }
}
