using System.Reflection;
using HarmonyLib;
using Verse;

namespace ChineseImeInput
{
    /// <summary>
    /// 模组入口。游戏在 LoadedModManager.CreateModClasses 里构造本类，构造函数用 PatchAll 挂上补丁。
    ///
    /// 联动关系：补丁都在 <see cref="WindowsImeHost"/>。一个挂在 UnityEngine.InputSystem.InputSystem.BeginFrame，
    /// 这是 RimWorldWaylith.Bootstrap.OnPrePollEvents 在 glfw 收集本帧按键之前调用的。另一个挂在
    /// InputSystem.QueueTextInput，用来确认游戏有没有已经收下同一个汉字，避免写两遍。
    ///
    /// 注意：必须在 CreateModClasses 里挂补丁，不能等 StaticConstructorOnStartup。主菜单的取名框
    /// 在进入存档之前就会出现。HarmonyId 与 About.xml 的 packageId 相同。
    /// </summary>
    public class ChineseImeInputMod : Mod
    {
        public const string HarmonyId = "Estrella6066.chineseImeInput";

        public ChineseImeInputMod(ModContentPack content) : base(content)
        {
            Harmony harmony = new Harmony(HarmonyId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
    }
}
