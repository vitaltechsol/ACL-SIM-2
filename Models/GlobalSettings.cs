using System;

namespace ACL_SIM_2.Models
{
    public class GlobalSettings
    {
        public string ProsimIp { get; set; } = "127.0.0.1";
        public bool AutoConnectProsim { get; set; } = false;
        public bool AutoCenterOnStartup { get; set; } = false;
    }
}
