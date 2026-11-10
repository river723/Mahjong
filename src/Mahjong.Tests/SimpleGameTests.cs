using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Core.Game;
using Mahjong.Core.Rules;
using Mahjong.Core.Tiles;
using Xunit;

namespace Mahjong.Tests
{
    /// <summary>
    ///简单决策的 ScriptedAgent：打第一张，不声明。
    /// </summary>
    public sealed class SimpleAgent : IAgent
    {
        public Task<Tile> ChooseDiscard(DecisionContext context)
        {
            return Task.FromResult(context.Hand[0]);
        }

        public Task<ClaimOption> ChooseClaim(DecisionContext context)
        {
            return Task.FromResult<ClaimOption>(null);
        }

        public Task<ClaimOption> ChooseConcealedAction(DecisionContext context)
        {
            return Task.FromResult<ClaimOption>(null);
        }
    }

    /// <summary>
    ///对局的流程测试：四家打第一张并无声明。
    /// </summary>
    public class SimpleGameTests
    {
        [Fact]
        public async Task 四家无声明_须流局_用作基础测试()
        {
            for (int seed = 0; seed < 10; seed++)
            {
                var agents = new IAgent[4];
                for (int i = 0; i < 4; i++) agents[i] = new SimpleAgent();
                var engine = new GameEngine(new RuleConfig(), agents, 0, wallSeed: seed);
                var result = await engine.RunAsync();
                Console.WriteLine("seed={0} winner={1} draw={2}", seed, result.WinnerIndex, result.IsDraw);
                Assert.True(result.IsDraw, "简单玩家应该导致流局");
            }
        }
    }
}
