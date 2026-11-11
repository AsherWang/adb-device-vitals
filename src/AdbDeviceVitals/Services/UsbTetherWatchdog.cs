using System;
using AdbDeviceVitals.Models;

namespace AdbDeviceVitals.Services
{
    /// <summary>
    /// 判断什么时候该把掉线的 USB 网络共享重新打开。纯状态机：不碰 ADB、不碰界面、不读系统时间，
    /// 时间由调用方传进来，方便在测试里推着时钟走。
    ///
    /// 只负责「保持已经开着的状态」，启动后本来就没开的交给「启动后自动开启 USB 网络共享」那个开关。
    /// 这样用户没让程序开共享时，光打开守护不会自己把手机切到 RNDIS。
    /// </summary>
    public sealed class UsbTetherWatchdog
    {
        /// <summary>主窗口每 2.5 秒采集一次，这里按固定间隔做判定，不必每刷新一轮都算。</summary>
        public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(20);

        /// <summary>连续读到未开启这么久才认定真的断开，避免单次解析抖动就去切 USB 模式。</summary>
        public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(20);

        /// <summary>一次尝试之后至少隔这么久才允许再试；切换 USB 功能本身要重新枚举。</summary>
        public static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(60);

        private bool _wasActive;
        private bool _manualOff;
        private DateTime _lastCheckUtc = DateTime.MinValue;
        private DateTime _inactiveSinceUtc = DateTime.MinValue;
        private DateTime _lastAttemptUtc = DateTime.MinValue;

        /// <summary>用户在主窗口手动关过共享：在他重新手动打开之前，自动恢复一律停手。</summary>
        public void NoteManualOff()
        {
            _manualOff = true;
            _inactiveSinceUtc = DateTime.MinValue;
        }

        public void NoteManualOn()
        {
            _manualOff = false;
            _inactiveSinceUtc = DateTime.MinValue;
        }

        /// <summary>
        /// 每轮采集后调用一次，返回 true 表示应该立即尝试重新开启共享。
        /// assumeWasActive 来自「启动后自动开启 USB 网络共享」：它打开时，即使还没观察到
        /// 共享处于开启状态，也按「本来就该开着」处理。
        /// busy 表示已有别的 USB 切换任务在跑，此时不插手。
        /// </summary>
        public bool Observe(DeviceSnapshot snapshot, DateTime nowUtc, bool enabled, bool assumeWasActive, bool busy)
        {
            if (!enabled || _manualOff || busy || snapshot == null || !snapshot.Connected)
            {
                _inactiveSinceUtc = DateTime.MinValue;
                return false;
            }

            if (snapshot.UsbTetheringActive)
            {
                _wasActive = true;
                _inactiveSinceUtc = DateTime.MinValue;
                return false;
            }

            if (!_wasActive && !assumeWasActive) return false;

            if (_lastCheckUtc != DateTime.MinValue && nowUtc - _lastCheckUtc < CheckInterval) return false;
            _lastCheckUtc = nowUtc;

            if (_inactiveSinceUtc == DateTime.MinValue) _inactiveSinceUtc = nowUtc;
            if (nowUtc - _inactiveSinceUtc < ConfirmWindow) return false;
            if (_lastAttemptUtc != DateTime.MinValue && nowUtc - _lastAttemptUtc < RetryCooldown) return false;

            _lastAttemptUtc = nowUtc;
            _inactiveSinceUtc = DateTime.MinValue;
            return true;
        }
    }
}
