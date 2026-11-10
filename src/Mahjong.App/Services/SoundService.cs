using System;
using System.IO;
using System.Media;

namespace Mahjong.App.Services
{
    /// <summary>
    /// 音效服务：短音效使用 System.Media.SoundPlayer（wav，零依赖）。
    /// </summary>
    public sealed class SoundService
    {
        private readonly SoundPlayer _player = new SoundPlayer();

        /// <summary>播放一张 wav 路径（如「碰」音效）。不存在时静默忽略。</summary>
        public void Play(string wavPath)
        {
            try
            {
                if (File.Exists(wavPath)) _player.SoundLocation = wavPath;
                _player.Play();
            }
            catch
            {
                // 音效加载失败静默降级
            }
        }
    }
}