using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace VrcTranslator;

public sealed class IncomingAudio(bool strongerNoiseFilter = false)
{
    public const int SampleRate = 16000;
    public const int FrameMilliseconds = 20;
    public const int FrameBytes = SampleRate * FrameMilliseconds / 1000 * sizeof(short);
    private const int OnsetFrames = 3;
    private const int LeadFrames = 300 / FrameMilliseconds + OnsetFrames - 1;
    private const int TailFrames = 160 / FrameMilliseconds;
    private const int IdleFrames = 5000 / FrameMilliseconds;
    private const int TaskFrames = 10000 / FrameMilliseconds;
    private static readonly TimeSpan MissingPacketTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly byte[] Silence = new byte[FrameBytes];
    private readonly int gateLevel = strongerNoiseFilter ? 260 : 130;
    private readonly Channel<Frame> frames = Channel.CreateBounded<Frame>(new BoundedChannelOptions(500)
    {
        SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
    });
    private long sequence;

    public void Push(byte[] pcm, long capturedAt)
    {
        if (pcm.Length != FrameBytes) throw new ArgumentException("Expected 20 ms of mono PCM.", nameof(pcm));
        frames.Writer.TryWrite(new(++sequence, pcm, capturedAt));
    }

    internal async Task<Utterance> WaitForSpeechAsync(long capturedSince, CancellationToken cancellation)
    {
        var lead = new Queue<Frame>(LeadFrames);
        long previous = 0;
        int audible = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var frame = await frames.Reader.ReadAsync(cancellation).ConfigureAwait(false);
            if (frame.CapturedAt < capturedSince) continue;
            if (previous + 1 != frame.Number)
            {
                lead.Clear();
                audible = 0;
            }
            previous = frame.Number;
            audible = IsAudible(frame.Pcm) ? audible + 1 : 0;
            if (audible == OnsetFrames) return new(lead.ToArray(), frame);
            if (lead.Count == LeadFrames) lead.Dequeue();
            lead.Enqueue(frame);
        }
    }

    internal async IAsyncEnumerable<byte[]> ReadSpeechAsync(Utterance utterance, [EnumeratorCancellation] CancellationToken cancellation)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        foreach (var frame in utterance.Lead) yield return frame.Pcm;
        yield return utterance.FirstAudible.Pcm;
        long previous = utterance.FirstAudible.Number;
        int silent = 0;
        for (int count = utterance.Lead.Length + 1; count < TaskFrames && silent < IdleFrames; count++)
        {
            Frame next;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                idle.CancelAfter(MissingPacketTimeout);
                next = await frames.Reader.ReadAsync(idle.Token).ConfigureAwait(false);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { yield break; }
            if (next.Number != previous + 1) throw new ServiceException("来话音频积压，已跳过当前片段。");
            previous = next.Number;
            silent = IsAudible(next.Pcm) ? 0 : silent + 1;
            // Keep quiet syllable tails, then pass true silence so the server can recognize the pause.
            yield return silent <= TailFrames ? next.Pcm : Silence;
        }
    }

    private bool IsAudible(byte[] pcm)
    {
        // A level gate at about -48 dBFS, or -42 dBFS with stronger filtering.
        var samples = MemoryMarshal.Cast<byte, short>(pcm);
        long energy = 0;
        foreach (short sample in samples) energy += sample * sample;
        return energy >= samples.Length * gateLevel * gateLevel;
    }

    internal readonly record struct Frame(long Number, byte[] Pcm, long CapturedAt);

    internal sealed record Utterance(Frame[] Lead, Frame FirstAudible)
    {
        public long StartTimestamp => (Lead.Length == 0 ? FirstAudible.CapturedAt : Lead[0].CapturedAt) -
            Stopwatch.Frequency * FrameMilliseconds / 1000;
    }
}
