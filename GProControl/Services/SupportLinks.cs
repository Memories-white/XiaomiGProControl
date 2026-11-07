namespace GProControl.Services;

public sealed record SupportLink(string Title, string Description, string Address);

public static class SupportLinks
{
    public static IReadOnlyList<SupportLink> All { get; } =
    [
        new("使用手册","小米笔记本官方使用说明","http://pc.mi.com/sysc.html"),
        new("售后政策","查看笔记本保修及服务条款","https://www.mi.com/service/serviceAgreement?id=4"),
        new("服务网点","查找附近的小米授权服务网点","https://www.mi.com/service/sitelist"),
        new("官方服务与客服","售后申请、咨询和服务查询","https://www.mi.com/service"),
        new("驱动下载","Redmi G Pro 2024 官方驱动页面","https://www.mi.com/service/notebook/drivers/B21"),
        new("反馈本软件问题","打开 GitHub，由你填写并提交反馈","https://github.com/Memories-white/XiaomiGProControl/issues/new")
    ];
}
