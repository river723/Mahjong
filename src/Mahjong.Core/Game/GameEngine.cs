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

        /// <summary>庄家索引（由上层传入；UI 场景当前固定 0，本类不自动轮换）。</summary>
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

        /// <summary>
        /// 每轮结算完成后、下一家摸牌前的节奏停顿毫秒数（UI 场景用，给播报留时间）。
        /// 默认 0（测试场景不希望有延迟）。原先放在「出牌与声明征询之间」，会让
        /// 可碰/杠/胡的按钮滞后一整句配音才出现，现已移到声明结算之后（PaceAsync）。
        /// </summary>
        public int ThinkDelayMs { get; set; } = 0;

        /// <summary>
        /// 真人出牌后的停顿毫秒数（未设置时跟随 <see cref="ThinkDelayMs"/>）。
        /// 真人自己出完牌不需要等自己听配音，可以比 AI 短一些，避免操作发滞。
        /// </summary>
        public int HumanThinkDelayMs { get; set; } = -1;

        /// <summary>运行完整一局直到胡牌或流局（异步，无 UI 线程依赖）。</summary>
        public async Task<GameResult> RunAsync()
        {
            // 发牌/抓牌：按逆时针座次从庄家起依次发（庄家 14 张，其余 13 张）。
            // 真实麻将抓牌是每家每次 2 墩、轮 3 圈 + 庄家跳牌；本项目简化为一次性
            // 连续发够，但「谁先拿、谁后拿」的家次必须与行牌方向一致，不能再按索引 0→3 跳着发。
            int dealSeat = _dealerIndex;
            for (int k = 0; k < 4; k++)
            {
                int count = (dealSeat == _dealerIndex) ? 14 : 13;
                for (int i = 0; i < count; i++)
                {
                    _players[dealSeat].ConcealedTiles.Add(_wall.Draw());
                }
                dealSeat = SeatOrder.NextSeat(dealSeat);
            }
            Raise(GameEventType.GameStarted, _dealerIndex, null, "发牌完成");

            // 主循环
            int current = _dealerIndex;
            // 碰/杠之后本轮不再摸牌（碰后直接出牌、杠的补牌由 ApplyPongOrKong 内部处理）。
            // 若这里重复摸牌，手牌会变成 3n 张而非胡牌要求的 3n+2，导致碰过牌就再也判不出胡。
            bool skipDraw = false;
            while (true)
            {
                PlayerState player = _players[current];
                IAgent agent = _agents[current];
                // 每轮开始只广播一次：UI 据此刷新中央「轮到谁」与回合提示。
                // 开局时本循环紧接着就执行，因此 GameStarted 之后不能再单独补播一次。
                Raise(GameEventType.PlayerTurnStarted, current, null, current == 0 ? "轮到你出牌" : "等待各家出牌");

                // —— 摸牌（庄家首轮、碰/杠后本轮例外）——
                Tile drawn = null;
                bool dealerOpening = (current == _dealerIndex && _turnCount == 0 && player.ConcealedTiles.Count == 14);
                if (!dealerOpening && !skipDraw)
                {
                    if (_wall.RemainingCount == 0) { EndInDraw(); return Result; }
                    drawn = _wall.Draw();
                    player.ConcealedTiles.Add(drawn);
                    Raise(GameEventType.TileDrawn, current, drawn, null);
                }
                bool justDrew = (drawn != null) || dealerOpening;
                skipDraw = false;

                // —— 自摸 / 天胡 / 暗杠（仅在本轮真正摸了牌时）——
                // 这三种动作都在自己回合内发起，共用 concealed 决策通道；暗杠会补一张牌，
                // 所以循环里重新收集选项（支持连续暗杠与暗杠后的杠上开花）。
                if (justDrew)
                {
                    var step = await ResolveConcealedAsync(current, drawn, "自摸胡牌");
                    if (step.Ended) return Result;
                    drawn = step.LastDrawn;
                }

                // —— 出牌（14 → 13）——
                var ctx = BuildContext(current, drawn);
                Tile chosen = await agent.ChooseDiscard(ctx);
                if (chosen == null)
                    throw new InvalidOperationException("Agent 返回了非法出牌(null): 玩家 " + current);

                // 优先按实例引用移除；若 UI 传来的是「同款但不同实例」的等价牌
                // （例如手牌同步发生漂移），则按牌面 Kind 回退移除一张同款。
                // 这样点哪张都能打出去，引擎状态机不会因非法出牌异常而整局终止。
                Tile discard = chosen;
                if (!player.ConcealedTiles.Remove(chosen))
                {
                    discard = player.ConcealedTiles.FirstOrDefault(t => t.Kind == chosen.Kind);
                    if (discard == null)
                        throw new InvalidOperationException("Agent 返回了非法出牌: 玩家 " + current);
                    player.ConcealedTiles.Remove(discard);
                }
                _discards.Add(discard);
                Raise(GameEventType.TileDiscarded, current, discard, null);
                _turnCount++;

                // —— 声明征询（其余三家）——
                // 出牌与征询之间不再停顿：牌一落牌河，可碰/杠/胡的按钮就该出现。
                // 给播报留的节奏停顿挪到「本轮结算之后、下一家摸牌之前」（PaceAsync），
                // 否则玩家会看到牌已经落下，按钮却要等 1.3 秒才弹出来。
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
                        int next = await ApplyPongOrKong(resolution, discard);
                        // -1 = 杠上开花已胡、-2 = 杠补牌时流局，Result 均已结算完毕
                        if (next < 0) return Result;
                        // ClaimResolved / 杠后补牌事件此时已播完，再停顿让播报跟上，
                        // 然后由碰/杠者本人出牌（本轮不再摸牌）。
                        await PaceAsync(current);
                        current = next;
                        skipDraw = true;
                        continue;
                    }
                }

                // 本轮所有显示（出牌、声明结算）都已完成，再留给播报的节奏停顿。
                // 真人自己打出的牌用更短的停顿，不必让自己干等自己。
                await PaceAsync(current);

                // —— 轮转下家：沿逆时针座次到右手侧相邻一家，见 SeatOrder ——
                current = SeatOrder.NextSeat(current);
            }
        }

        /// <summary>
        /// 一轮结算完成后为播报留的节奏停顿。
        /// 放在声明结算之后，让「出牌 → 可碰/杠/胡」零延迟，同时保证下一张牌
        /// 不会盖掉上一句配音。测试场景 ThinkDelayMs=0 时完全不等待。
        /// </summary>
        private Task PaceAsync(int discarder)
        {
            int delay = (_agents[discarder] is IHumanControlled)
                ? (HumanThinkDelayMs >= 0 ? HumanThinkDelayMs : ThinkDelayMs)
                : ThinkDelayMs;
            return delay > 0 ? Task.Delay(delay) : Task.CompletedTask;
        }

        private int _turnCount;

        /// <summary>自己回合内暗动作征询的结果。</summary>
        private sealed class ConcealedStep
        {
            /// <summary>是否已终局（胡牌或补牌时牌墙耗尽），Result 已就绪。</summary>
            public bool Ended;

            /// <summary>连续暗杠后最后补到的牌，供后续出牌上下文使用。</summary>
            public Tile LastDrawn;
        }

        /// <summary>
        /// 自己回合内的暗动作统一征询：自摸胡 / 杠上开花 + 暗杠 + 补杠。
        /// 暗杠/补杠成立后从墙尾补 1 张，循环再问一次（支持连续开杠与杠上开花）。
        /// 补杠亮牌时先走 ResolveRobKongAsync 让其余三家抢胡，被抢则杠不成立、直接终局。
        /// 返回 null 或不在选项里的动作 = 「过」，继续正常出牌流程。
        /// </summary>
        private async Task<ConcealedStep> ResolveConcealedAsync(int p, Tile drawn, string message)
        {
            Tile current = drawn;
            if (current == null)
            {
                var hand = _players[p].ConcealedTiles;
                current = hand.Count > 0 ? hand[hand.Count - 1] : null;
            }
            string prompt = message;

            while (current != null)
            {
                PlayerState player = _players[p];
                var options = new List<ClaimOption>();

                // 自摸胡 / 天胡 / 杠上开花
                if (CanSelfDraw(player))
                    options.Add(ClaimOption.Of(ClaimType.Win, current.Kind));

                // 暗杠 / 补杠：都要从墙尾补一张，牌墙空了就不能开杠
                if (_wall.RemainingCount > 0 && (_rules.AllowConcealedKong || _rules.AllowAddedKong))
                {
                    int[] counts = HandCountsOf(player);
                    if (_rules.AllowConcealedKong)
                    {
                        for (int k = 0; k < 34; k++)
                        {
                            if (counts[k] == 4)
                                options.Add(ClaimOption.Of(ClaimType.ConcealedKong, (TileKind)k));
                        }
                    }
                    if (_rules.AllowAddedKong)
                    {
                        // 已碰过的一组，手里还有同款那张 → 可升级成补杠
                        foreach (Meld m in player.Melds)
                        {
                            if (m.Type != ClaimType.Pong || m.Tiles.Count == 0) continue;
                            var kind = m.Tiles[0].Kind;
                            if (counts[(int)kind] < 1) continue;
                            if (options.Any(o => o.Type == ClaimType.AddedKong && o.Kind == kind)) continue;
                            options.Add(ClaimOption.Of(ClaimType.AddedKong, kind));
                        }
                    }
                }

                if (options.Count == 0)
                    return new ConcealedStep { Ended = false, LastDrawn = current };

                var ctx = BuildContext(p, current, claimOptions: options);
                // 用「带选项」的重载广播，UI 才能收到可胡/可暗杠选项并弹出按钮
                Raise(GameEventType.ConcealedActionOffered, p, current, prompt, options);
                var act = await _agents[p].ChooseConcealedAction(ctx);
                if (act == null || !options.Any(o => SameOption(o, act)))
                    return new ConcealedStep { Ended = false, LastDrawn = current };

                if (act.Type == ClaimType.Win)
                {
                    EndInWin(p, current, isSelfDraw: true, loser: -1);
                    return new ConcealedStep { Ended = true, LastDrawn = current };
                }

                if (act.Type == ClaimType.ConcealedKong)
                {
                    if (_wall.RemainingCount == 0) { EndInDraw(); return new ConcealedStep { Ended = true, LastDrawn = current }; }
                    Tile kongTile = ApplyConcealedKong(p, act.Kind);
                    current = _wall.DrawReplacement();
                    player.ConcealedTiles.Add(current);
                    // 顺序要紧：先播「暗杠」让 UI 扣掉四张同款，再播补牌加一张。
                    // 反过来若补到的牌恰好同款，UI 会把它误扣掉。
                    // 事件带的是「被杠的那张牌」而非补牌，UI 才能画出正确的四张副露。
                    Raise(GameEventType.ClaimResolved, p, kongTile, "暗杠");
                    Raise(GameEventType.TileDrawn, p, current, "暗杠补牌");
                    prompt = "杠上开花";
                    continue;
                }

                if (act.Type == ClaimType.AddedKong)
                {
                    if (_wall.RemainingCount == 0) { EndInDraw(); return new ConcealedStep { Ended = true, LastDrawn = current }; }
                    Tile added = player.ConcealedTiles.FirstOrDefault(t => t.Kind == act.Kind);
                    if (added == null) return new ConcealedStep { Ended = false, LastDrawn = current };

                    // 先亮牌征询抢杠胡。被抢时这四张牌一点都不能动：副露仍是碰、
                    // 手里那张也还在，杠完全没有成立，自然也不计任何杠加成。
                    int robber = await ResolveRobKongAsync(p, added);
                    if (robber >= 0)
                    {
                        EndInWin(robber, added, isSelfDraw: false, loser: p, robbedKong: true);
                        return new ConcealedStep { Ended = true, LastDrawn = current };
                    }

                    ApplyAddedKong(p, act.Kind);
                    current = _wall.DrawReplacement();
                    player.ConcealedTiles.Add(current);
                    // 与暗杠同理：先播补杠让 UI 升级副露并扣掉手里那张，再播补牌加一张。
                    Raise(GameEventType.ClaimResolved, p, added, "补杠");
                    Raise(GameEventType.TileDrawn, p, current, "补杠补牌");
                    prompt = "杠上开花";
                    continue;
                }

                return new ConcealedStep { Ended = false, LastDrawn = current };
            }

            return new ConcealedStep { Ended = false, LastDrawn = current };
        }

        /// <summary>成立一组暗杠：从暗牌里拿走四张同款，副露来源记为 -1；返回其中一张供事件播报。</summary>
        private Tile ApplyConcealedKong(int p, TileKind kind)
        {
            PlayerState player = _players[p];
            var tiles = player.ConcealedTiles.Where(t => t.Kind == kind).Take(4).ToList();
            foreach (Tile t in tiles) player.ConcealedTiles.Remove(t);
            player.Melds.Add(new Meld(ClaimType.ConcealedKong, tiles, -1));
            return tiles.Count > 0 ? tiles[0] : null;
        }

        /// <summary>
        /// 补杠亮牌后向其余三家征询抢杠胡，返回抢杠者索引（-1 = 无人抢）。
        /// 只有补杠能被抢：暗杠的四张从未公开，所以不会调用本方法。
        /// 多家能胡时沿用 ClaimResolver 的「离杠家最近」优先级（即已有的单赢家逻辑）。
        /// 抢杠只给「胡」这一个选项，不能顺带碰/杠这张牌。
        /// </summary>
        private async Task<int> ResolveRobKongAsync(int kongPlayer, Tile tile)
        {
            if (!_rules.AllowRobbingKong || tile == null) return -1;
            var claims = new List<ClaimOption>[4];
            bool any = false;
            // 按真实座次从杠家的下家开始征询，而不是按索引 0→3。
            int p = kongPlayer;
            for (int step = 0; step < 3; step++)
            {
                p = SeatOrder.NextSeat(p);
                int[] counts = HandCountsOf(_players[p]);
                counts[(int)tile.Kind]++;
                if (!WinChecker.CanWinFull(counts, _rules)) continue;

                var options = new List<ClaimOption> { ClaimOption.Of(ClaimType.Win, tile.Kind) };
                var ctx = BuildContext(p, null, claimOptions: options, discarder: kongPlayer);
                Raise(GameEventType.ClaimOffered, p, tile, "可抢杠胡", options);
                var choice = await _agents[p].ChooseClaim(ctx);
                if (choice != null && options.Any(o => SameOption(o, choice)))
                {
                    claims[p] = new List<ClaimOption> { choice };
                    any = true;
                }
            }
            if (!any) return -1;
            var resolution = _claimResolver.Resolve(claims, kongPlayer);
            return resolution != null && resolution.Type == ClaimType.Win ? resolution.PlayerIndex : -1;
        }

        /// <summary>补杠成立：手里拿走一张同款，把对应的一组碰升级成四张 AddedKong 副露。</summary>
        private void ApplyAddedKong(int p, TileKind kind)
        {
            PlayerState player = _players[p];
            Tile added = player.ConcealedTiles.FirstOrDefault(t => t.Kind == kind);
            if (added == null) return;
            player.ConcealedTiles.Remove(added);
            for (int i = 0; i < player.Melds.Count; i++)
            {
                Meld m = player.Melds[i];
                if (m.Type != ClaimType.Pong || m.Tiles.Count == 0 || m.Tiles[0].Kind != kind) continue;
                var tiles = new List<Tile>(m.Tiles) { added };
                player.Melds[i] = new Meld(ClaimType.AddedKong, tiles, m.FromPlayer);
                return;
            }
        }

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

            bool isKong = resolution.Type == ClaimType.Kong;
            claimer.Melds.Add(new Meld(isKong ? ClaimType.Kong : ClaimType.Pong,
                meldTiles, resolution.FromDiscarder));

            // 副露成立事件必须先于「杠后补牌」广播：否则补到的牌若与被杠的牌同款，
            // UI 在扣手牌时会把刚补进来的那张误扣掉。碰/杠统一在这里播一次。
            Raise(GameEventType.ClaimResolved, p, discarded, isKong ? "杠" : "碰");
            int next = p; // 碰/杠者继续出牌（本轮不再摸牌）

            if (isKong)
            {
                // 杠后从墙尾摸替换牌；牌墙已空则流局（副露已成立，牌数守恒不受影响）。
                if (_wall.RemainingCount == 0) { EndInDraw(); return -2; }
                Tile rep = _wall.DrawReplacement();
                claimer.ConcealedTiles.Add(rep);
                Raise(GameEventType.TileDrawn, p, rep, "杠后补牌");

                // 明杠补牌与自摸同构：统一走 concealed 决策通道，
                // 同时覆盖杠上开花、连续暗杠、补杠（补杠被抢时内部已直接结算终局）。
                // 旧实现这里只问「是否胡」，会吞掉补牌后新出现的开杠机会。
                var step = await ResolveConcealedAsync(p, rep, "杠上开花");
                if (step.Ended) return -1;
            }
            return next;
        }

        /// <summary>向其余三家征询声明并结算。</summary>
        private async Task<ClaimResolution> ResolveClaimsAsync(int discarder, Tile discarded)
        {
            var claims = new List<ClaimOption>[4];
            bool any = false;
            // 按真实座次从打出者的下家开始征询，而不是按索引 0→3。
            // 结算仍由 ClaimResolver 统一按距离判定，与征询先后无关。
            int p = discarder;
            for (int step = 0; step < 3; step++)
            {
                p = SeatOrder.NextSeat(p);
                int[] handCounts = HandCountsOf(_players[p]);
                var options = _claimResolver.GetOptions(_players[p], discarded, handCounts);
                if (options.Count > 0)
                {
                    var ctx = BuildContext(p, null, claimOptions: options, discarder: discarder);
                    Raise(GameEventType.ClaimOffered, p, discarded, "可碰/杠/胡", options);
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

        private void EndInWin(int winner, Tile winningTile, bool isSelfDraw, int loser, bool robbedKong = false)
        {
            WinPattern pattern = AnalyzeWinPattern(winner, winningTile, isSelfDraw);
            var scores = _scoreCalculator.ComputeScores(winner, isSelfDraw ? -1 : loser, isSelfDraw, pattern);
            int unit = _scoreCalculator.ComputeUnitPay(pattern, isSelfDraw);

            Result = new GameResult
            {
                WinnerIndex = winner,
                LoserIndex = loser,
                IsSelfDraw = isSelfDraw,
                IsRobbedKong = robbedKong,
                WinningTile = winningTile,
                Scores = scores,
                UnitPay = unit,
                PatternKind = pattern.Kind,
                OpenKongCount = pattern.OpenKongCount,
                ConcealedKongCount = pattern.ConcealedKongCount
            };

            // 结算文案供字幕 / 结算面板共用，直接写「每家 -N」比原来的分数向量易读
            string text = string.Format("{0} {1} · {2}{3} · 每家 -{4}",
                isSelfDraw ? "自摸" : (robbedKong ? "抢杠胡" : "胡"),
                winningTile != null ? winningTile.Kind.GetName() : "",
                pattern.DisplayName,
                pattern.KongDescription,
                unit);
            Raise(GameEventType.GameEnded, winner, winningTile, text);
        }

        /// <summary>
        /// 分析胡牌牌型与杠数。点炮胡时赢家暗牌只有 13 张（胡的那张已进牌河），
        /// 必须把胡牌张补进计数数组才是完整 14 张；自摸 / 杠上开花时那张还在手里，不能重复补。
        /// 杠只数胡牌者已成立的副露（明杠/暗杠分档计价），别人杠了白杠。
        /// </summary>
        private WinPattern AnalyzeWinPattern(int winner, Tile winningTile, bool isSelfDraw)
        {
            int[] counts = HandCountsOf(_players[winner]);
            if (!isSelfDraw && winningTile != null)
                counts[(int)winningTile.Kind]++;

            // 明杠 +1/家、暗杠 +2/家，必须分开数；补杠（AddedKong）归入明杠档。
            // 补杠被抢时该副露仍是 Pong，不会被算成杠。
            var melds = _players[winner].Melds;
            int openKongs = melds.Count(m => m.Type == ClaimType.Kong || m.Type == ClaimType.AddedKong);
            int concealedKongs = melds.Count(m => m.Type == ClaimType.ConcealedKong);
            return WinPattern.Analyze(counts, openKongs, concealedKongs, _rules);
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

        private void Raise(GameEventType type, int playerIndex, Tile tile, string message)
        {
            var handler = EventRaised;
            if (handler != null) handler(GameEvent.Of(type, playerIndex, tile, message));
        }

        private void Raise(GameEventType type, int playerIndex, Tile tile, string message, IList<ClaimOption> claimOptions)
        {
            var handler = EventRaised;
            if (handler != null) handler(GameEvent.Of(type, playerIndex, tile, message, claimOptions));
        }
    }
}
