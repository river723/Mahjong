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

        // AI 出牌停顿：以毫秒存储，界面按「秒」录入（支持小数）。
        // 下限 1.3s 是含席位前缀播报能被容纳的最短节奏；默认 3s。
        public const int DefaultAiThinkMs = 3000;
        public const int MinAiThinkMs = 1300;
        public const int MaxAiThinkMs = 30000;

        private static readonly GameSettings s_current = Load();

        /// <summary>全场唯一的一份设置。</summary>
        public static GameSettings Current { get { return s_current; } }

        /// <summary>新的一场里每人的初始金币数。</summary>
        public int InitialCoins = DefaultInitialCoins;

        /// <summary>开场 AI 档位（0 普通 / 1 较真 / 2 大师）。</summary>
        public int StartDifficulty = DefaultStartDifficulty;

        /// <summary>true=按连胜/局数继续升档；false=固定在起始档位。</summary>
        public bool AutoLevelUp = true;

        /// <summary>东家、北家出牌后的停顿毫秒数；西家固定 1300ms、真人固定 800ms 均不受影响。</summary>
        public int AiThinkMs = DefaultAiThinkMs;

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

        /// <summary>出牌间隔收敛到 1.3~30 秒对应的毫秒区间。</summary>
        public static int ClampAiThinkMs(int ms)
        {
            if (ms < MinAiThinkMs) return MinAiThinkMs;
            if (ms > MaxAiThinkMs) return MaxAiThinkMs;
            return ms;
        }

        /// <summary>
        /// 把界面输入的「秒」解析成毫秒（支持 1.3 这样的小数），并收敛到合法区间。
        /// 非法文本返回 null，由界面提示用户重填。
        /// </summary>
        public static int? TryParseAiThinkMs(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            double seconds;
            if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return null;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return null;
            return ClampAiThinkMs((int)Math.Round(seconds * 1000.0));
        }

        /// <summary>把毫秒格式化成界面显示的秒文本（整数不带小数点，非整数保留必要位数）。</summary>
        public static string FormatAiThinkSeconds(int ms)
        {
            return (ms / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);
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
                    else if (k == "aithinkms" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                        s.AiThinkMs = ClampAiThinkMs(n);
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
            AiThinkMs = ClampAiThinkMs(AiThinkMs);
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder();
                sb.AppendLine("# 家人麻将设置：coins=每人初始金币，difficulty=起始难度(0..2)，autolevel=自动升档");
                sb.AppendLine("# aithinkms=东家/北家出牌停顿毫秒(1300~30000，即1.3~30秒)，西家固定1300，下一局生效");
                sb.AppendLine("coins=" + InitialCoins.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("difficulty=" + StartDifficulty.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("autolevel=" + (AutoLevelUp ? "1" : "0"));
                sb.AppendLine("aithinkms=" + AiThinkMs.ToString(CultureInfo.InvariantCulture));
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
            AiThinkMs = DefaultAiThinkMs;
        }
    }
}
