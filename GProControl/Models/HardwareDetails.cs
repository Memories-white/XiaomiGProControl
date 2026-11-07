namespace GProControl.Models;

public sealed record HardwareDetailRow(string Label, string Value);
public sealed record HardwareDetailSection(string Title, IReadOnlyList<HardwareDetailRow> Rows);
public sealed record HardwareDetails(IReadOnlyList<HardwareDetailSection> Sections, string SerialNumber, DateTime ObservedAt)
{
    public string ToReport(bool includeSerial = false) =>
        $"G Pro Control · 设备参数 · {ObservedAt:yyyy-MM-dd HH:mm:ss}\n"
        + string.Join("\n\n", Sections.Select(section => section.Title + "\n"
            + string.Join("\n", section.Rows.Select(row => $"{row.Label}：{row.Value}"))))
        + (includeSerial ? $"\n\n序列号：{SerialNumber}" : "\n\n序列号未包含在此报告中。");
}
