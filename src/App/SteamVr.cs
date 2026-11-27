using System.Diagnostics;
using System.Runtime.InteropServices;
using Valve.VR;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal readonly record struct VrStatus(bool Connected, bool Bound, TriggerState Trigger, string Text);

internal sealed class SteamVr(Action<string> log) : IDisposable
{
    private const string AppKey = "com.vrcvoicetranslator.overlay";
    private const string ActionSet = "/actions/translator";
    private readonly VRActiveActionSet_t[] activeSets = new VRActiveActionSet_t[1];
    private readonly InputBindingInfo_t[] bindings = new InputBindingInfo_t[32];
    private static readonly uint SetSize = (uint)Marshal.SizeOf<VRActiveActionSet_t>();
    private static readonly uint DigitalSize = (uint)Marshal.SizeOf<InputDigitalActionData_t>();
    private static readonly uint EventSize = (uint)Marshal.SizeOf<VREvent_t>();
    private static readonly uint OriginSize = (uint)Marshal.SizeOf<InputOriginInfo_t>();
    private static readonly uint BindingSize = (uint)Marshal.SizeOf<InputBindingInfo_t>();
    private CVRSystem? system;
    private CVRInput? input;
    private SubtitleOverlay? overlay;
    private ProcessExitHandle? server;
    private TimeSpan nextConnect;
    private TimeSpan nextSetup;
    private TimeSpan nextBindingCheck;
    private ulong chord;
    private ulong first;
    private ulong second;
    private bool chordBound;
    private bool firstBound;
    private bool secondBound;
    private bool leftConnected;
    private bool rightConnected;
    private bool usingFallback;
    private string? lastConnectError;
    private string? inputError;

    public bool Connected => system is not null;

    public VrStatus Poll(TimeSpan now, Settings settings)
    {
        try { return Update(now, settings); }
        catch (Exception exception)
        {
            Disconnect(now);
            ReportConnectError($"OpenVR 连接失败（{exception.GetType().Name} / 0x{exception.HResult:X8}）");
            return new(false, false, default, lastConnectError!);
        }
    }

    private VrStatus Update(TimeSpan now, Settings settings)
    {
        if (system is null && now >= nextConnect) Connect(now);
        if (system is null) return new(false, false, default, lastConnectError ?? "等待 SteamVR");
        if (server is not null && server.WaitOne(0))
        {
            Disconnect(now);
            return new(false, false, default, "SteamVR 已退出");
        }

        if (input is null && now >= nextSetup) SetupInput(now);

        bool bindingChanged = false;
        var ev = new VREvent_t();
        while (system.PollNextEvent(ref ev, EventSize))
        {
            var type = (EVREventType)ev.eventType;
            if (type == EVREventType.VREvent_Quit)
            {
                overlay?.Dispose();
                overlay = null;
                system.AcknowledgeQuit_Exiting();
                Disconnect(now);
                return new(false, false, default, "SteamVR 正在退出");
            }
            if (type is EVREventType.VREvent_Input_BindingLoadSuccessful or EVREventType.VREvent_Input_BindingsUpdated or
                EVREventType.VREvent_Input_ActionManifestReloaded or EVREventType.VREvent_Input_BindingLoadFailed or
                EVREventType.VREvent_Input_ActionManifestLoadFailed) bindingChanged = true;
        }

        if (input is null) return new(true, false, default, inputError ?? "等待注册动作清单");

        bool left = HandConnected(ETrackedControllerRole.LeftHand);
        bool right = HandConnected(ETrackedControllerRole.RightHand);
        bool handLost = leftConnected && !left || rightConnected && !right;
        leftConnected = left;
        rightConnected = right;

        activeSets[0].nPriority = settings.ExperimentalVrInputOverride ? OpenVR.k_nActionSetOverlayGlobalPriorityMin : 0;
        var error = input.UpdateActionState(activeSets, SetSize);
        if (error != EVRInputError.None)
            return new(true, false, default, $"动作更新失败（{error}）");

        if (bindingChanged || now >= nextBindingCheck)
        {
            nextBindingCheck = now + TimeSpan.FromSeconds(1);
            chordBound = IsBound(chord);
            firstBound = IsBound(first);
            secondBound = IsBound(second);
        }

        bool preferChord = settings.VrBinding == VrBindingMode.Chord;
        var chordState = preferChord ? Read(chord) : default;
        bool bindingsStable = !handLost && !bindingChanged;
        if (preferChord && chordState.Available && !usingFallback)
        {
            return new(true, true, chordState with { Available = bindingsStable }, "PushToTalk " + Label(chordState, chordBound));
        }
        var a = Read(first);
        var b = Read(second);
        bool bothAvailable = a.Available && b.Available;
        bool bothBound = (a.Available || firstBound) && (b.Available || secondBound);
        if (preferChord && !bothAvailable)
        {
            usingFallback = false;
            return new(true, chordBound, default, "PushToTalk " + Label(default, chordBound));
        }

        // Some Touch emulations report an inactive chord even when both component actions are live.
        // Keep the selected source through the gesture, until both component buttons are released.
        usingFallback = preferChord && (a.Held || b.Held);
        string mode = preferChord ? "双动作备用 · " : "";
        return new(true, bothBound, TriggerState.Pair(bothAvailable && bindingsStable, a.Held, b.Held),
            $"{mode}A {Label(a, firstBound)} · B {Label(b, secondBound)}");
    }

    private static string Label(TriggerState condition, bool bound)
    {
        if (!condition.Available) return bound ? "已绑定 · 不可用" : "未报告绑定";
        return condition.Held ? "已绑定 · 按住" : "已绑定 · 松开";
    }

    private bool IsBound(ulong action)
    {
        uint count = 0;
        return input!.GetActionBindingInfo(action, bindings, BindingSize, ref count) == EVRInputError.None && count > 0;
    }

    private bool HandConnected(ETrackedControllerRole hand)
    {
        uint index = system!.GetTrackedDeviceIndexForControllerRole(hand);
        return index != OpenVR.k_unTrackedDeviceIndexInvalid && system.IsTrackedDeviceConnected(index);
    }

    private TriggerState Read(ulong action)
    {
        var data = new InputDigitalActionData_t();
        if (input!.GetDigitalActionData(action, ref data, DigitalSize, OpenVR.k_ulInvalidInputValueHandle) != EVRInputError.None || !data.bActive)
            return default;
        if (data.activeOrigin != OpenVR.k_ulInvalidInputValueHandle)
        {
            var origin = new InputOriginInfo_t();
            if (input.GetOriginTrackedDeviceInfo(data.activeOrigin, ref origin, OriginSize) == EVRInputError.None &&
                origin.trackedDeviceIndex != OpenVR.k_unTrackedDeviceIndexInvalid && !system!.IsTrackedDeviceConnected(origin.trackedDeviceIndex))
                return default;
        }
        return TriggerState.Digital(data.bState);
    }

    private void Connect(TimeSpan now)
    {
        nextConnect = now + TimeSpan.FromSeconds(2);
        // Keep the desktop-only workflow available without launching SteamVR from the tray.
        using var current = Process.GetCurrentProcess();
        var candidates = Process.GetProcessesByName("vrserver");
        bool foundInSession = false;
        ProcessExitHandle? handle = null;
        try
        {
            foreach (var candidate in candidates)
            {
                if (candidate.SessionId != current.SessionId) continue;
                foundInSession = true;
                handle ??= ProcessExitHandle.TryOpen(candidate.Id, "SteamVR", ignoreAccessDenied: true);
            }
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
        if (!foundInSession) { handle?.Dispose(); return; }
        server = handle;
        var error = EVRInitError.None;
        system = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Overlay);
        if (error != EVRInitError.None || system is null)
        {
            Disconnect(now);
            ReportConnectError($"等待 SteamVR（{error}）");
            return;
        }
        log("SteamVR 已连接。");
        lastConnectError = null;
        nextSetup = now;
    }

    private void ReportConnectError(string message)
    {
        if (lastConnectError != message) log(message);
        lastConnectError = message;
    }

    private void SetupInput(TimeSpan now)
    {
        nextSetup = now + TimeSpan.FromSeconds(2);
        var applications = OpenVR.Applications;
        var vrInput = OpenVR.Input;
        if (applications is null || vrInput is null)
        {
            SetInputError("SteamVR 输入接口不可用，请更新 SteamVR。");
            return;
        }
        string folder = AppContext.BaseDirectory;
        var registered = applications.AddApplicationManifest(Path.Combine(folder, "VRCVoiceTranslator.vrmanifest"), false);
        if (registered != EVRApplicationError.None)
        {
            SetInputError($"SteamVR 应用清单注册失败（{registered}）");
            return;
        }
        uint pid = (uint)Environment.ProcessId;
        if (applications.GetApplicationProcessId(AppKey) != pid)
        {
            var identified = applications.IdentifyApplication(pid, AppKey);
            if (identified != EVRApplicationError.None)
            {
                SetInputError($"SteamVR 应用标识失败（{identified}）");
                return;
            }
        }

        var error = vrInput.SetActionManifestPath(Path.Combine(folder, "vrc_actions.json"));
        if (error == EVRInputError.None) error = vrInput.GetActionSetHandle(ActionSet, ref activeSets[0].ulActionSet);
        if (error == EVRInputError.None) error = vrInput.GetActionHandle(ActionSet + "/in/PushToTalk", ref chord);
        if (error == EVRInputError.None) error = vrInput.GetActionHandle(ActionSet + "/in/PushToTalkA", ref first);
        if (error == EVRInputError.None) error = vrInput.GetActionHandle(ActionSet + "/in/PushToTalkB", ref second);
        if (error != EVRInputError.None)
        {
            SetInputError($"SteamVR 动作清单加载失败（{error}）");
            return;
        }
        inputError = null;
        input = vrInput;
        nextBindingCheck = now;
        log("SteamVR 动作清单已加载。");
    }

    private void SetInputError(string message)
    {
        if (inputError != message) log(message);
        inputError = message;
    }

    public void OpenBindings()
    {
        if (input is null) throw new ServiceException("请先启动 SteamVR，等待动作清单加载。");
        var error = input.OpenBindingUI(AppKey, activeSets[0].ulActionSet, OpenVR.k_ulInvalidInputValueHandle, true);
        if (error != EVRInputError.None) throw new ServiceException($"无法打开 SteamVR 绑定界面（{error}）。");
    }

    public void Show(string text, SubtitleSettings settings)
    {
        if (system is null) throw new ServiceException("请先连接 SteamVR 再显示头显字幕。");
        overlay ??= new SubtitleOverlay(system, OpenVR.Overlay ?? throw new ServiceException("SteamVR Overlay 接口不可用。"), AppKey);
        overlay.Show(text, settings);
    }

    public void Hide() => overlay?.Hide();

    public void ResetOverlay()
    {
        var current = overlay;
        overlay = null;
        current?.Destroy(disconnecting: false);
    }

    private void Disconnect(TimeSpan now)
    {
        bool wasConnected = system is not null;
        overlay?.Dispose();
        overlay = null;
        input = null;
        system = null;
        leftConnected = rightConnected = false;
        usingFallback = false;
        if (wasConnected)
        {
            OpenVR.Shutdown();
            log("SteamVR 已断开。");
        }
        server?.Dispose();
        server = null;
        nextConnect = now + TimeSpan.FromSeconds(2);
    }

    public void Dispose() => Disconnect(TimeSpan.Zero);
}
