using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Speech;
using System.Speech.Synthesis;
using System.Speech.Synthesis.TtsEngine;

namespace Mahjong.App.Services
{
    /// <summary>
    /// 语音服务：预生成 wav 为主，TTS 兜底；所有语音同步大字幕。
    /// </summary>
    public sealed class SpeechService
    {
        private readonly SpeechSynthesizer _tts;
        private readonly List<string> _installedVoices = new List<string>();
        private bool _zhCnAvailable;

        public SpeechService()
        {
            try
            {
                _tts = new SpeechSynthesizer();
                foreach (InstalledVoice voice in _tts.GetInstalledVoices())
                {
                    _installedVoices.Add(voice.VoiceInfo.Name);
                    if (voice.VoiceInfo.Culture.Name == "zh-CN") _zhCnAvailable = true;
                }
            }
            catch
            {
                _tts = null;
            }
        }

        /// <summary>是否具备中文 TTS 能力。</summary>
        public bool ZhCnAvailable { get { return _zhCnAvailable; } }

        /// <summary>播放预生成 wav（优先）。</summary>
        public void PlayWav(string wavPath)
        {
            try
            {
                if (File.Exists(wavPath))
                {
                    using (var player = new SoundPlayer(wavPath)) player.PlaySync();
                }
            }
            catch
            {
                // 静默降级
            }
        }

        /// <summary>实时 TTS（播报分数等动态内容）。</summary>
        public void Speak(string text)
        {
            if (_tts == null) return;
            try
            {
                _tts.Rate = -2;
                _tts.Volume = 100;
                _tts.Speak(text);
            }
            catch
            {
                // 静默降级
            }
        }
    }
}