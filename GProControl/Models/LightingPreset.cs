namespace GProControl.Models;

public sealed record LightingColorStop(string Color, double Offset);
public sealed record LightingPalette(byte Code, string Name, string PreviewColor,
    IReadOnlyList<LightingColorStop>? Stops = null);

public sealed record LightingPreset(int EffectId, string Name, string Summary,
    LightingProfile Profile, string PreviewColor)
{
    public bool IsHardwarePreset => EffectId is not 21 and not 22;
}

/// <summary>Factory defaults recovered from the vendor frontend, mapped to B21 fields.</summary>
public static class LightingPresetCatalog
{
    public static IReadOnlyList<LightingPalette> AudioPalettes { get; } =
    [new(0,"频谱渐变 1","#FFFF0000",[new("#FFFF0000",0),new("#FFFFFF0A",.2134),new("#FF00FF14",.3608),new("#FF05FFFF",.5466),new("#FF0066FF",.7866),new("#FFFF02FF",1)]),
     new(1,"频谱渐变 2","#FF05FFFF",[new("#FF05FFFF",0),new("#FFFF00FF",.3068),new("#FFFF0100",.666),new("#FFEBFF00",1)]),
     new(2,"频谱渐变 3","#FF14FF14",[new("#FF14FF14",0),new("#FF14FFFF",.1927),new("#FF0028FF",.3914),new("#FFFF32FF",.5747),new("#FFFF1414",.7856),new("#FFFFFF0A",1)]),
     new(3,"频谱渐变 4","#FF00FFD1",[new("#FF00FFD1",0),new("#FF00FF66",.2015),new("#FF00FF19",.3842),new("#FF52FF00",.578),new("#FF8FFF00",.7719),new("#FFBDFF00",1)])];
    public static IReadOnlyList<LightingPalette> SolidPalettes { get; } =
    [new(0,"红色","#FFFF0000"), new(1,"橙色","#FFFF5000"), new(2,"黄色","#FFFFC800"),
     new(3,"绿色","#FF00FF00"), new(4,"青色","#FF00FFB9"), new(5,"蓝色","#FF0000FF"),
     new(6,"粉色","#FFFF00B9")];
    public static IReadOnlyList<LightingPalette> GradientPalettes { get; } =
    [new(0,"渐变 1","#FFFF0000",[new("#FFFF0000",0),new("#FFFFC800",.3257),new("#FF00FF00",.6569),new("#FF0000FF",1)]),
     new(1,"渐变 2","#FF00FFB9",[new("#FF00FFB9",0),new("#FF0000FF",.5185),new("#FFFF00B9",1)]),
     new(2,"渐变 3","#FFFFC800",[new("#FFFFC800",0),new("#FFFF4800",.3125),new("#FFFF0000",.5277),new("#FFBF4000",.6458),new("#FF00FF00",1)])];

    public static IReadOnlyList<LightingPreset> All { get; } =
    [
        new(22,"光影随行","基础屏幕联动",new(11,255,0,2,1,0,"7F7F7F"),"#FF4FA3FF"),
        new(21,"光音视界","17 段频谱 · 统一混色",new(11,255,0,2,1,0,"7F0000"),"#FFBA7AFF"),
        new(23,"柔光","白色柔光",new(11,255,0,2,1,0,"7F7F7F"),"#FFFFFFFF"),
        new(7,"彩虹螺旋","顺时针 · 速度 2 档",new(7,0,1,2,2),"#FFFF8B4F"),
        new(5,"触发","红色按键响应",new(5,0,0,2,1),"#FFFF0000"),
        new(3,"涟漪","红色 · 正常宽度",new(3,0,0,2,2,1),"#FFFF0000"),
        new(6,"色波","向右 · 正常宽度",new(6,0,0,2,2,1),"#FF4FCF8B"),
        new(10,"萤火","红色 · 原厂亮度",new(10,0,0,3,1),"#FFFF0000"),
        new(8,"贪吃蛇","速度 2 档 · 长",new(8,0,0,2,2,1),"#FF58D78E"),
        new(1,"波浪","向右 · 速度 2 档",new(1,0,1,2,2),"#FF4F8CFF"),
        new(2,"呼吸","红色渐隐渐现",new(2,0,0,2,1),"#FFFF0000"),
        new(11,"常亮","红色常亮",new(11,0,0,2,1),"#FFFF0000")
    ];

    public static LightingPreset ForEffect(int id) => All.First(preset => preset.EffectId == id);
    public static IReadOnlyList<LightingPalette> PalettesFor(int id) => id is 2 or 3 or 5 or 10 or 11
        ? SolidPalettes : GradientPalettes;
    public static bool SupportsSolidPalette(byte effect) => effect is 2 or 3 or 5 or 10 or 11;
}
