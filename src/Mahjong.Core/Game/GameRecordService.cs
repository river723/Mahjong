using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mahjong.Core.Tiles;

namespace Mahjong.Core.Game
{
    /// <summary>
    /// 一局战绩记录（M7：战绩持久化）。
    /// </summary>
    public sealed class GameRecord
    {
        public DateTime PlayedAt { get; set; }
        public int WinnerIndex { get; set; }
        public bool IsSelfDraw { get; set; }
        public int[] Scores { get; set; }
        public int WinningTileKind { get; set; }
        public int RoundIndex { get; set; }
    }

    /// <summary>
    /// 战绩持久化服务：以 JSON 形式保存到 %APPDATA%/Mahjong/records.json
    /// </summary>
    public sealed class GameRecordService
    {
        private readonly string _recordsPath;

        public GameRecordService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "Mahjong");
            Directory.CreateDirectory(dir);
            _recordsPath = Path.Combine(dir, "records.json");
        }

        /// <summary>追加一条战绩。</summary>
        public void Append(GameRecord record)
        {
            var list = LoadAll();
            list.Add(record);
            SaveAll(list);
        }

        /// <summary>读取全部战绩。</summary>
        public List<GameRecord> LoadAll()
        {
            try
            {
                if (!File.Exists(_recordsPath)) return new List<GameRecord>();
                var json = File.ReadAllText(_recordsPath);
                return ParseJson(json);
            }
            catch
            {
                return new List<GameRecord>();
            }
        }

        private void SaveAll(List<GameRecord> records)
        {
            try
            {
                var json = ToJson(records);
                File.WriteAllText(_recordsPath, json);
            }
            catch
            {
                // 静默降级，不影响游戏
            }
        }

        private static List<GameRecord> ParseJson(string json)
        {
            var list = new List<GameRecord>();
            if (string.IsNullOrEmpty(json)) return list;
            // 简单手工解析：每条记录用 {...} 分隔
            int pos = 0;
            while (pos < json.Length)
            {
                var start = json.IndexOf('{', pos);
                if (start < 0) break;
                var end = json.IndexOf('}', start);
                if (end < 0) break;
                var obj = json.Substring(start + 1, end - start - 1);
                var record = ParseRecord(obj);
                if (record != null) list.Add(record);
                pos = end + 1;
            }
            return list;
        }

        private static GameRecord ParseRecord(string obj)
        {
            var record = new GameRecord();
            var parts = obj.Split(',');
            foreach (var part in parts)
            {
                var kv = part.Split(new[] { ':' }, 2);
                if (kv.Length != 2) continue;
                var key = kv[0].Trim().Trim('"');
                var value = kv[1].Trim().Trim('"');
                try
                {
                    switch (key)
                    {
                        case "PlayedAt":
                            record.PlayedAt = DateTime.Parse(value);
                            break;
                        case "WinnerIndex":
                            record.WinnerIndex = int.Parse(value);
                            break;
                        case "IsSelfDraw":
                            record.IsSelfDraw = bool.Parse(value);
                            break;
                        case "WinningTileKind":
                            record.WinningTileKind = int.Parse(value);
                            break;
                        case "RoundIndex":
                            record.RoundIndex = int.Parse(value);
                            break;
                    }
                }
                catch { }
            }
            return record;
        }

        private static string ToJson(List<GameRecord> records)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[");
            for (int i = 0; i < records.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var r = records[i];
                sb.Append("{\"PlayedAt\":\"" + r.PlayedAt.ToString("o") + "\",");
                sb.Append("\"WinnerIndex\":" + r.WinnerIndex + ",");
                sb.Append("\"IsSelfDraw\":" + (r.IsSelfDraw ? "true" : "false") + ",");
                sb.Append("\"WinningTileKind\":" + r.WinningTileKind + ",");
                sb.Append("\"RoundIndex\":" + r.RoundIndex + "}");
            }
            sb.Append("]");
            return sb.ToString();
        }
    }
}