using System.Collections.Generic;
using System.Threading.Tasks;
using Mahjong.Core.Tiles;

namespace Mahjong.Core.Game
{
    /// <summary>某玩家可做的声明（引擎征询后的答复）。</summary>
    public sealed class ClaimOption
    {
        public ClaimType Type { get; private set; }

        /// <summary>声明用的牌（碰/杠目标）。</summary>
        public TileKind Kind { get; private set; }

        private ClaimOption() { }

        public static ClaimOption Of(ClaimType type, TileKind kind)
        {
            return new ClaimOption { Type = type, Kind = kind };
        }
    }

    /// <summary>决策上下文：引擎把「当前局面」交给 Agent 时只给该 Agent 应得的信息。</summary>
    public sealed class DecisionContext
    {
        /// <summary>行动玩家的暗牌（刚摸牌后为 14 张；未摸为 13 张——仅声明结算后轮转前会出现）。</summary>
        public IList<Tile> Hand { get; private set; }

        /// <summary>自己已副露的组。</summary>
        public IList<Meld> Melds { get; private set; }

        /// <summary>当前摸到的牌（无则未处于摸牌后阶段）。</summary>
        public Tile DrawnTile { get; private set; }

        /// <summary>当前可声明选项（他打牌时可碰/杠/胡）。</summary>
        public IList<ClaimOption> ClaimOptions { get; private set; }

        /// <summary>谁打出的这张牌（声明场景）。</summary>
        public int Discarder { get; private set; }

        /// <summary>牌河全部牌（公开信息，AI 记牌器用）。</summary>
        public IList<Tile> Discards { get; private set; }

        /// <summary>各家副露（公开信息）。</summary>
        public IList<Meld>[] AllMelds { get; private set; }

        /// <summary>牌墙剩余张数（公开信息）。</summary>
        public int WallRemaining { get; private set; }

        private DecisionContext() { }

        public static DecisionContext Create(
            IList<Tile> hand, IList<Meld> melds, Tile drawnTile,
            IList<ClaimOption> claimOptions, int discarder,
            IList<Tile> discards, IList<Meld>[] allMelds, int wallRemaining)
        {
            return new DecisionContext
            {
                Hand = hand,
                Melds = melds,
                DrawnTile = drawnTile,
                ClaimOptions = claimOptions,
                Discarder = DiscarderValue(discarder),
                Discards = discards,
                AllMelds = allMelds,
                WallRemaining = wallRemaining
            };
        }

        private static int DiscarderValue(int d) { return d; }
    }

    /// <summary>
    ///决策代理接口：引擎 await 各玩家的决策（1 个人类 + 3 个 AI）。
    ///HumanAgent 用 TaskCompletionSource 把按钮点击接到引擎；AiPlayer 同步计算。
    /// </summary>
    public interface IAgent
    {
        /// <summary>摸牌后选择打出哪张（14 张里挑 1 张打出，剩下的 13 张留手）。</summary>
        Task<Tile> ChooseDiscard(DecisionContext context);

        /// <summary>他打牌后决定是否碰/杠/胡（返回 null 表示放弃）。</summary>
        Task<ClaimOption> ChooseClaim(DecisionContext context);

        /// <summary>自己回合内决定暗杠或宣布胡牌（返回 null 表示都不做，继续出牌流程）。
        ///v1 推倒胡：能胡时引擎直接判，无需询问此接口。</summary>
        Task<ClaimOption> ChooseConcealedAction(DecisionContext context);
    }
}
