using System.ComponentModel;
using System.Runtime.InteropServices;
using GProControl.Models;
using GProControl.Protocol;

namespace GProControl.Services;

internal static class ScreenLightingSampler
{
    public static KeyboardPixel[] Capture(int samples)
    {
        if(samples is <1 or >5) throw new ArgumentOutOfRangeException(nameof(samples));
        var width=KeyboardPixelCodec.Columns*samples; var height=KeyboardPixelCodec.Rows*samples;
        var desktop=GetDC(IntPtr.Zero);
        if(desktop==IntPtr.Zero) throw new IOException("无法读取当前桌面。");
        IntPtr memory=IntPtr.Zero,bitmap=IntPtr.Zero,previous=IntPtr.Zero;
        try
        {
            memory=CreateCompatibleDC(desktop);
            if(memory==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info=new BitmapInfo {Size=40,Width=width,Height=-height,Planes=1,BitCount=32};
            bitmap=CreateDIBSection(desktop,ref info,0,out var data,IntPtr.Zero,0);
            if(bitmap==IntPtr.Zero || data==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous=SelectObject(memory,bitmap);
            if(previous==IntPtr.Zero || previous==new IntPtr(-1)) throw new IOException("无法初始化屏幕采样。");
            SetStretchBltMode(memory,4); SetBrushOrgEx(memory,0,0,IntPtr.Zero);
            if(!StretchBlt(memory,0,0,width,height,desktop,0,0,GetSystemMetrics(0),GetSystemMetrics(1),0x00CC0020))
                throw new IOException("当前桌面无法读取，已停止逐键屏幕联动。");
            GdiFlush();
            var bytes=new byte[width*height*4]; Marshal.Copy(data,bytes,0,bytes.Length);
            var pixels=new KeyboardPixel[KeyboardPixelCodec.PixelCount];
            for(var row=0;row<KeyboardPixelCodec.Rows;row++)
            for(var col=0;col<KeyboardPixelCodec.Columns;col++)
            {
                int r=0,g=0,b=0;
                for(var y=0;y<samples;y++) for(var x=0;x<samples;x++)
                {
                    var offset=((row*samples+y)*width+col*samples+x)*4;
                    b+=bytes[offset];g+=bytes[offset+1];r+=bytes[offset+2];
                }
                var count=samples*samples;var index=row*KeyboardPixelCodec.Columns+col;
                pixels[index]=new((byte)index,(byte)(r/count),(byte)(g/count),(byte)(b/count));
            }
            return pixels;
        }
        finally
        {
            if(memory!=IntPtr.Zero && previous!=IntPtr.Zero && previous!=new IntPtr(-1)) SelectObject(memory,previous);
            if(bitmap!=IntPtr.Zero) DeleteObject(bitmap);
            if(memory!=IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero,desktop);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {public uint Size;public int Width,Height;public ushort Planes,BitCount;public uint Compression,ImageSize;public int XPels,YPels;public uint Used,Important,Colors;}
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc,int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc,int x,int y,IntPtr previous);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr destination,int x,int y,int width,int height,IntPtr source,int sx,int sy,int sw,int sh,uint operation);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
