using System;
using System.Collections.Generic;
using Mahjong.Core.Rules;
using Mahjong.Core.Tiles;

namespace Mahjong.Core.Game
{
    /// <summary>游戏事件类型（引擎 → UI 的单向广播）。</summary>
    public enum GameEventType
    {
        /// <summary>一局开始（发完牌）。</summary>
        GameStarted,

        /// <summary>某玩家摸牌。</summary>
        TileDrawn,

        /// <summary>某玩家出牌进牌河。</summary>
        TileDiscarded,

        /// <summary>有人可声明（碰/杠/胡，含补杠亮牌后的抢杠胡），向各 Agent 征询。</summary>
        ClaimOffered,

        /// <summary>自己摸牌后成胡（自摸/杠上开花/天胡），向该 Agent 征询是否胡。</summary>
        ConcealedActionOffered,

        /// <summary>声明结算（碰/杠/胡成立或全部放弃）。</summary>
        ClaimResolved,

        /// <summary>轮到某玩家行动（摸牌后出牌阶段）。</summary>
        PlayerTurnStarted,

        /// <summary>一局结束（胡或流局）。</summary>
        GameEnded
    }

    /// <summary>声明类型：吃被禁（推倒胡），故无 Chow。</summary>
    public enum ClaimType
    {
        None = 0,

        /// <summary>碰。</summary>
        Pong,

        /// <summary>杠（明杠）。</summary>
        Kong,

        /// <summary>胡（点炮、抢杠胡；杠上开花走自摸）。</summary>
        Win,

        /// <summary>暗杠（自己回合内发起，手里四张同款；每杠 +2/家；不可被抢）。</summary>
        ConcealedKong,

        /// <summary>补杠 / 加杠（把已有的一组碰升级成杠；每杠 +1/家；亮牌时可被抢杠胡）。</summary>
        AddedKong
    }

    /// <summary>游戏事件（不可变数据载体）。</summary>
    public sealed class GameEvent
    {
        public GameEventType Type { get; private set; }

        /// <summary>事件涉及的玩家索引（0-3；0 = 人类坐南即屏幕下方）。</summary>
        public int PlayerIndex { get; private set; }

        /// <summary>事件涉及的牌（摸/出/声明对象；可空）。</summary>
        public Tile Tile { get; private set; }

        /// <summary>附加文本（结果描述等）。</summary>
        public string Message { get; private set; }

        /// <summary>声明征询时，被征询玩家可做的碰/杠/胡选项（仅 ClaimOffered 事件携带，其余为 null）。</summary>
        public IList<ClaimOption> ClaimOptions { get; private set; }

        private GameEvent() { }

        public static GameEvent Of(GameEventType type, int playerIndex, Tile tile, string message)
        {
            return new GameEvent { Type = type, PlayerIndex = playerIndex, Tile = tile, Message = message };
        }

        public static GameEvent Of(GameEventType type, int playerIndex, Tile tile, string message, IList<ClaimOption> claimOptions)
        {
            return new GameEvent { Type = type, PlayerIndex = playerIndex, Tile = tile, Message = message, ClaimOptions = claimOptions };
        }
    }

    /// <summary>一局结果。</summary>
    public sealed class GameResult
    {
        /// <summary>胡牌者（-1 = 流局）。</summary>
        public int WinnerIndex { get; set; }

        /// <summary>点炮者（-1 = 自摸或流局）。</summary>
        public int LoserIndex { get; set; }

        /// <summary>是否自摸。</summary>
        public bool IsSelfDraw { get; set; }

        /// <summary>是否抢杠胡（胡的是别人补杠时亮出的那张牌；该杠不成立）。</summary>
        public bool IsRobbedKong { get; set; }

        /// <summary>胡的那张牌。</summary>
        public Tile WinningTile { get; set; }

        /// <summary>各家本局得分（有正有负，总和必为 0；金币场即金币变动量）。</summary>
        public int[] Scores { get; set; }

        /// <summary>本局每家应付的金币数（三家各付 UnitPay，胡家收 3 × UnitPay）。</summary>
        public int UnitPay { get; set; }

        /// <summary>胡牌牌型档次（流局时为 Normal）。</summary>
        public WinPatternKind PatternKind { get; set; }

        /// <summary>胡牌牌型中文名，供结算面板展示。</summary>
        public string PatternName
        {
            get
            {
                switch (PatternKind)
                {
                    case WinPatternKind.PureStraight: return "一条龙";
                    case WinPatternKind.SevenPairs: return "七对";
                    default: return "普通胡";
                }
            }
        }

        /// <summary>胡牌者已成立的明杠数（未胡牌者的杠不计）。</summary>
        public int OpenKongCount { get; set; }

        /// <summary>胡牌者已成立的暗杠数（未胡牌者的杠不计）。</summary>
        public int ConcealedKongCount { get; set; }

        /// <summary>胡牌者已成立的杠总数（明 + 暗）。</summary>
        public int KongCount { get { return OpenKongCount + ConcealedKongCount; } }

        /// <summary>杠的中文明细，例如「 + 1 明杠 + 2 暗杠」；无杠时为空串。</summary>
        public string KongDescription
        {
            get
            {
                var parts = new List<string>();
                if (OpenKongCount > 0) parts.Add(OpenKongCount + " 明杠");
                if (ConcealedKongCount > 0) parts.Add(ConcealedKongCount + " 暗杠");
                return parts.Count == 0 ? "" : " + " + string.Join(" + ", parts.ToArray());
            }
        }

        /// <summary>是否流局。</summary>
        public bool IsDraw
        {
            get { return WinnerIndex < 0; }
        }

        /// <summary>结束时各家的听牌状态（流局时听牌者免赔，v1 简化：仅展示用）。</summary>
        public bool[] TenpaiFlags { get; set; }
    }

    /// <summary>
    ///玩家的公开信息（对手可见）+ 自己的暗牌信息。
    ///引擎持有 4 个 PlayerState；AI 与 UI 从事件流构建自己的视图，不直接引用本类（防作弊）。
    /// </summary>
    public sealed class PlayerState
    {
        public int Index { get; private set; }

        /// <summary>暗牌（手牌）。仅自己可见；UI 展示对手时只用数量。</summary>
        public List<Tile> ConcealedTiles { get; private set; }

        /// <summary>副露组（碰/杠的明牌组）。</summary>
        public List<Meld> Melds { get; private set; }

        /// <summary>本局累计分。</summary>
        public int Score { get; set; }

        public PlayerState(int index)
        {
            Index = index;
            ConcealedTiles = new List<Tile>();
            Melds = new List<Meld>();
        }
    }

    /// <summary>一组副露（碰或杠）。</summary>
    public sealed class Meld
    {
        public ClaimType Type { get; private set; }

        /// <summary>组成副露的牌（碰 3 张、杠 4 张；明杠含他人打出的那张）。</summary>
        public List<Tile> Tiles { get; private set; }

        /// <summary>从谁那里声明来的（-1 = 暗杠）。</summary>
        public int FromPlayer { get; private set; }

        public Meld(ClaimType type, List<Tile> tiles, int fromPlayer)
        {
            Type = type;
            Tiles = tiles;
            FromPlayer = fromPlayer;
        }
    }
}
