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
    /// manufacturer "C-Media Electronics Inc.".
    ///
    /// Front-end protocol from the official HUB renderer bundle:
    ///
    ///   report id 0x55; query 64 bytes: 55 65 01 00 ...
    ///   reply (input report)          : 55 65 PP SS ...
    ///                 ^^ ^^ ^^
    ///                 |  |  +-- 0x02 discharging, 0x03 charging, 0x04 full, 0x1A sleep
    ///                 |  +----- battery percentage
    ///                 +-------- report id / message type
    ///
    /// Preparsed-data map on this Windows machine (six collections, MI_00):
    ///   FF82 : input/output report 0x41
    ///   FF90 : input report 0x55 only, NO output report
    ///   FF22 : feature report 0xAA
    /// The HID surface exposes no output report carrying 0x55, so the generic
    /// HID transports below cannot reach the firmware. The official HUB reads
    /// battery through the vendor audio SDK osConfLib.dll (koffi + C-Media
    /// driver PropertyControl), whose protocol lives in protected bytecode.
    /// This provider stays as an experimental enumeration stub: it still walks
    /// every candidate HID path (interrupt OUT, control output report, feature)
    /// and reports Sleeping when nothing answers.
    /// </summary>
    public class MchoseV9ProProvider : IDeviceProvider
    {
        private const ushort V9ProVid = 0x291D;
        private const ushort V9ProPid = 0x385D;

        private const ushort PageFf90 = 0xFF90;
        private const ushort PageFf82 = 0xFF82;
        private const ushort PageFf22 = 0xFF22;

        private const int FrameSize = 64;
        private const byte ReportId = 0x55;
        private const byte CmdDeviceInfo = 0x65;
        private const byte TargetHeadset = 0x01;

        private const int PercentIndex = 2;
        private const int StateIndex = 3;
        private const byte StateDischarging = 0x02;
        private const byte StateCharging = 0x03;
        private const byte StateFull = 0x04;
        private const byte StateSleep = 0x1A;

        private sealed class Candidate
        {
            public string Path;
            public ushort UsagePage;
            public int InLen;
            public int OutLen;
            public int FeatLen;
        }

        private readonly List<Candidate> _candidates = new List<Candidate>();

        public string Name { get { return "MCHOSE V9 Pro"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            _candidates.Clear();
            if (catalog == null) return found;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HidDeviceInfo hid in catalog)
            {
                if (hid == null) continue;
                if (hid.VendorId != V9ProVid || hid.ProductId != V9ProPid) continue;
                if (string.IsNullOrEmpty(hid.Path) || !seen.Add(hid.Path)) continue;

                bool vendorIo = (hid.UsagePage == PageFf90 || hid.UsagePage == PageFf82)
                    && hid.InputReportLength >= FrameSize && hid.OutputReportLength >= FrameSize;
                bool vendorFeature = hid.UsagePage == PageFf22 && hid.FeatureReportLength >= FrameSize;
                if (!vendorIo && !vendorFeature) continue;

                _candidates.Add(new Candidate
                {
                    Path = hid.Path,
                    UsagePage = hid.UsagePage,
                    InLen = hid.InputReportLength,
                    OutLen = hid.OutputReportLength,
                    FeatLen = hid.FeatureReportLength
                });
            }

            if (_candidates.Count == 0) return found;

            Candidate first = _candidates[0];
            DeviceInfo device = new DeviceInfo();
            device.Id = "mchose-v9-pro-" + V9ProPid.ToString("X4");
            device.Brand = "MCHOSE";
            device.Model = "V9 Pro";
            device.Name = "MCHOSE V9 Pro";
            device.ShortModel = "V9 Pro";
            device.Kind = DeviceKind.Headset;
            device.VendorId = V9ProVid;
            device.ProductId = V9ProPid;
            device.HidPath = first.Path;
            device.ProviderName = Name;
            found.Add(device);
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
                foreach (Candidate candidate in _candidates)
                {
                    DeviceReading reply = TryCandidate(candidate);
                    if (reply != null) return reply;
                }

                reading.Sleeping = true;
                reading.Error = "耳机休眠或未连接";
                return reading;
            }
            catch (Exception ex)
            {
                reading.Error = ex.Message;
                return reading;
            }
        }

        private DeviceReading TryCandidate(Candidate candidate)
        {
            int ioLen = Math.Max(candidate.InLen, candidate.OutLen);
            int openLen = Math.Max(ioLen, candidate.FeatLen);
            if (openLen <= 0) openLen = FrameSize;

            using (HidSession session = new HidSession(candidate.Path, openLen, openLen))
            {
                session.Flush(30);
                byte[] query = CreateBatteryQuery();

                // 1) Interrupt OUT (WriteFile), then listen on the IN pipe.
                if (candidate.OutLen >= FrameSize)
                {
                    if (session.Write(query, 250))
                    {
                        byte[] reply = WaitForBattery(session, 1100);
                        DeviceReading parsed = Parse(reply);
                        if (parsed != null) return parsed;
                    }

                    // 2) Control output report (SET_OUTPUT_REPORT).
                    if (session.SetOutputReport(query))
                    {
                        byte[] reply = WaitForBattery(session, 1100);
                        DeviceReading parsed = Parse(reply);
                        if (parsed != null) return parsed;
                    }
                }

                // 3) Feature report: SET_FEATURE then GET_FEATURE.
                if (candidate.FeatLen >= FrameSize)
                {
                    if (HidNative.HidD_SetFeature(session.Handle, query, query.Length))
                    {
                        byte[] feature = new byte[FrameSize];
                        if (HidNative.HidD_GetFeature(session.Handle, feature, feature.Length))
                        {
                            DeviceReading parsed = Parse(feature);
                            if (parsed != null) return parsed;
                        }
                    }
                }
            }

            return null;
        }

        private static byte[] CreateBatteryQuery()
        {
            byte[] query = new byte[FrameSize];
            query[0] = ReportId;
            query[1] = CmdDeviceInfo;
            query[2] = TargetHeadset;
            return query;
        }

        private byte[] WaitForBattery(HidSession session, int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                byte[] report = session.Read(150);
                if (report == null) continue;
                if (report.Length >= 4 && report[0] == ReportId && report[1] == CmdDeviceInfo)
                {
                    return report;
                }
            }
            return null;
        }

        private static DeviceReading Parse(byte[] reply)
        {
            if (reply == null || reply.Length < 4) return null;
            if (reply[0] != ReportId || reply[1] != CmdDeviceInfo) return null;

            int percent = reply[PercentIndex];
            byte state = reply[StateIndex];

            DeviceReading reading = new DeviceReading();
            reading.UpdatedAt = DateTime.Now;
            reading.Raw = ToHex(reply);

            if (state == StateSleep)
            {
                reading.Sleeping = true;
                reading.Error = "耳机休眠";
                return reading;
            }

            if (percent > 100)
            {
                reading.Online = true;
                reading.Error = "电量读数异常";
                return reading;
            }

            if (state != StateDischarging && state != StateCharging && state != StateFull)
            {
                reading.Online = true;
                reading.Error = "充电状态无效";
                return reading;
            }

            if (state == StateFull) percent = 100;

            reading.Percent = percent;
            reading.Level = BatteryMath.FromPercent(percent);
            reading.Charging = state == StateCharging;
            reading.ChargingKnown = true;
            reading.Online = true;
            reading.Detail = state == StateCharging ? "2.4G 充电中" : "2.4G";
            return reading;
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
