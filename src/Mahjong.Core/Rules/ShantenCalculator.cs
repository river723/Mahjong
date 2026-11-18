using System;

namespace Mahjong.Core.Rules
{
    /// <summary>
    /// 向听数计算：还需要摸进/吃进几张有效牌才能听牌。
    /// -1 = 已经和牌，0 = 听牌，1 = 一向听，以此类推。
    ///
    /// 与 TenpaiDetector 的分工：TenpaiDetector 只答「现在听没听」，本类回答
    /// 「离听牌还有多远」——AI 没听牌时也必须知道该留什么，否则只能打手牌第一张。
    /// 副露视为已完成的面子传入 fixedM；暗牌计数仍只含没亮出来的牌，
    /// 这与 WinChecker 的口径一致（副露不用参与 3n+2 判定）。
    /// </summary>
    public static class ShantenCalculator
    {
        /// <summary>标准型 + （可选）七对子取较小值。</summary>
        public static int Calculate(int[] counts, int fixedM, bool allowSevenPairs)
        {
            if (counts == null || counts.Length != 34)
                throw new ArgumentException("计数数组长度必须为 34", "counts");
            if (fixedM < 0) fixedM = 0;
            if (fixedM > 4) fixedM = 4;

            int standard = CalculateStandard(counts, fixedM);
            if (!allowSevenPairs) return standard;
            return Math.Min(standard, CalculateSevenPairs(counts));
        }

        /// <summary>
        /// 标准型：shanten = 8 - max(2×面子 + 搭子 + 雀头)，副露已占掉固定面子数。
        /// 穷举雀头、面子（刻子/顺子）、搭子（对子/两面/嵌张）的所有组合取最大值。
        /// </summary>
        private static int CalculateStandard(int[] counts, int fixedM)
        {
            int[] work = (int[])counts.Clone();
            int best = 0;
            Search(work, 0, fixedM, 0, 0, 0, ref best);
            return 8 - 2 * fixedM - best;
        }

        private static void Search(int[] c, int pos, int fixedM, int sets, int partials, int pair, ref int best)
        {
            int score = 2 * sets + partials + pair;
            if (score > best) best = score;
            if (pos >= 34) return;

            int maxGroups = 4 - fixedM;
            int slots = maxGroups - sets - partials;
            if (slots < 0) return;

            // 乐观上界：剩下的空位每个最多当一个面子（+2），雀头若还没定再 +1。
            // 上界不高于已知最优时后面的分支不可能更好，直接剪掉。
            if (score + 2 * slots + (pair == 0 ? 1 : 0) <= best) return;

            if (c[pos] == 0)
            {
                Search(c, pos + 1, fixedM, sets, partials, pair, ref best);
                return;
            }

            // 这张牌不用作面子/搭子（孤张，留在手里等后续进张）。
            Search(c, pos + 1, fixedM, sets, partials, pair, ref best);

            // 雀头（最多一个）
            if (pair == 0 && c[pos] >= 2)
            {
                c[pos] -= 2;
                Search(c, pos, fixedM, sets, partials, 1, ref best);
                c[pos] += 2;
            }

            // 面子：刻子 / 顺子
            if (sets < maxGroups)
            {
                if (c[pos] >= 3)
                {
                    c[pos] -= 3;
                    Search(c, pos, fixedM, sets + 1, partials, pair, ref best);
                    c[pos] += 3;
                }
                int rank = pos % 9;
                if (pos < 27 && rank <= 6 && c[pos + 1] > 0 && c[pos + 2] > 0)
                {
                    c[pos]--; c[pos + 1]--; c[pos + 2]--;
                    Search(c, pos, fixedM, sets + 1, partials, pair, ref best);
                    c[pos]++; c[pos + 1]++; c[pos + 2]++;
                }
            }

            // 搭子：对子 / 两面（含边张） / 嵌张
            if (sets + partials < maxGroups)
            {
                if (c[pos] >= 2)
                {
                    c[pos] -= 2;
                    Search(c, pos, fixedM, sets, partials + 1, pair, ref best);
                    c[pos] += 2;
                }
                int rank = pos % 9;
                if (pos < 27)
                {
                    if (rank <= 8 && c[pos + 1] > 0)
                    {
                        c[pos]--; c[pos + 1]--;
                        Search(c, pos, fixedM, sets, partials + 1, pair, ref best);
                        c[pos]++; c[pos + 1]++;
                    }
                    if (rank <= 7 && c[pos + 2] > 0)
                    {
                        c[pos]--; c[pos + 2]--;
                        Search(c, pos, fixedM, sets, partials + 1, pair, ref best);
                        c[pos]++; c[pos + 2]++;
                    }
                }
            }
        }

        /// <summary>七对子向听：6 - 对子数 + 缺少的牌种数。</summary>
        private static int CalculateSevenPairs(int[] counts)
        {
            int pairs = 0, kinds = 0;
            for (int k = 0; k < 34; k++)
            {
                if (counts[k] > 0) kinds++;
                pairs += counts[k] / 2;
            }
            if (pairs >= 7) return -1;
            return 6 - pairs + Math.Max(0, 7 - kinds);
        }
    }
}
