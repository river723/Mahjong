using System;

namespace Mahjong.Core.Rules
{
    /// <summary>胡牌牌型（金币场只用三档：普通 / 一条龙 / 七对）。</summary>
    public enum WinPatternKind
    {
        Normal = 0,

        /// <summary>一条龙：同花色 123 + 456 + 789 三组顺子。</summary>
        PureStraight = 1,

        /// <summary>七对（按一条龙计）。</summary>
        SevenPairs = 2
    }

    /// <summary>
    /// 胡牌那一刻的牌型信息：牌型档次 + 胡牌者已成立的明杠/暗杠数。
    /// 杠数只统计胡牌者（没胡的人杠了白杠），由 GameEngine 从副露里数好传入，
    /// 避免 Rules 层反向依赖 Game 层的 Meld。
    /// 明杠 +1/家、暗杠 +2/家，加成不吃自摸倍率，见 ScoreCalculator。
    /// </summary>
    public sealed class WinPattern
    {
        public WinPatternKind Kind { get; private set; }

        /// <summary>胡牌者已成立的明杠数（含将来的补杠）。</summary>
        public int OpenKongCount { get; private set; }

        /// <summary>胡牌者已成立的暗杠数。</summary>
        public int ConcealedKongCount { get; private set; }

        /// <summary>杠总数（兼容旧调用与 UI 摘要）。</summary>
        public int KongCount { get { return OpenKongCount + ConcealedKongCount; } }

        private WinPattern() { }

        /// <summary>兼容旧调用：只给一个杠数时，全部按明杠计。</summary>
        public static WinPattern Of(WinPatternKind kind, int kongCount)
        {
            return Of(kind, kongCount, 0);
        }

        public static WinPattern Of(WinPatternKind kind, int openKongs, int concealedKongs)
        {
            return new WinPattern
            {
                Kind = kind,
                OpenKongCount = Math.Max(0, openKongs),
                ConcealedKongCount = Math.Max(0, concealedKongs)
            };
        }

        /// <summary>普通胡（可带明杠）。</summary>
        public static WinPattern Plain(int kongCount = 0)
        {
            return Of(WinPatternKind.Normal, kongCount, 0);
        }

        /// <summary>
        /// 分析胡牌者暗牌（3n+2 张；点炮胡时调用方已把胡的那张补进计数数组）。
        /// 七对与一条龙同档基价，同时成立时优先记作七对；两者都不成立则为普通胡。
        /// </summary>
        public static WinPattern Analyze(int[] counts, int kongCount, RuleConfig rules)
        {
            return Analyze(counts, kongCount, 0, rules);
        }

        public static WinPattern Analyze(int[] counts, int openKongs, int concealedKongs, RuleConfig rules)
        {
            WinPatternKind kind = WinPatternKind.Normal;
            if (rules != null && rules.AllowSevenPairs && WinChecker.IsSevenPairs(counts))
                kind = WinPatternKind.SevenPairs;
            else if (WinChecker.HasPureStraight(counts))
                kind = WinPatternKind.PureStraight;
            return Of(kind, openKongs, concealedKongs);
        }

        /// <summary>本牌型的基价（七对可按开关降回普通基价）。</summary>
        public int BasePay(RuleConfig rules)
        {
            RuleConfig r = rules ?? new RuleConfig();
            if (Kind == WinPatternKind.Normal) return r.NormalBasePay;
            if (Kind == WinPatternKind.SevenPairs && !r.SevenPairsAsDragon) return r.NormalBasePay;
            return r.DragonBasePay;
        }

        /// <summary>所有杠合计让每家多付的金币。</summary>
        public int KongBonus(RuleConfig rules)
        {
            return (rules ?? new RuleConfig()).KongBonusOf(OpenKongCount, ConcealedKongCount);
        }

        public string DisplayName
        {
            get
            {
                switch (Kind)
                {
                    case WinPatternKind.PureStraight: return "一条龙";
                    case WinPatternKind.SevenPairs: return "七对";
                    default: return "普通胡";
                }
            }
        }

        /// <summary>杠的中文明细，例如「+ 1 明杠 + 2 暗杠」；无杠时为空串。</summary>
        public string KongDescription
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (OpenKongCount > 0) parts.Add(OpenKongCount + " 明杠");
                if (ConcealedKongCount > 0) parts.Add(ConcealedKongCount + " 暗杠");
                return parts.Count == 0 ? "" : " + " + string.Join(" + ", parts.ToArray());
            }
        }
    }
}
