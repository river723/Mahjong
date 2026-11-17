using System;
using System.IO;

namespace Mahjong.App.Services
{
    /// <summary>
    /// 诊断日志写入助手。历史版本把路径写死成 E:\cc_study\mj 且不建目录，
    /// 目录不存在的机器上每次 AppendAllText 都抛 DirectoryNotFoundException，
    /// 而且连异常处理器自己的兜底写入也指向同一路径，导致程序启动 ~2s 后崩溃且无法自愈。
    /// 这里每次写入前都 CreateDirectory 自愈，写入失败只吞掉、绝不影响对局。
    /// </summary>
    public static class GameLog
    {
        public const string LogDir = @"e:\cc_study\mj";

        /// <summary>启动时提前把目录建好（幂等，失败无碍——每次写入还会再自愈一次）。</summary>
        public static void EnsureDir()
        {
            try { Directory.CreateDirectory(LogDir); } catch { }
        }

        /// <summary>写对局事件日志 events.txt。</summary>
        public static void AppendEvent(string line)
        {
            Append(LogDir, "events.txt", line);
        }

        /// <summary>写引擎异常日志 engine_err.txt。</summary>
        public static void AppendEngineError(string line)
        {
            Append(LogDir, "engine_err.txt", line);
        }

        private static void Append(string dir, string fileName, string line)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, fileName), line);
            }
            catch
            {
                // 日志只是诊断辅助：写不进去也不能影响游戏本身。
            }
        }
    }
}