namespace VrcTranslator;

public enum TriggerSource { Keyboard, Vr }
public enum TriggerCommand { None, Start, Submit, Cancel }

public readonly record struct TriggerState(bool Available, bool Held, bool Neutral)
{
    public static TriggerState Digital(bool held) => new(true, held, !held);
    public static TriggerState Pair(bool available, bool a, bool b) => new(available, a && b, !a && !b);
}

public sealed class TriggerGate
{
    private readonly Edge keyboard = new();
    private readonly Edge vr = new();
    private TimeSpan stopAt;
    private bool capturing;
    public TriggerSource? Owner { get; private set; }
    public bool Capturing => capturing;

    public TriggerCommand Poll(TriggerState keys, TriggerState controller, TimeSpan now, TimeSpan limit)
    {
        bool keyboardPressed = keyboard.Observe(keys);
        bool vrPressed = vr.Observe(controller);
        if (Owner is { } owner)
        {
            if (!capturing) return TriggerCommand.None;
            var input = owner == TriggerSource.Keyboard ? keys : controller;
            if (!input.Available)
            {
                capturing = false;
                return TriggerCommand.Cancel;
            }
            if (!input.Held || now >= stopAt)
            {
                capturing = false;
                return TriggerCommand.Submit;
            }
            return TriggerCommand.None;
        }
        if (!keyboardPressed && !vrPressed) return TriggerCommand.None;
        Owner = keyboardPressed ? TriggerSource.Keyboard : TriggerSource.Vr;
        capturing = true;
        stopAt = now + limit;
        return TriggerCommand.Start;
    }

    public void Complete()
    {
        capturing = false;
        Owner = null;
    }

    public void Reset(TriggerSource source)
    {
        (source == TriggerSource.Keyboard ? keyboard : vr).Reset();
    }

    private sealed class Edge
    {
        private bool ready;
        private bool needsNeutral = true;

        public bool Observe(TriggerState input)
        {
            if (!input.Available) { Reset(); return false; }
            if (!input.Held)
            {
                if (input.Neutral) needsNeutral = false;
                ready = !needsNeutral;
                return false;
            }
            bool rising = ready;
            ready = false;
            return rising;
        }

        public void Reset()
        {
            needsNeutral = true;
            ready = false;
        }
    }
}
