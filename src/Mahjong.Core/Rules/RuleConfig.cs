using System;

namespace Mahjong.Core.Rules
{
    /// <summary>
    ///规则开关（推倒胡惯例默认值 + 预留扩展）。
    ///所有「能不能」的规则判定集中于此，便于将来扩展加杠/七对等。
    /// </summary>
    public sealed class RuleConfig
    {
        /// <summary>允许碰（默认开）。</summary>
        public bool AllowPong = true;

        /// <summary>允许明杠（对别人打出的牌杠，默认开）。</summary>
        public bool AllowKong = true;

        /// <summary>允许暗杠（自己回合内手里四张同款，默认开）。</summary>
        public bool AllowConcealedKong = true;

        /// <summary>允许补杠（把已有的一组碰升级成杠，按明杠档 +1/家，默认开）。</summary>
        public bool AllowAddedKong = true;

        /// <summary>允许抢杠胡（只有补杠亮出的牌可抢，暗杠不可抢，默认开）。</summary>
        public bool AllowRobbingKong = true;

        /// <summary>七对子（推倒胡惯例默认关）。</summary>
        public bool AllowSevenPairs = false;

        /// <summary>流局后连庄（影响下局庄家计算，v1 固定轮庄，默认关）。</summary>
        public bool AllowRenchan = false;

        // ==================== 金币场结算规则 ====================
        // 口径（已确认）：每家支付单价
        //   u = 牌型基价 × 自摸倍率 + 胡家明杠数 × 1 + 胡家暗杠数 × 2
        // 三家各付 u、胡家收 3u，每局零和，全场总额恒为 InitialCoins × 4。
        // 杠加成在倍率之后（不吃自摸倍率）；只有胡牌者的杠计入，没胡的人「白杠」。
        // 补杠（加杠）归入明杠档；补杠被抢胡时杠不成立，不计任何杠加成。

        /// <summary>开局每人金币数。</summary>
        public int InitialCoins = 20;

        /// <summary>普通胡牌的牌型基价。</summary>
        public int NormalBasePay = 1;

        /// <summary>一条龙的牌型基价（七对默认按此计）。</summary>
        public int DragonBasePay = 3;

        /// <summary>自摸倍率（作用于牌型基价，不作用于杠加成）。</summary>
        public int SelfDrawMultiplier = 2;

        /// <summary>胡牌者每个「明杠」让每家多付的金币。</summary>
        public int KongBonusPerPlayer = 1;

        /// <summary>胡牌者每个「暗杠」让每家多付的金币（暗杠比明杠难，加倍）。</summary>
        public int ConcealedKongBonusPerPlayer = 2;

        /// <summary>胡牌者所有杠合计让每家多付的金币（供结算面板拆解单价）。</summary>
        public int KongBonusOf(int openKongs, int concealedKongs)
        {
            return Math.Max(0, openKongs) * KongBonusPerPlayer
                 + Math.Max(0, concealedKongs) * ConcealedKongBonusPerPlayer;
        }

        /// <summary>七对按一条龙计（关则按普通胡计）。</summary>
        public bool SevenPairsAsDragon = true;

        /// <summary>结算后任一家金币 &lt;= 0 即结束整场。</summary>
        public bool EndOnNonPositiveCoin = true;

        /// <summary>金币场配置：在推倒胡默认值上打开七对（不改默认值，避免影响既有对局/测试）。</summary>
        public static RuleConfig CreateCoinMatch()
        {
            var rules = new RuleConfig();
            rules.AllowSevenPairs = true;
            return rules;
        }
    }
}
