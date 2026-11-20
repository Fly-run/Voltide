using System;
using System.Collections.Generic;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// MCHOSE (迈丛) V9 Pro 2.4G headset.
    ///
    /// Dongle: VID 0x291D, PID 0x385D, product string "MCHOSE V9 PRO",
    /// manufacturer "C-Media Electronics Inc.". Shares the same "M HUB" protocol
    /// as the X9 and V9 Turbo+, only the USB ids differ.
    ///
    /// The receiver answers battery commands on the 64-byte FF90 collection
    /// (usage page 0xFF90, report 0x55):
    ///
    ///   query : 55 65 01 00 ...
    ///   reply : 55 65 VV 02 ...
    ///                 ^^ ^^
    ///                 |  +-- 0x02 marks the "battery" field (constant)
    ///                 +----- battery percentage
    ///
    /// Verified against HeadsetControl's reverse engineering of the V9 Pro on
    /// real hardware: 55 65 01 was answered with 55 65 32 02 (i.e. 50%). The
    /// byte-3 field id is used only to recognise the battery reply; no reliable
    /// charging flag was observed, so ChargingKnown stays false rather than
    /// guessing from a field id.
    ///
    /// Note: the query is answered by the headset itself, so the headset has to
    /// be powered on and paired. A dongle whose headset is off accepts the write
    /// but never answers; that is reported as Sleeping so the card keeps its
    /// last value.
    /// </summary>
    public class MchoseV9ProProvider : IDeviceProvider
    {
        private const ushort V9ProVid = 0x291D;
        private const ushort V9ProPid = 0x385D;
        private const ushort VendorPage = 0xFF90;
        private const int FrameSize = 64;
        private const byte ReportId = 0x55;
        private const byte CmdDeviceInfo = 0x65;
        private const byte TargetHeadset = 0x01;
        private const byte FieldBattery = 0x02;

        private const int PercentIndex = 2;
        private const int FieldIndex = 3;

        public string Name { get { return "MCHOSE V9 Pro"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HidDeviceInfo hid in catalog)
            {
                if (hid == null) continue;
                if (hid.VendorId != V9ProVid || hid.ProductId != V9ProPid) continue;
                if (hid.UsagePage != VendorPage) continue;
                if (hid.InputReportLength != FrameSize || hid.OutputReportLength != FrameSize) continue;
                if (string.IsNullOrEmpty(hid.Path) || !seen.Add(hid.Path)) continue;

                DeviceInfo device = new DeviceInfo();
                device.Id = "mchose-v9-pro-" + V9ProPid.ToString("X4");
                device.Brand = "MCHOSE";
                device.Model = "V9 Pro";
                device.Name = "MCHOSE V9 Pro";
                device.ShortModel = "V9 Pro";
                device.Kind = DeviceKind.Headset;
                device.VendorId = hid.VendorId;
                device.ProductId = hid.ProductId;
                device.HidPath = hid.Path;
                device.ProviderName = Name;
                found.Add(device);
            }
            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading reading = new DeviceReading();
            reading.UpdatedAt = DateTime.Now;
            reading.Online = false;
            reading.Level = BatteryLevel.Unknown;
            reading.ChargingKnown = false;

            try
            {
                using (HidSession session = new HidSession(device.HidPath, FrameSize, FrameSize))
                {
                    session.Flush(30);
                    byte[] reply = null;

                    for (int attempt = 0; attempt < 3 && reply == null; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(120);
                        if (!session.Write(CreateBatteryQuery(), 250))
                        {
                            reading.Error = "发送电量查询失败";
                            return reading;
                        }

                        reply = WaitForBattery(session, attempt == 0 ? 1200 : 650);
                    }

                    if (reply == null)
                    {
                        reading.Sleeping = true;
                        reading.Error = "耳机休眠或未连接";
                        return reading;
                    }

                    reading.Raw = ToHex(reply);
                    int percent = reply[PercentIndex];
                    if (percent > 100)
                    {
                        reading.Online = true;
                        reading.Error = "电量读数异常";
                        return reading;
                    }

                    reading.Percent = percent;
                    reading.Level = BatteryMath.FromPercent(percent);
                    reading.Online = true;
                    reading.Detail = "2.4G";
                }
            }
            catch (Exception ex)
            {
                reading.Error = ex.Message;
            }

            return reading;
        }

        private static byte[] CreateBatteryQuery()
        {
            byte[] query = new byte[FrameSize];
            query[0] = ReportId;
            query[1] = CmdDeviceInfo;
            query[2] = TargetHeadset;
            return query;
        }

        private static byte[] WaitForBattery(HidSession session, int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                byte[] report = session.Read(150);
                if (report == null) continue;
                if (report.Length >= 4 &&
                    report[0] == ReportId &&
                    report[1] == CmdDeviceInfo &&
                    report[FieldIndex] == FieldBattery)
                {
                    return report;
                }
            }
            return null;
        }

        private static string ToHex(byte[] report)
        {
            StringBuilder text = new StringBuilder(report.Length * 3);
            for (int i = 0; i < report.Length; i++)
            {
                if (i != 0) text.Append(' ');
                text.Append(report[i].ToString("X2"));
            }
            return text.ToString();
        }
    }
}
