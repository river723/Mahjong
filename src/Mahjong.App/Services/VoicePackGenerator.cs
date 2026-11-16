using System;
using System.Collections.Generic;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using Mahjong.Core.Tiles;

namespace Mahjong.App.Services
{
    /// <summary>
    /// 离线语音包生成器（开发工具，不参与对局）。
    ///
    /// 用法：Mahjong.App.exe --genvoice [--voice-dir=&lt;目录&gt;] [--voice=&lt;SAPI 语音名&gt;]
    /// 用 Windows 自带中文 SAPI 语音，把 34 张牌面播报 + 碰/杠/胡等动作词
    /// 一次性合成为 PCM wav，裁掉首尾静音并统一响度后写入 Assets/Voice。
    ///
    /// 为什么预生成：棋牌类应用的通行做法是「预录/预生成短语音包 + 运行时兜底」——
    /// 出牌非常频繁，实时 TTS 有合成延迟且音色不稳；打包 wav 后播放零延迟、不依赖
    /// 用户机器上是否装了中文语音。想换音色时重跑一次本生成器即可。
    /// </summary>
    public static class VoicePackGenerator
    {
        public const int SampleRate = 16000;

        /// <summary>语音包词条：key = 稳定文件名（不含扩展名），value = 合成文本。</summary>
        private static readonly List<KeyValuePair<string, string>> _entries = BuildEntries();

        private static List<KeyValuePair<string, string>> BuildEntries()
        {
            var list = new List<KeyValuePair<string, string>>();
            // 34 张牌面。字牌按麻将习惯念 full name（东风/红中/发财/白板），
            // 单念「中」「发」「白」太短且易被听错。
            string[] ranks = { "一", "二", "三", "四", "五", "六", "七", "八", "九" };
            string[] suits = { "万", "条", "筒" };
            for (int suit = 0; suit < 3; suit++)
                for (int r = 0; r < 9; r++)
                    list.Add(new KeyValuePair<string, string>(
                        "tile_" + (suit * 9 + r).ToString("00"), ranks[r] + suits[suit]));
            list.Add(new KeyValuePair<string, string>("tile_27", "东风"));
            list.Add(new KeyValuePair<string, string>("tile_28", "南风"));
            list.Add(new KeyValuePair<string, string>("tile_29", "西风"));
            list.Add(new KeyValuePair<string, string>("tile_30", "北风"));
            list.Add(new KeyValuePair<string, string>("tile_31", "红中"));
            list.Add(new KeyValuePair<string, string>("tile_32", "发财"));
            list.Add(new KeyValuePair<string, string>("tile_33", "白板"));

            // 动作 / 事件播报
            list.Add(new KeyValuePair<string, string>("act_pong", "碰"));
            list.Add(new KeyValuePair<string, string>("act_kong", "杠"));
            list.Add(new KeyValuePair<string, string>("act_ckong", "暗杠"));
            list.Add(new KeyValuePair<string, string>("act_win", "胡了"));
            list.Add(new KeyValuePair<string, string>("act_selfdraw", "自摸"));
            list.Add(new KeyValuePair<string, string>("act_kongwin", "杠上开花"));
            list.Add(new KeyValuePair<string, string>("act_draw", "流局"));
            list.Add(new KeyValuePair<string, string>("act_deal", "开局"));

            // 席位（用于胡牌播报、可选的「谁打的」前缀）
            list.Add(new KeyValuePair<string, string>("seat_0", "南家"));
            list.Add(new KeyValuePair<string, string>("seat_1", "北家"));
            list.Add(new KeyValuePair<string, string>("seat_2", "东家"));
            list.Add(new KeyValuePair<string, string>("seat_3", "西家"));

            // 提示语（运行时 TTS 不可用时也能播）
            list.Add(new KeyValuePair<string, string>("hint_yourturn", "轮到你出牌"));
            list.Add(new KeyValuePair<string, string>("hint_claim", "可以碰杠胡"));
            return list;
        }

        /// <summary>牌面 → 语音包 key。与 Assets/Voice 下的文件名一一对应。</summary>
        public static string TileKey(TileKind kind)
        {
            return "tile_" + ((int)kind).ToString("00");
        }

        /// <summary>牌面 → 给字幕/兜底 TTS 用的中文播报文本。</summary>
        public static string TileText(TileKind kind)
        {
            return TextOf(TileKey(kind)) ?? kind.GetName();
        }

        /// <summary>语音包 key → 中文文本；语音包缺失时用它做实时 TTS 兜底。</summary>
        public static string TextOf(string key)
        {
            foreach (var entry in _entries)
                if (entry.Key == key) return entry.Value;
            return null;
        }

        /// <summary>生成整套语音包；返回写入的文件数，失败时抛出。</summary>
        public static int Generate(string outputDir, string preferredVoice, out string log)
        {
            var sb = new StringBuilder();
            Directory.CreateDirectory(outputDir);
            string tempDir = Path.Combine(Path.GetTempPath(), "mj_voice_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            using (var synth = new SpeechSynthesizer())
            {
                string chosen = PickChineseVoice(synth, preferredVoice);
                if (chosen == null)
                    throw new InvalidOperationException("本机没有可用的中文 SAPI 语音，无法生成语音包。");

                synth.SelectVoice(chosen);
                synth.Rate = 1;      // 麻将播报要干脆，比默认略快
                synth.Volume = 100;
                var format = new SpeechAudioFormatInfo(SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

                sb.AppendLine("voice=" + chosen);
                sb.AppendLine("format=16000Hz/16bit/mono PCM");
                sb.AppendLine("rate=1");
                sb.AppendLine("generated=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("count=" + _entries.Count);

                foreach (var entry in _entries)
                {
                    string raw = Path.Combine(tempDir, entry.Key + ".wav");
                    string dest = Path.Combine(outputDir, entry.Key + ".wav");
                    synth.SetOutputToWaveFile(raw, format);
                    synth.Speak(entry.Value);
                    synth.SetOutputToNull();

                    int ms = TrimAndNormalize(raw, dest);
                    sb.AppendLine(entry.Key + "=" + entry.Value + " (" + ms + "ms)");
                    File.Delete(raw);
                }
            }

            Directory.Delete(tempDir, true);
            File.WriteAllText(Path.Combine(outputDir, "voice_manifest.txt"), sb.ToString(), Encoding.UTF8);
            log = sb.ToString();
            return _entries.Count;
        }

        private static string PickChineseVoice(SpeechSynthesizer synth, string preferredVoice)
        {
            var names = new List<string>();
            foreach (InstalledVoice v in synth.GetInstalledVoices())
            {
                if (!v.Enabled) continue;
                names.Add(v.VoiceInfo.Name);
                if (!string.IsNullOrEmpty(preferredVoice) &&
                    v.VoiceInfo.Name.IndexOf(preferredVoice, StringComparison.OrdinalIgnoreCase) >= 0)
                    return v.VoiceInfo.Name;
            }
            foreach (string name in names)
            {
                if (name.IndexOf("Huihui", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Yaoyao", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Kangkang", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("zh-CN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0)
                    return name;
            }
            return null;
        }

        /// <summary>
        /// 裁掉 SAPI 输出首尾的静音、把峰值归一化到 -2.5dB 左右，并做 5ms 淡入淡出。
        /// 返回成品时长（毫秒）。输入输出均为标准 PCM wav。
        /// </summary>
        private static int TrimAndNormalize(string src, string dest)
        {
            byte[] raw = File.ReadAllBytes(src);
            int dataOffset = FindDataChunk(raw);
            int dataLength = BitConverter.ToInt32(raw, dataOffset + 4);
            int sampleCount = dataLength / 2;
            var samples = new short[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(raw, dataOffset + 8 + i * 2);

            int peak = 1;
            for (int i = 0; i < sampleCount; i++)
            {
                int a = Math.Abs((int)samples[i]);
                if (a > peak) peak = a;
            }
            // 阈值取峰值的一小部分：既能切掉合成器底噪，又不会吃掉轻声的字尾
            int threshold = Math.Max(120, peak / 25);
            int first = 0, last = sampleCount - 1;
            while (first < sampleCount && Math.Abs((int)samples[first]) < threshold) first++;
            while (last > first && Math.Abs((int)samples[last]) < threshold) last--;

            int pad = SampleRate / 25; // 40ms 前后留白，听感自然
            first = Math.Max(0, first - pad);
            last = Math.Min(sampleCount - 1, last + pad);
            int length = last - first + 1;
            if (length < SampleRate / 20) // 兜底：极端情况下至少 50ms
            {
                first = 0;
                length = Math.Min(sampleCount, SampleRate / 20);
            }

            double gain = 24000.0 / peak; // 目标峰值 ≈ 0.73 满刻度
            if (gain > 6.0) gain = 6.0;
            int fade = Math.Min(SampleRate / 200, length / 4); // 5ms

            var pcm = new byte[length * 2];
            for (int i = 0; i < length; i++)
            {
                double v = samples[first + i] * gain;
                if (v > 32767) v = 32767;
                if (v < -32768) v = -32768;
                if (fade > 0)
                {
                    if (i < fade) v *= (double)i / fade;
                    int fromEnd = length - 1 - i;
                    if (fromEnd < fade) v *= (double)fromEnd / fade;
                }
                short s = (short)v;
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                int byteRate = SampleRate * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + pcm.Length);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);              // PCM 格式块长度
                w.Write((short)1);        // PCM
                w.Write((short)1);        // mono
                w.Write(SampleRate);
                w.Write(byteRate);
                w.Write((short)2);        // block align
                w.Write((short)16);       // bits per sample
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(pcm.Length);
                w.Write(pcm);
            }
            return length * 1000 / SampleRate;
        }

        private static int FindDataChunk(byte[] raw)
        {
            for (int i = 12; i + 8 <= raw.Length; )
            {
                string id = Encoding.ASCII.GetString(raw, i, 4);
                int size = BitConverter.ToInt32(raw, i + 4);
                if (id == "data") return i;
                i += 8 + size + (size % 2); // chunk 按偶数字节对齐
            }
            throw new InvalidDataException("SAPI 输出的 wav 找不到 data 块");
        }
    }
}
