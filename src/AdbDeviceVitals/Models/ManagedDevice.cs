using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AdbDeviceVitals.Services;

namespace AdbDeviceVitals.Models
{
    /// <summary>
    /// 用户在设置页主动添加的一台受管设备：序列号 + 数据卡槽 + 优先级 + 余量运营商。
    /// Slot = -1 表示自动探测当前数据卡；Priority 数字越小越优先（重复时按添加先后）。
    /// Carrier = None 表示让程序按卡槽 SIM 自动识别；指定后覆盖探测结果。
    /// </summary>
    public sealed class ManagedDevice
    {
        public const int AutoSlot = -1;

        public ManagedDevice(string serial, int slot, int priority, TrafficCarrier carrier, bool meteredOnUsb = true)
        {
            Serial = serial ?? string.Empty;
            Slot = slot < 0 ? AutoSlot : slot;
            Priority = priority < 1 ? 1 : priority;
            Carrier = carrier;
            MeteredOnUsb = meteredOnUsb;
        }

        public string Serial { get; private set; }
        public int Slot { get; private set; }
        public int Priority { get; private set; }
        public TrafficCarrier Carrier { get; private set; }
        public bool MeteredOnUsb { get; private set; }

        public bool IsAutoSlot { get { return Slot == AutoSlot; } }

        public string SlotText { get { return IsAutoSlot ? "自动" : "卡槽 " + Slot.ToString(CultureInfo.InvariantCulture); } }

        public ManagedDevice With(string serial, int slot, int priority, TrafficCarrier carrier)
        {
            return new ManagedDevice(serial, slot, priority, carrier, MeteredOnUsb);
        }
    }

    /// <summary>
    /// 受管设备列表的编解码与旧配置迁移。
    /// 存储格式：serial|slot|priority|carrier;serial|slot|priority|carrier
    /// 旧版本没有这个字段，用 DevicePriority（顺序）+ TargetDeviceSerial + CarrierByDevice 还原。
    /// </summary>
    public static class ManagedDeviceStore
    {
        private static readonly char[] RecordSeparator = { ';' };
        private static readonly char[] FieldSeparator = { '|' };

        public static IList<ManagedDevice> Parse(string text)
        {
            List<ManagedDevice> result = new List<ManagedDevice>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            int fallbackPriority = 0;
            foreach (string record in text.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = record.Split(FieldSeparator);
                if (fields.Length < 4) continue;
                string serial = fields[0].Trim();
                if (!Services.DeviceSelection.IsValidSerial(serial)) continue;

                int slot;
                if (!int.TryParse(fields[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out slot))
                    slot = ManagedDevice.AutoSlot;
                int priority;
                if (!int.TryParse(fields[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out priority))
                    priority = fallbackPriority + 1;
                int carrierValue;
                TrafficCarrier carrier = TrafficCarrier.None;
                if (int.TryParse(fields[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out carrierValue)
                    && Enum.IsDefined(typeof(TrafficCarrier), carrierValue))
                    carrier = (TrafficCarrier)carrierValue;

                bool duplicate = false;
                foreach (ManagedDevice existing in result)
                    if (string.Equals(existing.Serial, serial, StringComparison.OrdinalIgnoreCase)) { duplicate = true; break; }
                if (duplicate) continue;

                result.Add(new ManagedDevice(serial, slot, priority, carrier, fields.Length < 5 || fields[4].Trim() != "0"));
                fallbackPriority = Math.Max(fallbackPriority, priority);
            }
            Sort(result);
            return result;
        }

        public static string Serialize(IEnumerable<ManagedDevice> devices)
        {
            List<ManagedDevice> list = new List<ManagedDevice>();
            if (devices != null)
            {
                foreach (ManagedDevice device in devices)
                    if (device != null && Services.DeviceSelection.IsValidSerial(device.Serial)) list.Add(device);
            }
            Sort(list);
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) text.Append(';');
                ManagedDevice device = list[i];
                text.Append(device.Serial).Append('|')
                    .Append(device.Slot.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(device.Priority.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(((int)device.Carrier).ToString(CultureInfo.InvariantCulture));
                if (!device.MeteredOnUsb) text.Append("|0");
            }
            return text.ToString();
        }

        /// <summary>按优先级升序遍历 serial，供自动切换解析使用。</summary>
        public static IList<string> PrioritySerials(IEnumerable<ManagedDevice> devices)
        {
            List<ManagedDevice> list = new List<ManagedDevice>();
            if (devices != null)
                foreach (ManagedDevice device in devices)
                    if (device != null && Services.DeviceSelection.IsValidSerial(device.Serial)) list.Add(device);
            Sort(list);
            List<string> serials = new List<string>();
            foreach (ManagedDevice device in list) serials.Add(device.Serial);
            return serials;
        }

        /// <summary>
        /// 从旧配置迁移：优先级列表的顺序即优先级（1 起），预设设备没有优先级时排最后；
        /// 运营商沿用 CarrierByDevice。旧字段都没配时返回空列表。
        /// </summary>
        public static IList<ManagedDevice> Migrate(string devicePriority, string targetSerial, string carrierByDevice)
        {
            List<ManagedDevice> result = new List<ManagedDevice>();
            int priority = 1;
            foreach (string part in (devicePriority ?? string.Empty).Split(RecordSeparator))
            {
                string serial = (part ?? string.Empty).Trim();
                if (!Services.DeviceSelection.IsValidSerial(serial)) continue;
                if (Contains(result, serial)) continue;
                result.Add(new ManagedDevice(serial, ManagedDevice.AutoSlot, priority++,
                    Services.CarrierMapping.Get(carrierByDevice ?? string.Empty, serial)));
            }
            string target = (targetSerial ?? string.Empty).Trim();
            if (Services.DeviceSelection.IsValidSerial(target) && !Contains(result, target))
                result.Add(new ManagedDevice(target, ManagedDevice.AutoSlot, priority++,
                    Services.CarrierMapping.Get(carrierByDevice ?? string.Empty, target)));
            return result;
        }

        public static ManagedDevice Find(IEnumerable<ManagedDevice> devices, string serial)
        {
            if (devices == null || string.IsNullOrEmpty(serial)) return null;
            foreach (ManagedDevice device in devices)
                if (device != null && string.Equals(device.Serial, serial, StringComparison.OrdinalIgnoreCase)) return device;
            return null;
        }

        private static bool Contains(IList<ManagedDevice> devices, string serial)
        {
            foreach (ManagedDevice device in devices)
                if (string.Equals(device.Serial, serial, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>稳定排序：优先级相同按 serial 保留原顺序（List.Sort 不稳，用索引兜底）。</summary>
        private static void Sort(List<ManagedDevice> list)
        {
            List<ManagedDevice> ordered = new List<ManagedDevice>();
            foreach (ManagedDevice device in list) ordered.Add(device);
            list.Sort(delegate(ManagedDevice a, ManagedDevice b)
            {
                int cmp = a.Priority.CompareTo(b.Priority);
                if (cmp != 0) return cmp;
                return ordered.IndexOf(a).CompareTo(ordered.IndexOf(b));
            });
        }
    }
}
