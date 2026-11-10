using System.Collections.Generic;
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

        /// <summary>有人可声明（碰/杠/胡），向各 Agent 征询。</summary>
        ClaimOffered,

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

        /// <summary>胡（点炮或抢杠，v1 无抢杠）。</summary>
        Win,

        /// <summary>暗杠（自己回合内发起，非声明）。</summary>
        ConcealedKong
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

        private GameEvent() { }

        public static GameEvent Of(GameEventType type, int playerIndex, Tile tile, string message)
        {
            return new GameEvent { Type = type, PlayerIndex = playerIndex, Tile = tile, Message = message };
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

        /// <summary>胡的那张牌。</summary>
        public Tile WinningTile { get; set; }

        /// <summary>各家本局得分（有正有负，总和必为 0）。</summary>
        public int[] Scores { get; set; }

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
