using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Core.Game;
using Mahjong.Core.Rules;
using Xunit;

namespace Mahjong.Tests
{
    public class DebugTests
    {
        [Fact]
        public async Task 观察单局_seed0_5_结果()
        {
            for (int seed = 0; seed < 6; seed++)
            {
                var agents = new IAgent[4];
                for (int i = 0; i < 4; i++) agents[i] = new ScriptedAgent(i);
                var engine = new GameEngine(new RuleConfig(), agents, 0, wallSeed: seed);
                int events = 0;
                engine.EventRaised += e => { events++; };
                var result = await engine.RunAsync();
                int handSum = engine.Players.Sum(p => p.ConcealedTiles.Count);
                int meldSum = engine.Players.Sum(p => p.Melds.Sum(m => m.Tiles.Count));
                Console.WriteLine("seed={0} events={1} winner={2} self={3} draw={4} scores=[{5}] hands={6} melds={7} discards={8} wallLeft={9}",
                    seed, events, result.WinnerIndex, result.IsSelfDraw, result.IsDraw,
                    string.Join(",", result.Scores), handSum, meldSum, engine.Discards.Count, engine.WallRemaining);
                foreach (var p in engine.Players)
                    Console.WriteLine("   p{0} hand={1} melds={2}", p.Index, p.ConcealedTiles.Count, p.Melds.Count);
            }
        }
    }
}
