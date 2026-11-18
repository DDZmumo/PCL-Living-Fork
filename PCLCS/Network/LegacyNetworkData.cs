using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using MeloongCore.Extensions;
using PCLCS.Loaders;

namespace PCLCS.Network;
public class LegacyNetSourceData<TThread>
    where TThread : class
{
    public int Id;
    public string Url = null !;
    public int FailCount;
    public Exception Ex = null !;
    public TThread SingleThread = null !;
    public bool IsFailed;
    public override string ToString() => Url;
}

public abstract class LegacyNetThreadData<TFile, TSource, TThread, TNetState> : IEnumerable<TThread>, IEquatable<TThread> where TFile : class, ILegacyNetFileTiming where TThread : LegacyNetThreadData<TFile, TSource, TThread, TNetState> where TNetState : struct, Enum
{
    private readonly Func<long> clock;
    protected LegacyNetThreadData(Func<long> clock, TNetState waitingState)
    {
        this.clock = clock;
        SpeedLastTime = clock();
        InitTime = clock();
        State = waitingState;
    }

    public TFile Task = null !;
    public Thread Thread = null !;
    internal IDisposable? IoLease;
    public TThread NextThread = null !;
    private IEnumerable<TThread> Next()
    {
        TThread current = (TThread)this;
        while (current is not null)
        {
            yield return current;
            current = current.NextThread;
        }
    }

    public IEnumerator<TThread> GetEnumerator() => Next().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => Next().GetEnumerator();
    public int Uuid;
    public bool IsFirstThread => DownloadStart == 0 && Task.CoreFileSize == -2;

    public string Temp = null !;
    public long DownloadStart;
    public long DownloadEnd
    {
        get
        {
            lock (Task.CoreLockChain)
            {
                if (NextThread is null)
                    return Task.CoreUnknownSize ? 1000 * 1000 * 1000 * 1000L : checked(Task.CoreFileSize - 1);
                return checked(NextThread.DownloadStart - 1);
            }
        }
    }

    public long DownloadUndone => checked(DownloadEnd - (DownloadStart + DownloadDone) + 1);

    public long DownloadDone;
    private long SpeedLastTime, SpeedLastDone, speed;
    public long Speed
    {
        get
        {
            checked
            {
                if (clock() - SpeedLastTime > 200)
                {
                    long delta = clock() - SpeedLastTime;
                    speed = Convert.ToInt64((DownloadDone - SpeedLastDone) / (delta / 1000d));
                    SpeedLastDone = DownloadDone;
                    SpeedLastTime += delta;
                }

                return speed;
            }
        }
    }

    public long InitTime;
    public long LastReceiveTime = -1;
    public TNetState State;
    public bool IsEnded => Convert.ToInt32(State) == 5 || Convert.ToInt32(State) == 6;

    public TSource Source = default !;
    public bool Equals(TThread? other) => other is not null && Uuid == other.Uuid;
    public override bool Equals(object? value) => value is TThread other && Equals(other);
    // LF uses UUID equality with Object's original identity hash.
    public override int GetHashCode() => base.GetHashCode();
}
