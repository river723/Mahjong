namespace Mahjong.Core.Ai
{
    /// <summary>
    /// 动态难度：按「玩家连胜」或「累计完成局数」自动升档，只升不降。
    /// 用静态状态是刻意的——结算后重开一局是 new MainWindow()，实例字段会丢；
    /// 进程退出则清零（连胜本来就该随一次游戏会话重置）。
    /// 纯逻辑放在 Core 里，方便单元测试覆盖升档规则。
    /// </summary>
    public static class DifficultyState
    {
        public const int MaxLevel = 2;

        // 触发阈值集中在这里，改难度曲线只动这四行。
        // 数值比早期版本放大：让玩家先在普通/较真档多打几局，别刚上手就被大师压着打。
        public const int StreakForLevel1 = 3;
        public const int StreakForLevel2 = 6;
        public const int RoundsForLevel1 = 6;
        public const int RoundsForLevel2 = 12;

        private static readonly string[] Names = { "普通", "较真", "大师" };

        /// <summary>当前档位 0..2。</summary>
        public static int Level { get; private set; }

        /// <summary>本会话已完成局数（含流局）。</summary>
        public static int RoundsPlayed { get; private set; }

        /// <summary>玩家（0 号，南）当前连胜局数。</summary>
        public static int WinStreak { get; private set; }

        /// <summary>是否由 --difficulty / MJ_DIFFICULTY 固定，固定时不自动升档。</summary>
        public static bool IsFixed { get; private set; }

        public static string LevelName(int level)
        {
            if (level < 0) level = 0;
            if (level > MaxLevel) level = MaxLevel;
            return Names[level];
        }

        // Lv0 = 普通；Lv1 = 较真；Lv2 = 大师。三档用各自的 Profile，出牌打分逐档变强。
        public static AiProfile ProfileFor(int level)
        {
            if (level <= 0) return AiProfile.Normal;
            if (level == 1) return AiProfile.Hard;
            return AiProfile.Master;
        }

        /// <summary>碰/杠的接受概率（%）。档位越高越愿意副露，成牌越快。</summary>
        public static int ClaimPercentFor(int level)
        {
            if (level <= 0) return 40;
            if (level == 1) return 70;
            return 95;
        }

        /// <summary>Lv2 额外强化：在能听牌的候选里优先选进张最多的。</summary>
        public static bool PreferMoreWaitsFor(int level) { return level >= 2; }

        /// <summary>调试/录屏用：固定档位，跳过自动升档。</summary>
        public static void SetFixedLevel(int level)
        {
            Level = ClampLevel(level);
            IsFixed = true;
        }

        /// <summary>
        /// 「设置」里的开场档位：清空本会话的连胜 / 局数进度，从指定档位重新起步。
        /// 改了起始难度就等于换了一条难度曲线，旧的连胜不该继续算数。
        /// autoLevelUp=false 时固定在该档位（与 --difficulty 行为一致），不再自动升档。
        /// </summary>
        public static void ApplyStartLevel(int level, bool autoLevelUp)
        {
            Reset();
            Level = ClampLevel(level);
            IsFixed = !autoLevelUp;
        }

        /// <summary>
        /// 只设置当前档位，不动固定标志与会话进度。
        /// 进程启动时加载「起始难度」用：此时还没有任何对局进度可清。
        /// </summary>
        public static void SetStartLevel(int level) { Level = ClampLevel(level); }

        private static int ClampLevel(int level)
        {
            if (level < 0) return 0;
            if (level > MaxLevel) return MaxLevel;
            return level;
        }

        /// <summary>测试用：清空会话进度。</summary>
        public static void Reset()
        {
            Level = 0;
            RoundsPlayed = 0;
            WinStreak = 0;
            IsFixed = false;
        }

        /// <summary>结算一局。返回给玩家看的升档提示；没有升档则返回 null。</summary>
        public static string RecordResult(bool playerWon)
        {
            RoundsPlayed++;
            WinStreak = playerWon ? WinStreak + 1 : 0;
            if (IsFixed) return null;

            int target = ComputeLevel(RoundsPlayed, WinStreak);
            if (target <= Level) return null;
            Level = target;

            string reason = WinStreak >= 1
                ? "连胜 " + WinStreak + " 局"
                : "累计 " + RoundsPlayed + " 局";
            return reason + " · 下局难度升为「" + LevelName(Level) + "」";
        }

        /// <summary>由累计局数与连胜数算出应处档位（两条路径取较高者）。</summary>
        public static int ComputeLevel(int rounds, int streak)
        {
            if (streak >= StreakForLevel2 || rounds >= RoundsForLevel2) return 2;
            if (streak >= StreakForLevel1 || rounds >= RoundsForLevel1) return 1;
            return 0;
        }
    }
}
