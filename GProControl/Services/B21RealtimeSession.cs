using System.ComponentModel;
using System.Runtime.InteropServices;
using GProControl.Models;
using GProControl.Protocol;

namespace GProControl.Services;

internal sealed partial class B21HidClient
{
    internal async Task<RealtimeSession> StartRealtimeAsync(CancellationToken cancellationToken = default)
    {
        if(!await RealtimeLease.WaitAsync(0,cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("另一个逐键灯效会话正在运行。");
        var timeout=TimeSpan.FromSeconds(2);
        byte[]? original=null;
        var locked=false;
        try
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false); locked=true;
            var version=await ExchangeAsync(6,null,timeout,cancellationToken).ConfigureAwait(false);
            if(version[5]!=1 || version[6]!=0x13) throw new InvalidOperationException("键盘硬件版本不匹配。");
            var captured=await ExchangeAsync(1,null,timeout,cancellationToken).ConfigureAwait(false);
            if(!KeyboardLightingCodec.TryParseCmd1Reply(captured,DateTime.Now,out var state) || state?.LedAreaMask!=0xB || state.EffectCode==0)
                throw new InvalidDataException("无法备份常规灯效，请先停止其他程序中的动态灯效。");
            original=captured;
            var target=(byte[])original.Clone(); target[5]=0; target[12]=0xB3;
            await ExchangeAsync(2,target,timeout,cancellationToken).ConfigureAwait(false);
            var actual=await ExchangeAsync(1,null,timeout,cancellationToken).ConfigureAwait(false);
            if (actual[5] != 0 || actual[12] != 0xB3)
                throw new InvalidDataException($"实时灯效模式未读回确认。状态：{Convert.ToHexString(actual.AsSpan(4, 9))}");
            var path=FindDevicePath() ?? throw new IOException("键盘已断开。");
            var handle=CreateFile(path,GenericRead|GenericWrite,FileShareRead|FileShareWrite,IntPtr.Zero,OpenExisting,FileFlagOverlapped,IntPtr.Zero);
            if(handle.IsInvalid) {var error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);}
            try {return new RealtimeSession(this,original,new FileStream(handle,FileAccess.ReadWrite,65,isAsync:true));}
            catch {handle.Dispose();throw;}
        }
        catch
        {
            try {if(original is not null) await RestoreStateAsync(original,timeout,"realtime entry").ConfigureAwait(false);}
            finally {RealtimeLease.Release();}
            throw;
        }
        finally {if(locked) _commandLock.Release();}
    }

    internal sealed class RealtimeSession(B21HidClient client,byte[] original,FileStream stream) : IAsyncDisposable
    {
        private int _disposed;
        private long _lastHeartbeat;
        public long FramesWritten {get;private set;}
        public async Task WriteAsync(IReadOnlyList<KeyboardPixel> pixels,CancellationToken cancellationToken=default)
        {
            var packets=KeyboardPixelCodec.Encode(pixels);
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed!=0,this);
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                if(Environment.TickCount64-_lastHeartbeat>=1000)
                {
                    // Native EnterEditMode sends empty CMD 0 with write-only OperatorType 1.
                    var heartbeat=new byte[65];heartbeat[1]=0x57;heartbeat[2]=0xAB;
                    await stream.WriteAsync(heartbeat,deadline.Token).ConfigureAwait(false);
                    _lastHeartbeat=Environment.TickCount64;
                }
                foreach(var packet in packets) await stream.WriteAsync(packet,deadline.Token).ConfigureAwait(false);
                FramesWritten++;
            }
            finally {_commandLock.Release();}
        }
        public async ValueTask DisposeAsync()
        {
            await _commandLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if(Interlocked.Exchange(ref _disposed,1)!=0) return;
                try
                {
                    try {await stream.DisposeAsync().ConfigureAwait(false);}
                    finally
                    {
                        // Even a handle-close error must not skip restoring the captured state.
                        // Restoration is independent of animation cancellation. Frames never use CMD 5.
                        await client.RestoreStateAsync(original,TimeSpan.FromSeconds(2),"realtime exit").ConfigureAwait(false);
                    }
                }
                finally {RealtimeLease.Release();}
            }
            finally {_commandLock.Release();}
        }
    }
}
