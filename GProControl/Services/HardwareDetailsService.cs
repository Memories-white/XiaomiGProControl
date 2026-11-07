using System.Management;
using GProControl.Models;

namespace GProControl.Services;

public sealed class HardwareDetailsService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static HardwareDetails? _cached;
    public async Task<HardwareDetails> CaptureAsync(bool refresh = false)
    {
        await Gate.WaitAsync();
        try
        {
            if (!refresh && _cached is { } cached && DateTime.Now - cached.ObservedAt < TimeSpan.FromMinutes(2)) return cached;
            return _cached = await Task.Run(Capture);
        }
        finally { Gate.Release(); }
    }

    private static HardwareDetails Capture()
    {
        var sections = new List<HardwareDetailSection>();
        string serial = "未提供";
        Add("整机与固件", "Win32_ComputerSystemProduct", ["Vendor","Name","IdentifyingNumber"], item => {
            serial = Text(item,"IdentifyingNumber");
            return [new("制造商",Text(item,"Vendor")),new("机型",Text(item,"Name"))];
        });
        Add("BIOS", "Win32_BIOS", ["Manufacturer","SMBIOSBIOSVersion","ReleaseDate"], item =>
            [new("厂商",Text(item,"Manufacturer")),new("版本",Text(item,"SMBIOSBIOSVersion")),
             new("发布日期",Date(item,"ReleaseDate"))]);
        Add("处理器", "Win32_Processor", ["Name","NumberOfCores","NumberOfLogicalProcessors","MaxClockSpeed"], item =>
            [new("型号",Text(item,"Name")),new("核心 / 线程",$"{Text(item,"NumberOfCores")} / {Text(item,"NumberOfLogicalProcessors")}"),
             new("WMI 标称最高频率",Unit(item,"MaxClockSpeed","MHz"))]);
        Add("显卡", "Win32_VideoController", ["Name","DriverVersion","CurrentHorizontalResolution","CurrentVerticalResolution","CurrentRefreshRate"], item => {
            var rows = new List<HardwareDetailRow> {new("型号",Text(item,"Name")),new("驱动版本",Text(item,"DriverVersion"))};
            var gpu=NvidiaGpuTelemetry.TryCapture();
            if (gpu?.Name == Text(item,"Name") && gpu.TotalMemoryBytes is ulong bytes) rows.Add(new("独立显存",Capacity(bytes)));
            if (Number(item,"CurrentHorizontalResolution") > 0) rows.Add(new("当前输出",$"{Text(item,"CurrentHorizontalResolution")} × {Text(item,"CurrentVerticalResolution")} · {Unit(item,"CurrentRefreshRate","Hz")}"));
            return rows;
        });
        Add("内存模块", "Win32_PhysicalMemory", ["DeviceLocator","Manufacturer","Capacity","ConfiguredClockSpeed","SMBIOSMemoryType","PartNumber"], item =>
            [new("插槽",Text(item,"DeviceLocator")),new("厂商 / 型号",$"{Text(item,"Manufacturer")} · {Text(item,"PartNumber")}"),
             new("容量",Capacity(Number(item,"Capacity"))),new("类型",Number(item,"SMBIOSMemoryType") switch {26=>"DDR4",34=>"DDR5",30=>"LPDDR4",35=>"LPDDR5",_=>"未识别"}),
             new("配置速率",Unit(item,"ConfiguredClockSpeed","MT/s"))]);
        Add("物理磁盘", "Win32_DiskDrive", ["Model","Size","InterfaceType","MediaType"], item =>
            [new("型号",Text(item,"Model")),new("容量",Capacity(Number(item,"Size"))),new("WMI 接口类型",Text(item,"InterfaceType")),new("介质",Text(item,"MediaType"))]);
        Add("操作系统", "Win32_OperatingSystem", ["Caption","Version","BuildNumber","OSArchitecture"], item =>
            [new("系统",Text(item,"Caption")),new("版本 / 构建",$"{Text(item,"Version")} / {Text(item,"BuildNumber")}"),new("架构",Text(item,"OSArchitecture"))]);
        Add("显示器", "WmiMonitorBasicDisplayParams", ["InstanceName","MaxHorizontalImageSize","MaxVerticalImageSize"], item =>
            [new("设备",Text(item,"InstanceName")),new("物理尺寸",Size(item))], "root\\wmi");
        return new(sections, serial, DateTime.Now);

        void Add(string title, string cls, string[] fields, Func<ManagementObject,IReadOnlyList<HardwareDetailRow>> format, string scope="root\\cimv2")
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, "SELECT "+string.Join(',',fields)+" FROM "+cls);
                searcher.Options.Timeout=TimeSpan.FromSeconds(4);
                using var result=searcher.Get(); var index=0;
                foreach(ManagementObject item in result)
                {
                    using(item) sections.Add(new(index++ == 0 ? title : $"{title} {index}",format(item)));
                }
                if(index==0) sections.Add(new(title,[new("状态","未检测到或设备未提供") ]));
            }
            catch(Exception exception) when(exception is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            { sections.Add(new(title,[new("状态","当前无法读取") ])); }
        }
    }

    private static string Text(ManagementObject item,string name) => Convert.ToString(item[name])?.Trim() is {Length:>0} text ? text : "未提供";
    private static ulong Number(ManagementObject item,string name) => ulong.TryParse(Convert.ToString(item[name]),out var value) ? value : 0;
    private static string Unit(ManagementObject item,string name,string unit) => Number(item,name) is >0 and var value ? $"{value} {unit}" : "未提供";
    private static string Capacity(ulong bytes) => bytes>0 ? $"{bytes/1073741824d:0.##} GiB" : "未提供";
    private static string Date(ManagementObject item,string name) { try {return ManagementDateTimeConverter.ToDateTime(Text(item,name)).ToString("yyyy-MM-dd");} catch(ArgumentException) {return "未提供";} }
    private static string Size(ManagementObject item)
    {
        var width=Number(item,"MaxHorizontalImageSize"); var height=Number(item,"MaxVerticalImageSize");
        return width>0 && height>0 ? $"{width} × {height} cm · {Math.Sqrt(width*width+height*height)/2.54:0.0} 英寸" : "设备未提供 EDID 尺寸";
    }
}
