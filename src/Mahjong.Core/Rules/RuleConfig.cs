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

        /// <summary>允许杠：明杠 + 暗杠（默认开）。</summary>
        public bool AllowKong = true;

        /// <summary>加杠（补杠）v1 不做，默认关。</summary>
        public bool AllowAddedKong = false;

        /// <summary>七对子（推倒胡惯例默认关）。</summary>
        public bool AllowSevenPairs = false;

        /// <summary>流局后连庄（影响下局庄家计算，v1 固定轮庄，默认关）。</summary>
        public bool AllowRenchan = false;
    }
}
