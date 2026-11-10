using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Core.Rules;
using Mahjong.Core.Tiles;

namespace Mahjong.Core.Game
{
    /// <summary>
    ///一局麻将的状态机：定庄 → 发 13/14 张 → 摸牌 → 出牌 → 声明结算 → 轮转 → 胡或流局。
    ///
    ///事件驱动：引擎发出 GameEvent 供 UI/AI 消费；4 个 IAgent（1 人 + 3 AI）由引擎 await 决策，
    ///逻辑与 UI 完全解耦。引擎在调用 Agent 前把可行动作算好（能胡必提示），Agent 只做「选」。
    /// </summary>
    public sealed class GameEngine
    {
        private readonly RuleConfig _rules;
        private readonly IAgent[] _agents;
        private readonly PlayerState[] _players;
        private readonly Wall _wall;
        private readonly List<Tile> _discards;
        private readonly ClaimResolver _claimResolver;
        private readonly ScoreCalculator _scoreCalculator;

        /// <summary>庄家索引（v1 固定轮庄：上局赢家或流局时下家）。</summary>
        private readonly int _dealerIndex;

        /// <summary>事件出口（UI 订阅）。</summary>
        public event Action<GameEvent> EventRaised;

        /// <summary>本局结果（RunToCompletion 后有效）。</summary>
        public GameResult Result { get; private set; }

        public GameEngine(RuleConfig rules, IAgent[] agents, int dealerIndex, int wallSeed)
        {
            _rules = rules;
            _agents = agents;
            _dealerIndex = dealerIndex;
            _wall = new Wall(wallSeed);
            _players = new PlayerState[4];
            for (int i = 0; i < 4; i++) _players[i] = new PlayerState(i);
            _discards = new List<Tile>();
            _claimResolver = new ClaimResolver(rules);
            _scoreCalculator = new ScoreCalculator(rules);
        }

        /// <summary>玩家状态（供测试/UI 查询）。</summary>
        public IList<PlayerState> Players { get { return Array.AsReadOnly(_players); } }

        /// <summary>牌河。</summary>
        public IList<Tile> Discards { get { return _discards; } }

        /// <summary>牌墙剩余。</summary>
        public int WallRemaining { get { return _wall.RemainingCount; } }

        /// <summary>运行完整一局直到胡牌或流局（异步，无 UI 线程依赖）。</summary>
        public async Task<GameResult> RunAsync()
        {
            // 发牌：庄家 14 张，其余 13 张
            for (int p = 0; p < 4; p++)
            {
                int count = (p == _dealerIndex) ? 14 : 13;
                for (int i = 0; i < count; i++)
                {
                    _players[p].ConcealedTiles.Add(_wall.Draw());
                }
            }
            Raise(GameEventType.GameStarted, _dealerIndex, null, "发牌完成");
            Raise(GameEventType.PlayerTurnStarted, _dealerIndex, null, "轮到庄家");

            // 主循环
            int current = _dealerIndex;
            bool firstTurn = true;
            while (true)
            {
                PlayerState player = _players[current];
                IAgent agent = _agents[current];
                bool afterDraw = !firstTurn || current != _dealerIndex || true; // 庄家首轮也已摸过（发牌即含）
                firstTurn = false;

                // —— 摸牌（庄家首轮例外：起手 14 张含摸）——
                Tile drawn = null;
                if (!(current == _dealerIndex && _turnCount == 0 && player.ConcealedTiles.Count == 14))
                {
                    if (_wall.RemainingCount == 0) { EndInDraw(); return Result; }
                    drawn = _wall.Draw();
                    player.ConcealedTiles.Add(drawn);
                    Raise(GameEventType.TileDrawn, current, drawn, null);
                }

                // —— 自摸判定（摸牌后）——
                if (CanSelfDraw(player))
                {
                    var winCtx = BuildContext(current, drawn);
                    Raise(GameEventType.ClaimOffered, current, drawn, "胡牌提示");
                    var act = await agent.ChooseConcealedAction(winCtx);
                    if (act != null && act.Type == ClaimType.Win)
                    {
                        EndInWin(current, drawn, isSelfDraw: true, loser: -1);
                        return Result;
                    }
                }

                // —— 出牌（14 → 13）——
                var ctx = BuildContext(current, drawn);
                Tile discard = await agent.ChooseDiscard(ctx);
                if (discard == null || !player.ConcealedTiles.Remove(discard))
                    throw new InvalidOperationException("Agent 返回了非法出牌: 玩家 " + current);
                _discards.Add(discard);
                Raise(GameEventType.TileDiscarded, current, discard, null);
                _turnCount++;

                // —— 声明征询（其余三家）——
                var resolution = await ResolveClaimsAsync(current, discard);
                if (resolution != null)
                {
                    if (resolution.Type == ClaimType.Win)
                    {
                        EndInWin(resolution.PlayerIndex, discard, isSelfDraw: false, loser: current);
                        return Result;
                    }
                    if (resolution.Type == ClaimType.Pong || resolution.Type == ClaimType.Kong)
                    {
                        current = await ApplyPongOrKong(resolution, discard);
                        continue; // 碰/杠者获得行动权（出牌或杠后补牌）
                    }
                }

                // —— 轮转下家 ——
                current = (current + 1) % 4;
            }
        }

        private int _turnCount;

        /// <summary>碰/杠结算：从声明者手里取出对应牌组成副露；杠补摸一张。</summary>
        private async Task<int> ApplyPongOrKong(ClaimResolution resolution, Tile discarded)
        {
            int p = resolution.PlayerIndex;
            PlayerState claimer = _players[p];
            int kind = (int)discarded.Kind;
            int tilesNeeded = resolution.Type == ClaimType.Kong ? 3 : 2;

            // 从手里取出 tilesNeeded 张同款
            var meldTiles = new List<Tile>();
            for (int i = 0; i < tilesNeeded; i++)
            {
                Tile t = claimer.ConcealedTiles.First(x => (int)x.Kind == kind);
                claimer.ConcealedTiles.Remove(t);
                meldTiles.Add(t);
            }
            meldTiles.Add(discarded);
            _discards.Remove(discarded); // 从牌河移回副露

            int next;
            if (resolution.Type == ClaimType.Kong)
            {
                claimer.Melds.Add(new Meld(ClaimType.Kong, meldTiles, resolution.FromDiscarder));
                // 杠后从墙尾摸替换牌
                if (_wall.RemainingCount == 0) { EndInDraw(); return -2; }
                Tile rep = _wall.DrawReplacement();
                claimer.ConcealedTiles.Add(rep);
                Raise(GameEventType.TileDrawn, p, rep, "杠后补牌");
                // 杠上开花判定
                if (CanSelfDraw(claimer))
                {
                    var ctx = BuildContext(p, rep);
                    var act = await _agents[p].ChooseConcealedAction(ctx);
                    if (act != null && act.Type == ClaimType.Win)
                    {
                        EndInWin(p, rep, isSelfDraw: true, loser: -1);
                        return -1; // 已终局
                    }
                }
                next = p; // 杠者继续出牌
            }
            else
            {
                claimer.Melds.Add(new Meld(ClaimType.Pong, meldTiles, resolution.FromDiscarder));
                next = p; // 碰者出牌
            }
            Raise(GameEventType.ClaimResolved, p, discarded,
                resolution.Type == ClaimType.Kong ? "杠" : "碰");
            return next;
        }

        /// <summary>向其余三家征询声明并结算。</summary>
        private async Task<ClaimResolution> ResolveClaimsAsync(int discarder, Tile discarded)
        {
            var claims = new List<ClaimOption>[4];
            bool any = false;
            for (int p = 0; p < 4; p++)
            {
                if (p == discarder) continue;
                int[] handCounts = HandCountsOf(_players[p]);
                var options = _claimResolver.GetOptions(_players[p], discarded, handCounts);
                if (options.Count > 0)
                {
                    var ctx = BuildContext(p, null, claimOptions: options, discarder: discarder);
                    Raise(GameEventType.ClaimOffered, p, discarded, "可碰/杠/胡");
                    var choice = await _agents[p].ChooseClaim(ctx);
                    claims[p] = choice != null && options.Any(o => SameOption(o, choice))
                        ? new List<ClaimOption> { choice }
                        : null;
                    if (claims[p] != null) any = true;
                }
            }
            if (!any) return null;
            var resolution = _claimResolver.Resolve(claims, discarder);
            return resolution;
        }

        private static bool SameOption(ClaimOption a, ClaimOption b)
        {
            return a.Type == b.Type && a.Kind == b.Kind;
        }

        private static int[] HandCountsOf(PlayerState player)
        {
            int[] counts = new int[34];
            foreach (var t in player.ConcealedTiles) counts[(int)t.Kind]++;
            return counts;
        }

        private bool CanSelfDraw(PlayerState player)
        {
            int[] counts = HandCountsOf(player);
            return WinChecker.CanWinFull(counts, _rules);
        }

        private DecisionContext BuildContext(int playerIndex, Tile drawn,
            IList<ClaimOption> claimOptions = null, int discarder = -1)
        {
            var melds = _players.Select(pl => (IList<Meld>)pl.Melds).ToArray();
            return DecisionContext.Create(
                _players[playerIndex].ConcealedTiles,
                _players[playerIndex].Melds,
                drawn,
                claimOptions ?? new List<ClaimOption>(),
                discarder,
                _discards,
                melds,
                _wall.RemainingCount);
        }

        private void EndInWin(int winner, Tile winningTile, bool isSelfDraw, int loser)
        {
            var scores = _scoreCalculator.ComputeScores(winner, isSelfDraw ? -1 : loser, isSelfDraw);
            Result = new GameResult
            {
                WinnerIndex = winner,
                LoserIndex = loser,
                IsSelfDraw = isSelfDraw,
                WinningTile = winningTile,
                Scores = scores
            };
            Raise(GameEventType.GameEnded, winner, winningTile,
                string.Format("{0} {1}「{2}」", isSelfDraw ? "自摸" : "胡", winningTile.Kind.GetName(),
                    DescribeScores(scores)));
        }

        private void EndInDraw()
        {
            Result = new GameResult
            {
                WinnerIndex = -1,
                LoserIndex = -1,
                Scores = new int[4]
            };
            Raise(GameEventType.GameEnded, -1, null, "流局");
        }

        private static string DescribeScores(int[] scores)
        {
            return string.Join(",", scores.Select(s => s.ToString()).ToArray());
        }

        private void Raise(GameEventType type, int playerIndex, Tile tile, string message)
        {
            var handler = EventRaised;
            if (handler != null) handler(GameEvent.Of(type, playerIndex, tile, message));
        }
    }
}
