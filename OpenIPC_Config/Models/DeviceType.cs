using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;

namespace OpenIPC_Config.Models;

[JsonConverter(typeof(StringEnumConverter))]
public enum DeviceType
{
    None,
    Camera,
    Radxa
}
// public enum DeviceType
// {
//     None,
//     Camera,
//     Radxa,
//     NVR
// }


public static class DevicesFriendlyNames
{
    static DevicesFriendlyNames()
    {
        // Keep the values unique too in the dictionaries below as we do reverse searches too in order to build unique firmware filenames
        
        mappingsManufacturers =  new Dictionary<string, string>(){
            {"openipc", "OpenIPC"},
            {"emax", "EMax"},
            {"runcam", "RunCam"},
            {"caddx", "Caddx"}
        };
        mappingsDevices =  new Dictionary<string, string>(){
            {"mario-aio", "OpenIPC Mario AIO"},
            {"thinker-aio", "OpenIPC Thinker AIO"},
            {"urllc-aio", "OpenIPC Generic SSC338"},
            {"wifilink", "WiFi Link"},
            {"wyvern-link", "Wyvern Link"}
        };
    }

    public static string ManufacturerByFriendlyName(string firendlyName)
    {
        foreach (string keyVar in mappingsManufacturers.Keys)
        {
            if (mappingsManufacturers[keyVar] == firendlyName)
            {
                return keyVar;
            }
        }
        return firendlyName;
    }

    public static string DeviceByFriendlyName(string firendlyName)
    {
        foreach (string keyVar in mappingsDevices.Keys)
        {
            if (mappingsDevices[keyVar] == firendlyName)
            {
                return keyVar;
            }
        }
        return firendlyName;
    }

    public static Dictionary<string, string> mappingsManufacturers;
    public static Dictionary<string, string> mappingsDevices;

}
