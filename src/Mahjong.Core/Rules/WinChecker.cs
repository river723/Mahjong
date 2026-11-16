using System;
using System.Collections.Generic;

namespace Mahjong.Core.Rules
{
    /// <summary>
    ///胡牌判定（全项目正确性核心）。
    ///
    ///关键点：推倒胡「不可吃」禁的只是「吃」这个向别人要牌的声明动作（ClaimResolver
    ///没有 Chow 选项），但胡牌牌型本身包含顺子（3-4-5 万是胡的），WinChecker 必须
    ///完整支持顺子。
    ///
    ///算法：选雀头 + 回溯拆分（14 张规模瞬时完成，无需 DP）。
    ///内部一律用 int[34] 计数数组，不操作 Tile 对象。
    /// </summary>
    public static class WinChecker
    {
        /// <summary>兼容入口：按默认规则做完整判定（旧版此处是 return true 的占位实现）。</summary>
        public static bool CanWin(int[] counts)
        {
            return CanWinFull(counts, new RuleConfig());
        }

        /// <summary>真实入口：完整判定。</summary>
        public static bool CanWinFull(int[] counts, RuleConfig rules)
        {
            ValidateCounts(counts, "CanWinFull");
            int total = TotalOf(counts);
            if (total % 3 != 2) return false;

            // 七对（若开启）：所有非零计数均为偶数且恰好 7 对
            if (rules != null && rules.AllowSevenPairs && IsSevenPairs(counts)) return true;

            // 标准型：枚举雀头
            for (int k = 0; k < 34; k++)
            {
                if (counts[k] < 2) continue;
                counts[k] -= 2;
                bool ok = SplitAll(counts, 0);
                counts[k] += 2;
                if (ok) return true;
            }
            return false;
        }

        /// <summary>
        /// 回溯拆分：从 pos 起把剩余牌拆成刻子/顺子。
        /// 字牌（27-33）只能刻子；数牌刻子与顺子都试。
        /// </summary>
        private static bool SplitAll(int[] counts, int pos)
        {
            while (pos < 34 && counts[pos] == 0) pos++;
            if (pos == 34) return true;

            if (pos >= 27)
            {
                // 字牌只能刻子
                if (counts[pos] < 3) return false;
                counts[pos] -= 3;
                bool ok = SplitAll(counts, pos);
                counts[pos] += 3;
                return ok;
            }

            // 数牌：先试刻子
            if (counts[pos] >= 3)
            {
                counts[pos] -= 3;
                bool ok = SplitAll(counts, pos);
                counts[pos] += 3;
                if (ok) return true;
            }

            // 再试顺子（pos+2 不越界由 pos % 9 <= 6 保证；8、9 不能做顺子头）
            int rank = pos % 9;
            if (rank <= 6 && counts[pos + 1] > 0 && counts[pos + 2] > 0)
            {
                counts[pos]--; counts[pos + 1]--; counts[pos + 2]--;
                bool ok = SplitAll(counts, pos);
                counts[pos]++; counts[pos + 1]++; counts[pos + 2]++;
                if (ok) return true;
            }
            return false;
        }

        /// <summary>是否为七对型（14 张、每种计数均为偶数、恰好 7 对）。</summary>
        public static bool IsSevenPairs(int[] counts)
        {
            ValidateCounts(counts, "IsSevenPairs");
            if (TotalOf(counts) != 14) return false;
            int pairs = 0;
            for (int k = 0; k < 34; k++)
            {
                if ((counts[k] & 1) != 0) return false;
                pairs += counts[k] / 2;
            }
            return pairs == 7;
        }

        /// <summary>
        /// 是否含「一条龙」：同一数牌花色里同时拆出 123、456、789 三个顺子，
        /// 其余面子与雀头不限花色。项目禁吃，顺子只可能来自暗牌，故只看暗牌计数即可。
        /// 必须枚举所有拆分方案（同一手牌可能有多种拆法），任一拆法成立即算。
        /// </summary>
        public static bool HasPureStraight(int[] counts)
        {
            ValidateCounts(counts, "HasPureStraight");
            if (TotalOf(counts) % 3 != 2) return false;

            bool found = false;
            for (int pair = 0; pair < 34 && !found; pair++)
            {
                if (counts[pair] < 2) continue;
                counts[pair] -= 2;
                var chows = new List<int>();
                SplitCollect(counts, 0, chows, ref found);
                counts[pair] += 2;
            }
            return found;
        }

        /// <summary>回溯枚举所有面子拆分，把顺子起点记入 chows；命中一条龙时提前结束。</summary>
        private static void SplitCollect(int[] counts, int pos, List<int> chows, ref bool found)
        {
            if (found) return;
            while (pos < 34 && counts[pos] == 0) pos++;
            if (pos == 34)
            {
                if (ContainsDragon(chows)) found = true;
                return;
            }

            if (pos >= 27)
            {
                // 字牌只能刻子
                if (counts[pos] < 3) return;
                counts[pos] -= 3;
                SplitCollect(counts, pos, chows, ref found);
                counts[pos] += 3;
                return;
            }

            // 先试刻子
            if (counts[pos] >= 3)
            {
                counts[pos] -= 3;
                SplitCollect(counts, pos, chows, ref found);
                counts[pos] += 3;
                if (found) return;
            }

            // 再试顺子，起点入列
            int rank = pos % 9;
            if (rank <= 6 && counts[pos + 1] > 0 && counts[pos + 2] > 0)
            {
                counts[pos]--; counts[pos + 1]--; counts[pos + 2]--;
                chows.Add(pos);
                SplitCollect(counts, pos, chows, ref found);
                chows.RemoveAt(chows.Count - 1);
                counts[pos]++; counts[pos + 1]++; counts[pos + 2]++;
            }
        }

        /// <summary>顺子起点集合里是否同时存在某花色的 1/4/7 万（或条、筒）起步。</summary>
        private static bool ContainsDragon(List<int> chows)
        {
            int[] bases = { 0, 9, 18 };
            foreach (int b in bases)
            {
                if (chows.Contains(b) && chows.Contains(b + 3) && chows.Contains(b + 6))
                    return true;
            }
            return false;
        }

        private static int TotalOf(int[] counts)
        {
            int total = 0;
            for (int k = 0; k < 34; k++) total += counts[k];
            return total;
        }

        private static void ValidateCounts(int[] counts, string method)
        {
            if (counts == null) throw new ArgumentNullException("counts");
            if (counts.Length != 34)
                throw new ArgumentException("计数数组长度必须为 34", "counts");
        }
    }
}
