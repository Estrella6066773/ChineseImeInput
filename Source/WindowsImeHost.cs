using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityStubs.Internal;
using Verse;

namespace ChineseImeInput
{
    /// <summary>
    /// 文本框有键盘焦点时保留 Windows 输入法，并把提交的汉字送进游戏原来的文本输入。
    ///
    /// 联动关系：<see cref="Patch_SyncImeBeforePoll"/> 挂在 InputSystem.BeginFrame 之后。
    /// 宿主在 glfw 轮询之前调用 BeginFrame。焦点读 UnityStubs.Internal.TextFocus.CapturesText，
    /// 它看的是上一帧 GUI 留下的 GUIUtility.keyboardControl。
    /// 本类不替换窗口过程。glfw 自己的窗口过程继续收消息，汉字仍走原来的 OnChar。
    /// 线程钩子只在窗口过程之前读出输入法已经提交的字符串。glfw 若已经调用
    /// InputSystem.QueueTextInput，<see cref="Patch_SkipDuplicateImeChar"/> 会把这份结果划掉。
    /// 下一帧 BeginFrame 只把 glfw 没送出的剩余汉字再交给 QueueTextInput。
    /// 宿主的 ImguiDispatch 订阅 Keyboard.onTextInput，DispatchKeyboard 做成 keyDown，
    /// 文本框的 ApplyTextKey 按 Event.character 插入。
    ///
    /// 注意：只处理 Windows。ImmAssociateContext 返回的上下文不能交给 ImmReleaseContext，
    /// 否则上下文会被释放，文本框里再开不了输入法。组字还没结束时不要拆开输入法，
    /// 否则这一次提交会落空。钩子委托必须留在静态字段里，被回收后线程钩子会把游戏打崩。
    /// </summary>
    public static class WindowsImeHost
    {
        const int WhCallWndProc = 4;
        const int WmImeStartComposition = 0x010D;
        const int WmImeEndComposition = 0x010E;
        const int WmImeComposition = 0x010F;
        const int WmImeChar = 0x0286;
        const int WmChar = 0x0102;
        const int GcsResultStr = 0x0800;
        const int GcsResultReadStr = 0x0200;
        const int NiCompositionStr = 0x0015;
        const int CpsCancel = 0x0004;
        const int CfsPoint = 0x0002;
        const int CfsForcePosition = 0x0020;
        const int CfsCandidatePos = 0x0040;

        const string LogPrefix = "[1.7临时中文输入法] ";

        static readonly HookProc CallWndProcHolder = CallWndProc;

        static PropertyInfo runtimeCurrent;
        static PropertyInfo appProperty;
        static PropertyInfo windowProperty;
        static PropertyInfo nativeHandleProperty;
        static bool reflectionFailed;

        static IntPtr hookHandle;
        static IntPtr gameWindow;
        static IntPtr savedContext;
        static bool detached;
        static bool textWanted;
        static bool composing;
        static bool loggedHook;
        static bool loggedFailure;
        static bool loggedDelivery;

        static string pendingResult;
        static int pendingIndex;

        /// <summary>
        /// 在本帧按键进入游戏之前切换输入法，并补上上一帧 glfw 没有送进文本框的汉字。
        /// </summary>
        public static void SyncBeforePoll()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            try
            {
                FlushPendingResult();
                EnsureHook();

                IntPtr hwnd = TryGetGameWindow();
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                if (hwnd != gameWindow)
                {
                    savedContext = IntPtr.Zero;
                    detached = false;
                    composing = false;
                    gameWindow = hwnd;
                }

                bool wantText = TextFocus.CapturesText || composing;
                if (wantText)
                {
                    AttachIme(hwnd);
                    if (!textWanted)
                    {
                        PositionIme(hwnd);
                    }
                }
                else
                {
                    DetachIme(hwnd);
                }

                textWanted = wantText;
            }
            catch (Exception exception)
            {
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    Log.Error(LogPrefix + "切换输入法失败：" + exception);
                }
            }
        }

        /// <summary>
        /// glfw 已经把这个字送进 QueueTextInput 时，从待写入的输入法结果里划掉它。
        /// 对不上的字符照常进入游戏，英文按键不受影响。
        /// </summary>
        public static void ConsumeIfDeliveredByGlfw(char character)
        {
            if (pendingResult == null || pendingIndex >= pendingResult.Length)
            {
                return;
            }

            if (pendingResult[pendingIndex] != character)
            {
                return;
            }

            pendingIndex++;
            if (pendingIndex >= pendingResult.Length)
            {
                pendingResult = null;
                pendingIndex = 0;
            }
        }

        static void FlushPendingResult()
        {
            if (pendingResult == null || pendingIndex >= pendingResult.Length)
            {
                pendingResult = null;
                pendingIndex = 0;
                return;
            }

            string leftover = pendingResult.Substring(pendingIndex);
            pendingResult = null;
            pendingIndex = 0;
            for (int i = 0; i < leftover.Length; i++)
            {
                InputSystem.QueueTextInput(leftover[i]);
            }

            if (!loggedDelivery)
            {
                loggedDelivery = true;
                Log.Message(LogPrefix + "已把输入法提交的文字送入文本框。");
            }
        }

        static void NoteCommitted(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (pendingResult != null && pendingIndex < pendingResult.Length)
            {
                return;
            }

            pendingResult = text;
            pendingIndex = 0;
        }

        static void AttachIme(IntPtr hwnd)
        {
            if (!detached)
            {
                return;
            }

            if (savedContext != IntPtr.Zero)
            {
                ImmAssociateContext(hwnd, savedContext);
            }

            detached = false;
        }

        static void DetachIme(IntPtr hwnd)
        {
            if (detached || composing)
            {
                return;
            }

            CancelComposition(hwnd);
            IntPtr previous = ImmAssociateContext(hwnd, IntPtr.Zero);
            if (previous != IntPtr.Zero)
            {
                savedContext = previous;
            }

            detached = true;
        }

        static void CancelComposition(IntPtr hwnd)
        {
            IntPtr context = ImmGetContext(hwnd);
            if (context == IntPtr.Zero)
            {
                return;
            }

            try
            {
                ImmNotifyIME(context, NiCompositionStr, CpsCancel, 0);
            }
            finally
            {
                ImmReleaseContext(hwnd, context);
            }
        }

        static void PositionIme(IntPtr hwnd)
        {
            IntPtr context = ImmGetContext(hwnd);
            if (context == IntPtr.Zero)
            {
                return;
            }

            try
            {
                Point point;
                if (!GetCursorPos(out point) || !ScreenToClient(hwnd, ref point))
                {
                    return;
                }

                CompositionForm composition = new CompositionForm
                {
                    dwStyle = CfsPoint | CfsForcePosition,
                    ptCurrentPos = point
                };
                ImmSetCompositionWindow(context, ref composition);

                CandidateForm candidate = new CandidateForm
                {
                    dwIndex = 0,
                    dwStyle = CfsCandidatePos | CfsForcePosition,
                    ptCurrentPos = point
                };
                ImmSetCandidateWindow(context, ref candidate);
            }
            finally
            {
                ImmReleaseContext(hwnd, context);
            }
        }

        static void EnsureHook()
        {
            if (hookHandle != IntPtr.Zero)
            {
                return;
            }

            hookHandle = SetWindowsHookEx(WhCallWndProc, CallWndProcHolder, IntPtr.Zero, GetCurrentThreadId());
            if (hookHandle == IntPtr.Zero)
            {
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    Log.Error(LogPrefix + "没能挂上输入法监听，错误码 " + Marshal.GetLastWin32Error() + "。");
                }

                return;
            }

            if (!loggedHook)
            {
                loggedHook = true;
                Log.Message(LogPrefix + "文本框获得焦点时会接收输入法提交的汉字。");
            }
        }

        static IntPtr CallWndProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && lParam != IntPtr.Zero && (textWanted || composing))
            {
                try
                {
                    CwpStruct info = Marshal.PtrToStructure<CwpStruct>(lParam);
                    if (gameWindow == IntPtr.Zero || info.hwnd == gameWindow)
                    {
                        ObserveImeMessage(info);
                    }
                }
                catch (Exception exception)
                {
                    if (!loggedFailure)
                    {
                        loggedFailure = true;
                        Log.Error(LogPrefix + "读取输入法结果失败：" + exception);
                    }
                }
            }

            return CallNextHookEx(hookHandle, code, wParam, lParam);
        }

        static void ObserveImeMessage(CwpStruct info)
        {
            if (info.message == WmImeStartComposition)
            {
                composing = true;
                PositionIme(info.hwnd);
                return;
            }

            if (info.message == WmImeEndComposition)
            {
                composing = false;
                return;
            }

            if (info.message == WmImeComposition)
            {
                long bits = info.lParam.ToInt64();
                if ((bits & GcsResultStr) == 0 && (bits & GcsResultReadStr) == 0)
                {
                    return;
                }

                NoteCommitted(ReadResultString(info.hwnd));
                return;
            }

            if (info.message != WmImeChar && info.message != WmChar)
            {
                return;
            }

            char character = (char)(info.wParam.ToInt64() & 0xFFFF);
            if (character < 128 || char.IsSurrogate(character))
            {
                return;
            }

            NoteCommitted(character.ToString());
        }

        static string ReadResultString(IntPtr hwnd)
        {
            IntPtr context = ImmGetContext(hwnd);
            if (context == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                string result = ReadComposition(context, GcsResultStr);
                if (string.IsNullOrEmpty(result))
                {
                    result = ReadComposition(context, GcsResultReadStr);
                }

                return result;
            }
            finally
            {
                ImmReleaseContext(hwnd, context);
            }
        }

        static string ReadComposition(IntPtr context, int index)
        {
            int byteCount = ImmGetCompositionString(context, index, null, 0);
            if (byteCount <= 0)
            {
                return null;
            }

            byte[] buffer = new byte[byteCount];
            if (ImmGetCompositionString(context, index, buffer, byteCount) <= 0)
            {
                return null;
            }

            return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }

        static IntPtr TryGetGameWindow()
        {
            IntPtr glfwWindow = TryGetGlfwWindowPointer();
            if (glfwWindow == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            return glfwGetWin32Window(glfwWindow);
        }

        static IntPtr TryGetGlfwWindowPointer()
        {
            if (reflectionFailed)
            {
                return IntPtr.Zero;
            }

            if (runtimeCurrent == null)
            {
                Type runtimeType = AccessTools.TypeByName("Waylith.UnityCompat.UnityRuntime");
                runtimeCurrent = runtimeType == null
                    ? null
                    : runtimeType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                if (runtimeCurrent == null)
                {
                    reflectionFailed = true;
                    if (!loggedFailure)
                    {
                        loggedFailure = true;
                        Log.Error(LogPrefix + "找不到 Waylith.UnityCompat.UnityRuntime，输入法补丁没有挂上。");
                    }

                    return IntPtr.Zero;
                }
            }

            object runtime = runtimeCurrent.GetValue(null);
            if (runtime == null)
            {
                return IntPtr.Zero;
            }

            if (appProperty == null)
            {
                appProperty = runtime.GetType().GetProperty("App", BindingFlags.Public | BindingFlags.Instance);
            }

            object app = appProperty == null ? null : appProperty.GetValue(runtime);
            if (app == null)
            {
                return IntPtr.Zero;
            }

            if (windowProperty == null)
            {
                windowProperty = app.GetType().GetProperty("Window", BindingFlags.Public | BindingFlags.Instance);
            }

            object window = windowProperty == null ? null : windowProperty.GetValue(app);
            if (window == null)
            {
                return IntPtr.Zero;
            }

            if (nativeHandleProperty == null)
            {
                nativeHandleProperty = window.GetType().GetProperty("NativeHandle", BindingFlags.Public | BindingFlags.Instance);
            }

            object handle = nativeHandleProperty == null ? null : nativeHandleProperty.GetValue(window);
            if (handle is IntPtr pointer)
            {
                return pointer;
            }

            return IntPtr.Zero;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        struct CwpStruct
        {
            [FieldOffset(0)]
            public IntPtr lParam;

            [FieldOffset(8)]
            public IntPtr wParam;

            [FieldOffset(16)]
            public uint message;

            [FieldOffset(24)]
            public IntPtr hwnd;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Point
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Rect
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CompositionForm
        {
            public int dwStyle;
            public Point ptCurrentPos;
            public Rect rcArea;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CandidateForm
        {
            public int dwIndex;
            public int dwStyle;
            public Point ptCurrentPos;
            public Rect rcArea;
        }

        [DllImport("glfw3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "glfwGetWin32Window")]
        static extern IntPtr glfwGetWin32Window(IntPtr window);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        static extern bool ScreenToClient(IntPtr hwnd, ref Point point);

        [DllImport("imm32.dll")]
        static extern IntPtr ImmGetContext(IntPtr hwnd);

        [DllImport("imm32.dll")]
        static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr context);

        [DllImport("imm32.dll")]
        static extern IntPtr ImmAssociateContext(IntPtr hwnd, IntPtr context);

        [DllImport("imm32.dll")]
        static extern bool ImmNotifyIME(IntPtr context, int action, int index, int value);

        [DllImport("imm32.dll")]
        static extern bool ImmSetCompositionWindow(IntPtr context, ref CompositionForm form);

        [DllImport("imm32.dll")]
        static extern bool ImmSetCandidateWindow(IntPtr context, ref CandidateForm form);

        [DllImport("imm32.dll", EntryPoint = "ImmGetCompositionStringW")]
        static extern int ImmGetCompositionString(IntPtr context, int index, byte[] buffer, int bufferLength);
    }

    /// <summary>
    /// 挂在输入轮询之前。宿主每个绘制帧调用一次 BeginFrame，然后才把这一帧的按键收进来。
    /// </summary>
    [HarmonyPatch(typeof(InputSystem), nameof(InputSystem.BeginFrame))]
    public static class Patch_SyncImeBeforePoll
    {
        public static void Postfix()
        {
            WindowsImeHost.SyncBeforePoll();
        }
    }

    /// <summary>
    /// 输入法结果已经记下来时，glfw 再投递同一个字就从待写入串里划掉。
    /// 前缀不拦截原方法。下一帧只补 glfw 没送出的剩余汉字。
    /// </summary>
    [HarmonyPatch(typeof(InputSystem), nameof(InputSystem.QueueTextInput))]
    public static class Patch_SkipDuplicateImeChar
    {
        public static void Prefix(char character)
        {
            WindowsImeHost.ConsumeIfDeliveredByGlfw(character);
        }
    }
}
