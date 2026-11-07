using GProControl.Models;
using GProControl.Services;

var fixture=new HardwareDetails([new("CPU",[new("型号","Fixture CPU")])],"PRIVATE-SERIAL-TEST",DateTime.Now);
if(fixture.ToReport().Contains("PRIVATE-SERIAL-TEST") || !fixture.ToReport(true).Contains("PRIVATE-SERIAL-TEST"))
    throw new InvalidOperationException("Report serial privacy failed.");
if(!fixture.ToReport().Contains("Fixture CPU")) throw new InvalidOperationException("Report is empty.");
foreach(var link in SupportLinks.All)
{
    var uri=new Uri(link.Address);
    if(uri.Scheme is not "http" and not "https" || !new[]{"www.mi.com","pc.mi.com","github.com"}.Contains(uri.Host))
        throw new InvalidOperationException("Unexpected service link.");
}
Console.WriteLine("Report privacy and support link checks passed.");
if(args.Contains("--hardware"))
{
    var service=new HardwareDetailsService();
    var details=await service.CaptureAsync(true);
    if(details.Sections.Count<8 || details.Sections.All(section=>section.Rows.Any(row=>row.Value=="当前无法读取")))
        throw new InvalidOperationException("Hardware details could not be read.");
    if(!ReferenceEquals(details,await service.CaptureAsync())) throw new InvalidOperationException("Details cache failed.");
    foreach(var section in details.Sections) Console.WriteLine($"section={section.Title}; fields={section.Rows.Count}");
    Console.WriteLine("hardware-details=True; cache=True; serialExcluded=True");
}
