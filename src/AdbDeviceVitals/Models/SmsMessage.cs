using System;

namespace AdbDeviceVitals.Models
{
    /// <summary>
    /// 本次运行期间收到的一条短信。只存在内存里，不写 CSV、不写日志、不上传。
    /// DateMs 来自手机的收件箱 date 列（epoch 毫秒），用于增量游标。
    /// </summary>
    public sealed class SmsMessage
    {
        public SmsMessage(long id, string address, string body, long dateMs, DateTime receivedAt)
        {
            Id = id;
            Address = address ?? string.Empty;
            Body = body ?? string.Empty;
            DateMs = dateMs;
            ReceivedAt = receivedAt;
        }

        public long Id { get; private set; }
        public string Address { get; private set; }
        public string Body { get; private set; }
        public long DateMs { get; private set; }
        public DateTime ReceivedAt { get; private set; }
    }
}
