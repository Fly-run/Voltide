using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// EWEADN (前行者) 8K Nano — Compx-family 2.4G combo receiver.
    ///
    /// Dongle: VID 0x3554, PID 0xFB35, product string "EWEADN 8K Nano",
    /// manufacturer "Compx". This is the same CompX chipset family as the ATK A9
    /// Air, VXE R1 and IMECOO P1 mice, so the framing is byte-for-byte identical;
    /// only the ids differ. The receiver exposes a keyboard interface (MI_02) and
    /// a mouse interface (MI_00) in addition to the vendor battery channel.
    ///
    ///   channel : vendor collection, usage page 0xFF02, input and output report
    ///             length 17. Report id 0x08 is the writable id.
    ///
    ///   query : 17 bytes
    ///     [0]      = 0x08  report id
    ///     [1]      = 0x04  opcode: battery query
    ///     [2..15]  = 0x00  body
    ///     [16]     = checksum, sum(frame[1..16]) &amp; 0xFF == 0x4D -> 0x49
    ///
    ///   reply : 17 bytes, read back from the same handle.
    ///           Verified live capture (2026-11-20, 10% discharging):
    ///
    ///     08 04 00 00 00 02 0A 00 0D 76 00 00 00 00 00 00 BA
    ///                       ^^ ^^ ^^^^^                  ^^
    ///                       |  |  |                      +-- checksum (0x4D)
    ///                       |  |  +------------------------- millivolts, big endian
    ///                       |  |                             (0x0D76 = 3446 mV)
    ///                       |  +---------------------------- charging: 0x01 yes, 0x00 no
    ///                       +------------------------------- percent (0x0A = 10)
    ///
    ///   [2] is the status byte (0x00 ok, 0x01 rejected).
    ///
    ///   Transport: WriteFile (interrupt OUT) answers on this dongle, with
    ///   HidD_SetOutputReport kept as a fallback for firmware that only answers
    ///   that way.
    /// </summary>
    public class Eweadn8KNanoProvider : IDeviceProvider
    {
        private const ushort EweadnVid = 0x3554;
        private const ushort EightKNanoPid = 0xFB35;
        private const ushort VendorPage = 0xFF02;

        private const int FrameSize = 17;
        private const byte ReportId = 0x08;
        private const byte CmdBattery = 0x04;
        private const byte FrameSum = 0x4D;

        private const int PercentIndex = 6;
        private const int ChargingIndex = 7;
        private const int MillivoltIndex = 8;

        public string Name { get { return "EWEADN 8K Nano"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            Dictionary<string, HidDeviceInfo> candidates = new Dictionary<string, HidDeviceInfo>();

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != EweadnVid) continue;
                if (h.ProductId != EightKNanoPid) continue;
                if (h.UsagePage != VendorPage) continue;
                if (h.InputReportLength != FrameSize) continue;
                if (h.OutputReportLength != FrameSize) continue;

                string key = h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                if (!candidates.ContainsKey(key)) candidates.Add(key, h);
            }

            foreach (KeyValuePair<string, HidDeviceInfo> kv in candidates)
            {
                HidDeviceInfo h = kv.Value;

                DeviceInfo d = new DeviceInfo();
                d.Id = "eweadn-8knano-" + kv.Key;
                d.Brand = "EWEADN";
                d.Model = "8K Nano";
                d.Name = "EWEADN 8K Nano";
                d.ShortModel = "8K Nano";
                d.Kind = DeviceKind.Mouse;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;
                found.Add(d);
            }
            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;

            try
            {
                byte[] cmd = NewQuery();

                using (HidSession s = new HidSession(device.HidPath, FrameSize, FrameSize))
                {
                    s.Flush(30);

                    byte[] resp = null;

                    for (int attempt = 0; attempt < 3 && resp == null; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(120);

                        bool sent = s.Write(cmd, 400) || s.SetOutputReport(cmd);
                        if (!sent)
                        {
                            r.Error = "发送失败（设备被占用？）";
                            return r;
                        }

                        resp = WaitFor(s, ReportId, CmdBattery, 700);
                    }

                    if (resp == null)
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠";
                        return r;
                    }

                    r.Raw = Hex(resp);

                    if (resp.Length <= MillivoltIndex + 1)
                    {
                        r.Online = true;
                        r.Percent = null;
                        r.Error = "读数异常";
                        return r;
                    }

                    if (resp[2] != 0x00)
                    {
                        // status 0x01 = the dongle rejected the opcode
                        r.Online = true;
                        r.Percent = null;
                        r.Error = "读数异常";
                        return r;
                    }

                    int level = resp[PercentIndex];
                    if (level > 100)
                    {
                        r.Online = true;
                        r.Percent = null;
                        r.Error = "读数异常";
                        return r;
                    }

                    int millivolts = (resp[MillivoltIndex] << 8) | resp[MillivoltIndex + 1];

                    r.Percent = level;
                    r.Level = BatteryMath.FromPercent(level);
                    r.Charging = resp[ChargingIndex] == 0x01;
                    r.ChargingKnown = true;
                    r.Online = true;
                    r.Detail = millivolts > 0 ? "2.4G " + millivolts + " mV" : "2.4G";
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        private static byte[] NewQuery()
        {
            byte[] cmd = new byte[FrameSize];
            cmd[0] = ReportId;
            cmd[1] = CmdBattery;
            cmd[FrameSize - 1] = Checksum(cmd);
            return cmd;
        }

        private static byte Checksum(byte[] frame)
        {
            int sum = 0;
            for (int i = 1; i < frame.Length - 1; i++) sum += frame[i];
            return (byte)((FrameSum - sum) & 0xFF);
        }

        private static byte[] WaitFor(HidSession s, byte rid, byte cmd, int timeoutMs)
        {
            for (int i = 0; i < 12; i++)
            {
                byte[] p = s.Read(Math.Max(60, timeoutMs / 4));
                if (p == null) return null;
                if (p.Length > MillivoltIndex + 1 && p[0] == rid && p[1] == cmd) return p;
            }
            return null;
        }

        private static string Hex(byte[] b)
        {
            if (b == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < b.Length; i++)
            {
                sb.Append(b[i].ToString("X2"));
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }
    }
}
