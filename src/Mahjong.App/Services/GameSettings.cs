using System;
using System.Globalization;
using System.IO;
using System.Text;
using Mahjong.Core.Ai;

namespace Mahjong.App.Services
{
    /// <summary>
    /// 游戏设置（初始金币 / 起始难度 / 是否自动升档），持久化到
    /// %LocalAppData%\Mahjong\settings.ini，进程启动时加载一次。
    ///
    /// 声音相关的四个开关不在这里：它们由 AudioService 自己存到同目录的 audio.ini
    /// （那个文件里还有 tailpad / keepalive 等手写的高级项），设置窗口直接读写
    /// AudioService 的属性即可即时生效，避免两处配置互相覆盖。
    ///
    /// 生效时机：初始金币与起始难度都属于「一场的参数」，只在新的一场开始时读取，
    /// 中途修改不会改动正在进行的账本或已经创建好的 AI。
    /// </summary>
    public sealed class GameSettings
    {
        public const int DefaultInitialCoins = 20;
        public const int MinInitialCoins = 5;
        public const int MaxInitialCoins = 999;
        public const int DefaultStartDifficulty = 0;

        private static readonly GameSettings s_current = Load();

        /// <summary>全场唯一的一份设置。</summary>
        public static GameSettings Current { get { return s_current; } }

        /// <summary>新的一场里每人的初始金币数。</summary>
        public int InitialCoins = DefaultInitialCoins;

        /// <summary>开场 AI 档位（0 普通 / 1 较真 / 2 大师）。</summary>
        public int StartDifficulty = DefaultStartDifficulty;

        /// <summary>true=按连胜/局数继续升档；false=固定在起始档位。</summary>
        public bool AutoLevelUp = true;

        public static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mahjong");
                return Path.Combine(dir, "settings.ini");
            }
        }

        /// <summary>金币输入收敛到合法区间；非法文本返回 null，由界面提示用户重填。</summary>
        public static int? TryParseInitialCoins(string raw)
        {
            int value;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)) return null;
            return ClampInitialCoins(value);
        }

        /// <summary>越界值收敛而不是报错：一场金币太多没有意义，太少一炮就终局。</summary>
        public static int ClampInitialCoins(int value)
        {
            if (value < MinInitialCoins) return MinInitialCoins;
            if (value > MaxInitialCoins) return MaxInitialCoins;
            return value;
        }

        public static int ClampDifficulty(int value)
        {
            if (value < 0) return 0;
            if (value > DifficultyState.MaxLevel) return DifficultyState.MaxLevel;
            return value;
        }

        private static GameSettings Load()
        {
            var s = new GameSettings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    if (line == null || line.TrimStart().StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    int n;
                    if (k == "coins" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                        s.InitialCoins = ClampInitialCoins(n);
                    else if (k == "difficulty" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                        s.StartDifficulty = ClampDifficulty(n);
                    else if (k == "autolevel")
                        s.AutoLevelUp = v != "0";
                }
            }
            catch { /* 配置损坏或目录无权限：用默认值继续，不让游戏起不来 */ }
            return s;
        }

        /// <summary>落盘；先收敛一遍，避免界面手滑写进非法值。</summary>
        public void Save()
        {
            InitialCoins = ClampInitialCoins(InitialCoins);
            StartDifficulty = ClampDifficulty(StartDifficulty);
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder();
                sb.AppendLine("# 家人麻将设置：coins=每人初始金币，difficulty=起始难度(0..2)，autolevel=自动升档");
                sb.AppendLine("coins=" + InitialCoins.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("difficulty=" + StartDifficulty.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("autolevel=" + (AutoLevelUp ? "1" : "0"));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { /* 无写权限时保持本次会话设置即可 */ }
        }

        /// <summary>恢复默认（界面上的「恢复默认」按钮）。</summary>
        public void Reset()
        {
            InitialCoins = DefaultInitialCoins;
            StartDifficulty = DefaultStartDifficulty;
            AutoLevelUp = true;
        }
    }
}
